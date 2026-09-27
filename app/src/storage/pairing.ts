import { generateKeyPair, type KeyPair, keyPairFromSecret } from '@reach/protocol';
import * as SecureStore from 'expo-secure-store';
import { decodeBase64Url, encodeBase64Url } from '../core/base64url';

/** The paired PC, as remembered in the Keychain. */
export interface PairedPc {
  hosts: string[];
  port: number;
  /** The PC's static public key, base64url. */
  pcKey: string;
  pcName: string;
}

const PHONE_KEY = 'reach.phoneKey';
const PAIRED_PC = 'reach.pc';
// Spec §3.2: readable only while the phone is unlocked, never restored to another device.
const OPTIONS: SecureStore.SecureStoreOptions = { keychainAccessible: SecureStore.WHEN_UNLOCKED_THIS_DEVICE_ONLY };

/**
 * The phone's static key pair, created on first use and kept for good: rescanning a QR code
 * pairs the same identity again rather than creating a new one (spec §3.3). A damaged entry is
 * replaced, which means pairing again.
 */
export async function loadPhoneKey(): Promise<KeyPair> {
  const secret = decodeBase64Url((await SecureStore.getItemAsync(PHONE_KEY, OPTIONS)) ?? '');
  if (secret?.length === 32) return keyPairFromSecret(secret);
  const pair = generateKeyPair();
  await SecureStore.setItemAsync(PHONE_KEY, encodeBase64Url(pair.secretKey), OPTIONS);
  return pair;
}

/** Null if nothing is paired, or the stored entry is unreadable. */
export async function loadPairedPc(): Promise<PairedPc | null> {
  const text = await SecureStore.getItemAsync(PAIRED_PC, OPTIONS);
  if (text === null) return null;
  try {
    const pc = JSON.parse(text) as PairedPc;
    const valid =
      Array.isArray(pc.hosts) &&
      pc.hosts.length > 0 &&
      pc.hosts.every((h) => typeof h === 'string') &&
      Number.isInteger(pc.port) &&
      decodeBase64Url(pc.pcKey)?.length === 32 &&
      typeof pc.pcName === 'string';
    return valid ? { hosts: pc.hosts, port: pc.port, pcKey: pc.pcKey, pcName: pc.pcName } : null;
  } catch {
    return null;
  }
}

export async function savePairedPc(pc: PairedPc): Promise<void> {
  await SecureStore.setItemAsync(PAIRED_PC, JSON.stringify(pc), OPTIONS);
}
