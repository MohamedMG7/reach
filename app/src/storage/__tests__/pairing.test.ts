import * as SecureStore from 'expo-secure-store';
import { loadPairedPc, loadPhoneKey, type PairedPc, savePairedPc } from '../pairing';

jest.mock('expo-secure-store', () => {
  const items = new Map<string, string>();
  return {
    WHEN_UNLOCKED_THIS_DEVICE_ONLY: 'WHEN_UNLOCKED_THIS_DEVICE_ONLY',
    getItemAsync: jest.fn(async (key: string) => items.get(key) ?? null),
    setItemAsync: jest.fn(async (key: string, value: string) => void items.set(key, value)),
    __items: items,
  };
});

const items = (SecureStore as unknown as { __items: Map<string, string> }).__items;
const PC: PairedPc = { hosts: ['192.168.1.20'], port: 47800, pcKey: 'A'.repeat(43), pcName: 'DESKTOP' };

beforeEach(() => {
  items.clear();
  jest.clearAllMocks();
});

it('creates the phone key once and returns the same key afterwards', async () => {
  const first = await loadPhoneKey();
  const second = await loadPhoneKey();
  expect(second.publicKey).toEqual(first.publicKey);
  expect(SecureStore.setItemAsync).toHaveBeenCalledTimes(1);
});

it('stores secrets only while unlocked, on this device only', async () => {
  await loadPhoneKey();
  await savePairedPc(PC);
  for (const call of (SecureStore.setItemAsync as jest.Mock).mock.calls) {
    expect(call[2]).toEqual({ keychainAccessible: 'WHEN_UNLOCKED_THIS_DEVICE_ONLY' });
  }
});

it('replaces a damaged phone key', async () => {
  items.set('reach.phoneKey', 'not-a-key');
  const pair = await loadPhoneKey();
  expect(pair.secretKey).toHaveLength(32);
  expect(items.get('reach.phoneKey')).not.toBe('not-a-key');
});

it('saves and loads the paired PC', async () => {
  expect(await loadPairedPc()).toBeNull();
  await savePairedPc(PC);
  expect(await loadPairedPc()).toEqual(PC);
});

it.each(['not json', '{}', JSON.stringify({ ...PC, hosts: [] }), JSON.stringify({ ...PC, pcKey: 'short' })])(
  'treats a damaged PC entry %p as not paired',
  async (text) => {
    items.set('reach.pc', text);
    expect(await loadPairedPc()).toBeNull();
  },
);
