import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { x25519 } from '@noble/curves/ed25519.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { hmac } from '@noble/hashes/hmac.js';
import { CipherState, IKHandshake, generateKeyPair, keyPairFromSecret, MAX_MESSAGE_LEN, PROTOCOL_NAME } from '../src/noise';

const hex = (s: string) => Uint8Array.from(Buffer.from(s, 'hex'));
const toHex = (b: Uint8Array) => Buffer.from(b).toString('hex');

interface Vector {
  init_prologue: string;
  init_static: string;
  init_ephemeral: string;
  init_remote_static: string;
  resp_prologue: string;
  resp_static: string;
  resp_ephemeral: string;
  handshake_hash: string;
  messages: { payload: string; ciphertext: string }[];
}

export function replayVector(vector: Vector) {
  const initiator = new IKHandshake({
    initiator: true,
    prologue: hex(vector.init_prologue),
    s: keyPairFromSecret(hex(vector.init_static)),
    e: keyPairFromSecret(hex(vector.init_ephemeral)),
    rs: hex(vector.init_remote_static),
  });
  const responder = new IKHandshake({
    initiator: false,
    prologue: hex(vector.resp_prologue),
    s: keyPairFromSecret(hex(vector.resp_static)),
    e: keyPairFromSecret(hex(vector.resp_ephemeral)),
  });
  const [m0, m1, ...transportMessages] = vector.messages;

  const c0 = initiator.writeMessage(hex(m0.payload));
  expect(toHex(c0)).toBe(m0.ciphertext);
  expect(toHex(responder.readMessage(c0))).toBe(m0.payload);
  expect(toHex(responder.remoteStaticKey!)).toBe(toHex(keyPairFromSecret(hex(vector.init_static)).publicKey));

  const c1 = responder.writeMessage(hex(m1.payload));
  expect(toHex(c1)).toBe(m1.ciphertext);
  expect(toHex(initiator.readMessage(c1))).toBe(m1.payload);

  expect(toHex(initiator.handshakeHash)).toBe(vector.handshake_hash);
  expect(toHex(responder.handshakeHash)).toBe(vector.handshake_hash);

  const it = initiator.split();
  const rt = responder.split();
  transportMessages.forEach((m, i) => {
    const [sender, receiver] = i % 2 === 0 ? [it, rt] : [rt, it];
    const c = sender.encrypt(hex(m.payload));
    expect(toHex(c)).toBe(m.ciphertext);
    expect(toHex(receiver.decrypt(c))).toBe(m.payload);
  });
}

function loadVectors(file: string): Vector[] {
  return JSON.parse(readFileSync(new URL(`../../test-vectors/${file}`, import.meta.url), 'utf8')).vectors;
}

describe('Noise IK vectors', () => {
  it('matches the official cacophony vector', () => {
    loadVectors('noise-ik.json').forEach(replayVector);
  });

  it('matches the Reach cross-language vector', () => {
    loadVectors('reach-ik.json').forEach(replayVector);
  });
});

/**
 * A correctly encrypted message 1 whose ephemeral key is all zeros: es is then zero on the
 * responder's side, so the sender knows it without any secret and can build a valid tag.
 */
function zeroEphemeralFirstMessage(prologue: Uint8Array, responderStatic: Uint8Array): Uint8Array {
  const sender = generateKeyPair();
  const cat = (...parts: Uint8Array[]) => Uint8Array.from(parts.flatMap((p) => [...p]));
  let h = new Uint8Array(32);
  h.set(new TextEncoder().encode(PROTOCOL_NAME));
  let ck = h.slice();
  const cipher = new CipherState();
  const mixHash = (data: Uint8Array) => (h = sha256(cat(h, data)));
  const mixKey = (ikm: Uint8Array) => {
    const temp = hmac(sha256, ck, ikm);
    ck = hmac(sha256, temp, Uint8Array.of(1));
    cipher.initializeKey(hmac(sha256, temp, cat(ck, Uint8Array.of(2))));
  };
  const encryptAndHash = (plaintext: Uint8Array) => {
    const c = cipher.encryptWithAd(h, plaintext);
    mixHash(c);
    return c;
  };
  mixHash(prologue);
  mixHash(responderStatic);
  const re = new Uint8Array(32);
  mixHash(re);
  mixKey(new Uint8Array(32));
  const encryptedStatic = encryptAndHash(sender.publicKey);
  mixKey(x25519.getSharedSecret(sender.secretKey, responderStatic));
  const body = encryptAndHash(new TextEncoder().encode('{}'));
  return cat(re, encryptedStatic, body);
}

function pair() {
  const pc = generateKeyPair();
  const phone = generateKeyPair();
  const prologue = new TextEncoder().encode('reach/1');
  const initiator = new IKHandshake({ initiator: true, prologue, s: phone, rs: pc.publicKey });
  const responder = new IKHandshake({ initiator: false, prologue, s: pc });
  responder.readMessage(initiator.writeMessage(new Uint8Array(0)));
  initiator.readMessage(responder.writeMessage(new Uint8Array(0)));
  return { initiator: initiator.split(), responder: responder.split() };
}

describe('Noise IK failure cases', () => {
  it('rejects a tampered transport message', () => {
    const { initiator, responder } = pair();
    const c = initiator.encrypt(new TextEncoder().encode('hi'));
    c[0] ^= 1;
    expect(() => responder.decrypt(c)).toThrow();
  });

  it('rejects a replayed transport message', () => {
    const { initiator, responder } = pair();
    const c = initiator.encrypt(new TextEncoder().encode('hi'));
    responder.decrypt(c);
    expect(() => responder.decrypt(c)).toThrow();
  });

  it('fails the handshake when the initiator has the wrong responder key', () => {
    const prologue = new TextEncoder().encode('reach/1');
    const initiator = new IKHandshake({ initiator: true, prologue, s: generateKeyPair(), rs: generateKeyPair().publicKey });
    const responder = new IKHandshake({ initiator: false, prologue, s: generateKeyPair() });
    expect(() => responder.readMessage(initiator.writeMessage(new Uint8Array(0)))).toThrow();
  });

  it('fails the handshake when the prologues differ', () => {
    const pc = generateKeyPair();
    const initiator = new IKHandshake({ initiator: true, prologue: new TextEncoder().encode('reach/1'), s: generateKeyPair(), rs: pc.publicKey });
    const responder = new IKHandshake({ initiator: false, prologue: new TextEncoder().encode('reach/2'), s: pc });
    expect(() => responder.readMessage(initiator.writeMessage(new Uint8Array(0)))).toThrow();
  });

  it('rejects a first message whose ephemeral key is all zeros', () => {
    const pc = generateKeyPair();
    const prologue = new TextEncoder().encode('reach/1');
    const responder = new IKHandshake({ initiator: false, prologue, s: pc });
    expect(() => responder.readMessage(zeroEphemeralFirstMessage(prologue, pc.publicKey))).toThrow('invalid public key');
  });

  it('rejects an empty transport message', () => {
    const { responder } = pair();
    expect(() => responder.decrypt(new Uint8Array(0))).toThrow('message too short');
  });

  it('rejects an oversized incoming transport message before decrypting', () => {
    const { responder } = pair();
    expect(() => responder.decrypt(new Uint8Array(MAX_MESSAGE_LEN + 1))).toThrow('message too long');
  });

  it('rejects a truncated first message', () => {
    const responder = new IKHandshake({ initiator: false, prologue: new Uint8Array(0), s: generateKeyPair() });
    expect(() => responder.readMessage(new Uint8Array(40))).toThrow('message too short');
  });

  it('refuses to encrypt a message that would exceed the Noise limit', () => {
    const { initiator } = pair();
    expect(() => initiator.encrypt(new Uint8Array(MAX_MESSAGE_LEN))).toThrow('message too long');
  });

  it('refuses to write out of turn', () => {
    const responder = new IKHandshake({ initiator: false, prologue: new Uint8Array(0), s: generateKeyPair() });
    expect(() => responder.writeMessage(new Uint8Array(0))).toThrow('out of turn');
  });
});
