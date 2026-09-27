import { getRandomValues } from 'expo-crypto';

// @noble/* reads globalThis.crypto.getRandomValues when it generates keys; Hermes has no Web
// Crypto, so expo-crypto's secure generator stands in (spec §3.1). Imported first by the root layout.
const g = globalThis as { crypto?: { getRandomValues?: unknown } };
g.crypto ??= {};
g.crypto.getRandomValues ??= getRandomValues;
