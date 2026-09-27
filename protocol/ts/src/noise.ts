import { x25519 } from '@noble/curves/ed25519.js';
import { chacha20poly1305 } from '@noble/ciphers/chacha.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { hmac } from '@noble/hashes/hmac.js';

export const PROTOCOL_NAME = 'Noise_IK_25519_ChaChaPoly_SHA256';
/** Largest Noise message (handshake or transport), including the 16-byte tag. */
export const MAX_MESSAGE_LEN = 65535;
const TAG_LEN = 16;
const DH_LEN = 32;
const HASH_LEN = 32;
const MAX_NONCE = 2n ** 64n - 1n;
const EMPTY = new Uint8Array(0);

export interface KeyPair {
  publicKey: Uint8Array;
  secretKey: Uint8Array;
}

export function generateKeyPair(): KeyPair {
  return keyPairFromSecret(x25519.utils.randomSecretKey());
}

export function keyPairFromSecret(secretKey: Uint8Array): KeyPair {
  return { secretKey, publicKey: x25519.getPublicKey(secretKey) };
}

/** X25519. Rejects low-order public keys (all-zero shared secret) so both implementations agree. */
function dh(own: KeyPair, remotePublic: Uint8Array): Uint8Array {
  let shared: Uint8Array;
  try {
    shared = x25519.getSharedSecret(own.secretKey, remotePublic);
  } catch {
    throw new Error('invalid public key');
  }
  if (shared.every((b) => b === 0)) throw new Error('invalid public key');
  return shared;
}

function concat(...parts: Uint8Array[]): Uint8Array {
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let offset = 0;
  for (const p of parts) {
    out.set(p, offset);
    offset += p.length;
  }
  return out;
}

/** Noise HKDF with two outputs: identical to RFC 5869 with salt = ck and empty info. */
function hkdf2(ck: Uint8Array, ikm: Uint8Array): [Uint8Array, Uint8Array] {
  const temp = hmac(sha256, ck, ikm);
  const out1 = hmac(sha256, temp, Uint8Array.of(1));
  const out2 = hmac(sha256, temp, concat(out1, Uint8Array.of(2)));
  return [out1, out2];
}

function nonceBytes(n: bigint): Uint8Array {
  const bytes = new Uint8Array(12);
  new DataView(bytes.buffer).setBigUint64(4, n, true);
  return bytes;
}

export class CipherState {
  private n = 0n;

  constructor(private k: Uint8Array | null = null) {}

  initializeKey(k: Uint8Array): void {
    this.k = k;
    this.n = 0n;
  }

  encryptWithAd(ad: Uint8Array, plaintext: Uint8Array): Uint8Array {
    if (this.k === null) return plaintext;
    if (this.n === MAX_NONCE) throw new Error('nonce exhausted');
    const ciphertext = chacha20poly1305(this.k, nonceBytes(this.n), ad).encrypt(plaintext);
    this.n++;
    return ciphertext;
  }

  /** Throws if authentication fails; the nonce only advances on success. */
  decryptWithAd(ad: Uint8Array, ciphertext: Uint8Array): Uint8Array {
    if (this.k === null) return ciphertext;
    if (this.n === MAX_NONCE) throw new Error('nonce exhausted');
    const plaintext = chacha20poly1305(this.k, nonceBytes(this.n), ad).decrypt(ciphertext);
    this.n++;
    return plaintext;
  }
}

class SymmetricState {
  ck: Uint8Array;
  h: Uint8Array;
  readonly cipher = new CipherState();

  constructor(protocolName: string) {
    const name = new TextEncoder().encode(protocolName);
    if (name.length <= HASH_LEN) {
      this.h = new Uint8Array(HASH_LEN);
      this.h.set(name);
    } else {
      this.h = sha256(name);
    }
    this.ck = this.h.slice();
  }

  mixKey(ikm: Uint8Array): void {
    const [ck, k] = hkdf2(this.ck, ikm);
    this.ck = ck;
    this.cipher.initializeKey(k);
  }

  mixHash(data: Uint8Array): void {
    this.h = sha256(concat(this.h, data));
  }

  encryptAndHash(plaintext: Uint8Array): Uint8Array {
    const ciphertext = this.cipher.encryptWithAd(this.h, plaintext);
    this.mixHash(ciphertext);
    return ciphertext;
  }

  decryptAndHash(ciphertext: Uint8Array): Uint8Array {
    const plaintext = this.cipher.decryptWithAd(this.h, ciphertext);
    this.mixHash(ciphertext);
    return plaintext;
  }

  split(): [CipherState, CipherState] {
    const [k1, k2] = hkdf2(this.ck, EMPTY);
    return [new CipherState(k1), new CipherState(k2)];
  }
}

export interface HandshakeConfig {
  initiator: boolean;
  prologue: Uint8Array;
  /** Our long-term static key pair. */
  s: KeyPair;
  /** Responder's static public key. Required for the initiator. */
  rs?: Uint8Array;
  /** Fixed ephemeral key pair. Only for test vectors; normally generated. */
  e?: KeyPair;
}

/**
 * Noise IK handshake:
 *   <- s
 *   ...
 *   -> e, es, s, ss
 *   <- e, ee, se
 * Any exception leaves the handshake unusable; the caller must drop the connection.
 */
export class IKHandshake {
  private readonly sym = new SymmetricState(PROTOCOL_NAME);
  private e: KeyPair | undefined;
  private re: Uint8Array | undefined;
  private rs: Uint8Array | undefined;
  private step = 0;

  constructor(private readonly cfg: HandshakeConfig) {
    if (cfg.initiator && !cfg.rs) throw new Error('initiator needs the responder static key');
    this.e = cfg.e;
    this.rs = cfg.rs;
    this.sym.mixHash(cfg.prologue);
    this.sym.mixHash(cfg.initiator ? cfg.rs! : cfg.s.publicKey);
  }

  /** The peer's static public key: known up front for the initiator, learned from message 1 by the responder. */
  get remoteStaticKey(): Uint8Array | undefined {
    return this.rs;
  }

  get handshakeHash(): Uint8Array {
    return this.sym.h;
  }

  get isComplete(): boolean {
    return this.step === 2;
  }

  writeMessage(payload: Uint8Array): Uint8Array {
    const { initiator, s } = this.cfg;
    if (this.step === 0 && initiator) {
      const e = (this.e ??= generateKeyPair());
      this.sym.mixHash(e.publicKey);
      this.sym.mixKey(dh(e, this.rs!));
      const encryptedStatic = this.sym.encryptAndHash(s.publicKey);
      this.sym.mixKey(dh(s, this.rs!));
      const body = this.sym.encryptAndHash(payload);
      this.step = 1;
      return checkLength(concat(e.publicKey, encryptedStatic, body));
    }
    if (this.step === 1 && !initiator) {
      const e = (this.e ??= generateKeyPair());
      this.sym.mixHash(e.publicKey);
      this.sym.mixKey(dh(e, this.re!));
      this.sym.mixKey(dh(e, this.rs!));
      const body = this.sym.encryptAndHash(payload);
      this.step = 2;
      return checkLength(concat(e.publicKey, body));
    }
    throw new Error('writeMessage called out of turn');
  }

  readMessage(message: Uint8Array): Uint8Array {
    const { initiator, s } = this.cfg;
    if (message.length > MAX_MESSAGE_LEN) throw new Error('message too long');
    if (this.step === 0 && !initiator) {
      if (message.length < DH_LEN + DH_LEN + TAG_LEN + TAG_LEN) throw new Error('message too short');
      this.re = message.slice(0, DH_LEN);
      this.sym.mixHash(this.re);
      this.sym.mixKey(dh(s, this.re));
      this.rs = this.sym.decryptAndHash(message.slice(DH_LEN, DH_LEN * 2 + TAG_LEN));
      this.sym.mixKey(dh(s, this.rs));
      const payload = this.sym.decryptAndHash(message.slice(DH_LEN * 2 + TAG_LEN));
      this.step = 1;
      return payload;
    }
    if (this.step === 1 && initiator) {
      if (message.length < DH_LEN + TAG_LEN) throw new Error('message too short');
      this.re = message.slice(0, DH_LEN);
      this.sym.mixHash(this.re);
      this.sym.mixKey(dh(this.e!, this.re));
      this.sym.mixKey(dh(s, this.re));
      const payload = this.sym.decryptAndHash(message.slice(DH_LEN));
      this.step = 2;
      return payload;
    }
    throw new Error('readMessage called out of turn');
  }

  split(): Transport {
    if (!this.isComplete) throw new Error('handshake not complete');
    const [c1, c2] = this.sym.split();
    return this.cfg.initiator ? new Transport(c1, c2) : new Transport(c2, c1);
  }
}

function checkLength(message: Uint8Array): Uint8Array {
  if (message.length > MAX_MESSAGE_LEN) throw new Error('message too long');
  return message;
}

/** Post-handshake channel. Messages must be decrypted in the order they were encrypted. */
export class Transport {
  constructor(
    private readonly sender: CipherState,
    private readonly receiver: CipherState,
  ) {}

  encrypt(plaintext: Uint8Array): Uint8Array {
    if (plaintext.length + TAG_LEN > MAX_MESSAGE_LEN) throw new Error('message too long');
    return this.sender.encryptWithAd(EMPTY, plaintext);
  }

  decrypt(ciphertext: Uint8Array): Uint8Array {
    if (ciphertext.length > MAX_MESSAGE_LEN) throw new Error('message too long');
    if (ciphertext.length < TAG_LEN) throw new Error('message too short');
    return this.receiver.decryptWithAd(EMPTY, ciphertext);
  }
}
