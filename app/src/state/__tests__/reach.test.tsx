import { generateKeyPair } from '@reach/protocol';
import { act, render, waitFor } from '@testing-library/react-native';
import * as LocalAuthentication from 'expo-local-authentication';
import * as SecureStore from 'expo-secure-store';
import { AppState, type AppStateStatus } from 'react-native';
import { encodeBase64Url } from '../../core/base64url';
import { RELOCK_AFTER_MS } from '../../core/lock';
import type { PairingInfo } from '../../core/pairingUri';
import { FakePc } from '../../testing/fakePc';
import { ReachProvider, type ReachContextValue, useReach } from '../reach';

jest.mock('expo-secure-store', () => {
  const items = new Map<string, string>();
  return {
    WHEN_UNLOCKED_THIS_DEVICE_ONLY: 1,
    getItemAsync: jest.fn(async (key: string) => items.get(key) ?? null),
    setItemAsync: jest.fn(async (key: string, value: string) => void items.set(key, value)),
    __items: items,
  };
});
jest.mock('expo-local-authentication', () => ({ authenticateAsync: jest.fn() }));
jest.mock('expo-device', () => ({ deviceName: 'Test iPhone' }));

const items = (SecureStore as unknown as { __items: Map<string, string> }).__items;
const authenticate = LocalAuthentication.authenticateAsync as jest.Mock;

let appState: (state: AppStateStatus) => void;
let pc: FakePc;
let reach: ReachContextValue;

function Probe() {
  reach = useReach();
  return null;
}

const renderApp = () =>
  render(
    <ReachProvider createSocket={pc.createSocket}>
      <Probe />
    </ReachProvider>,
  );

async function unlock(): Promise<string | null> {
  let error: string | null = null;
  await act(async () => {
    error = await reach.unlock();
  });
  return error;
}

/**
 * Starts pairing inside act but awaits the result outside it: act holds effects back until its
 * callback returns, and it's an effect that starts the pairing connection.
 */
async function startPairing(): Promise<boolean> {
  let result!: Promise<boolean>;
  await act(async () => {
    result = reach.pair(scanned());
  });
  return result;
}

const goTo = (state: AppStateStatus) => act(async () => appState(state));

/** A phone key and paired PC already in the Keychain, as after an earlier pairing. */
function storePairing() {
  const phone = generateKeyPair();
  items.set('reach.phoneKey', encodeBase64Url(phone.secretKey));
  items.set('reach.pc', JSON.stringify({ hosts: ['192.168.1.20'], port: 47800, pcKey: encodeBase64Url(pc.key.publicKey), pcName: 'DESKTOP' }));
  pc.paired.add(encodeBase64Url(phone.publicKey));
}

const scanned = (): PairingInfo => ({
  hosts: ['192.168.1.20'],
  port: 47800,
  pcKey: encodeBase64Url(pc.key.publicKey),
  code: 'AAECAwQFBgcICQoLDA0ODw',
  pcName: 'From QR',
});

beforeEach(() => {
  items.clear();
  jest.clearAllMocks();
  pc = new FakePc();
  authenticate.mockResolvedValue({ success: true });
  jest.spyOn(AppState, 'addEventListener').mockImplementation((_type, listener) => {
    appState = listener as (state: AppStateStatus) => void;
    return { remove: jest.fn() } as never;
  });
});

afterEach(() => act(async () => reach?.client?.stop()));

it('starts locked and reads nothing from the Keychain until Face ID succeeds', async () => {
  await renderApp();
  expect(reach.locked).toBe(true);
  expect(reach.pc).toBeUndefined();
  expect(SecureStore.getItemAsync).not.toHaveBeenCalled();

  expect(await unlock()).toBeNull();

  expect(reach.locked).toBe(false);
  expect(reach.pc).toBeNull();
});

it('stays locked and explains when the iPhone has no passcode', async () => {
  authenticate.mockResolvedValue({ success: false, error: 'passcode_not_set' });
  await renderApp();

  expect(await unlock()).toMatch(/passcode/);
  expect(reach.locked).toBe(true);
});

it('connects to the stored PC once unlocked', async () => {
  storePairing();
  await renderApp();

  await unlock();

  await waitFor(() => expect(reach.client?.state.status).toBe('connected'));
  expect(reach.pc?.pcName).toBe('DESKTOP');
});

it('pairs, saves the PC and keeps that connection as the session', async () => {
  pc.pairCodes.add('AAECAwQFBgcICQoLDA0ODw');
  await renderApp();
  await unlock();

  expect(await startPairing()).toBe(true);

  await waitFor(() =>
    expect(reach.pc).toEqual({ hosts: ['192.168.1.20'], port: 47800, pcKey: encodeBase64Url(pc.key.publicKey), pcName: 'DESKTOP' }),
  );
  expect(JSON.parse(items.get('reach.pc')!).pcName).toBe('DESKTOP');
  expect(reach.client?.state.status).toBe('connected');
  expect(pc.sockets).toHaveLength(1);
});

it('pairs while another phone is using the PC, and waits its turn', async () => {
  pc.pairCodes.add('AAECAwQFBgcICQoLDA0ODw');
  pc.busyWith = 'Kitchen Pixel';
  await renderApp();
  await unlock();

  expect(await startPairing()).toBe(true);

  await waitFor(() => expect(reach.pc?.pcKey).toBe(encodeBase64Url(pc.key.publicKey)));
  expect(reach.client?.state.problem).toBe('busy');
});

it('saves nothing when pairing fails', async () => {
  await renderApp();
  await unlock();

  expect(await startPairing()).toBe(false); // the PC has no such code

  await waitFor(() => expect(reach.client).toBeNull());
  expect(reach.pc).toBeNull();
  expect(reach.client).toBeNull();
  expect(items.has('reach.pc')).toBe(false);
});

it('disconnects in the background and asks for Face ID again after more than 60 s', async () => {
  storePairing();
  await renderApp();
  await unlock();
  await waitFor(() => expect(reach.client?.state.status).toBe('connected'));
  const now = jest.spyOn(Date, 'now').mockReturnValue(1_000_000);

  await goTo('background');
  expect(pc.sockets.every((s) => s.closed)).toBe(true);
  expect(reach.client?.state.status).toBe('offline');

  now.mockReturnValue(1_000_000 + RELOCK_AFTER_MS + 1);
  await goTo('active');
  expect(reach.locked).toBe(true);
  expect(pc.sockets).toHaveLength(1); // no connection while locked
  now.mockRestore();
});

it('reconnects without Face ID after a short trip to the background', async () => {
  storePairing();
  await renderApp();
  await unlock();
  await waitFor(() => expect(reach.client?.state.status).toBe('connected'));
  const now = jest.spyOn(Date, 'now').mockReturnValue(1_000_000);

  await goTo('background');
  now.mockReturnValue(1_000_000 + 5_000);
  await goTo('active');
  now.mockRestore();

  expect(reach.locked).toBe(false);
  await waitFor(() => expect(reach.client?.state.status).toBe('connected'));
  expect(pc.sockets).toHaveLength(2);
});

it('ignores the inactive state that the Face ID prompt itself causes', async () => {
  await renderApp();
  await unlock();

  await goTo('inactive');
  await goTo('active');

  expect(reach.locked).toBe(false);
});
