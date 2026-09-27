import type { KeyPair, Message } from '@reach/protocol';
import Constants from 'expo-constants';
import * as Device from 'expo-device';
import * as LocalAuthentication from 'expo-local-authentication';
import { createContext, type ReactNode, useCallback, useContext, useEffect, useRef, useState, useSyncExternalStore } from 'react';
import { AppState, Platform } from 'react-native';
import { decodeBase64Url } from '../core/base64url';
import { mustRelock } from '../core/lock';
import type { PairingInfo } from '../core/pairingUri';
import { type ClientState, ReachClient } from '../net/client';
import type { SocketFactory } from '../net/socket';
import { loadPairedPc, loadPhoneKey, type PairedPc, savePairedPc } from '../storage/pairing';

export interface ReachContextValue {
  locked: boolean;
  /** Face ID (or the passcode). Resolves to an error to show, or null on success. */
  unlock(): Promise<string | null>;
  /** The paired PC; undefined until loaded after unlocking, null if none. */
  pc: PairedPc | null | undefined;
  client: ReachClient | null;
  /** Pairs with a scanned code; the connection continues as the app's session (spec §3.3). */
  pair(info: PairingInfo): Promise<boolean>;
}

export const ReachContext = createContext<ReachContextValue | null>(null);

export function useReach(): ReachContextValue {
  const value = useContext(ReachContext);
  if (!value) throw new Error('useReach outside ReachProvider');
  return value;
}

const OFFLINE: ClientState = { status: 'offline', problem: null, pcName: '' };
const noop = () => () => {};

export function useClientState(client: ReachClient | null): ClientState {
  return useSyncExternalStore(client ? (l) => client.onChange(l) : noop, () => client?.state ?? OFFLINE);
}

/** Calls `listener` for every message from the PC while mounted. */
export function usePcMessages(client: ReachClient | null, listener: (message: Message) => void): void {
  const latest = useRef(listener);
  latest.current = listener;
  useEffect(() => client?.onMessage((m) => latest.current(m)), [client]);
}

function unlockError(error: LocalAuthentication.LocalAuthenticationError): string | null {
  switch (error) {
    case 'user_cancel':
    case 'system_cancel':
    case 'app_cancel':
      return null;
    case 'passcode_not_set':
    case 'not_enrolled':
    case 'not_available':
      return Platform.OS === 'ios'
        ? 'Reach needs Face ID or a passcode on this iPhone. Set one up in Settings → Face ID & Passcode.'
        : 'Reach needs a screen lock on this phone: a fingerprint, face unlock, PIN, pattern or passcode. Set one up in Settings → Security.';
    case 'lockout':
      return 'Too many attempts. Lock and unlock your phone, then try again.';
    default:
      return "Couldn't unlock. Try again.";
  }
}

function makeClient(pc: PairedPc, phoneKey: KeyPair, createSocket?: SocketFactory): ReachClient {
  return new ReachClient({
    hosts: pc.hosts,
    port: pc.port,
    pcKey: decodeBase64Url(pc.pcKey)!,
    phoneKey,
    deviceName: Device.deviceName ?? Device.modelName ?? 'Phone',
    appVersion: Constants.expoConfig?.version ?? '1.0.0',
    createSocket,
  });
}

/**
 * Face ID gate, stored pairing and the connection's lifecycle (spec §3.6, §6.6): the client runs
 * only while unlocked and in the foreground; after more than 60 s in the background, Face ID again.
 */
export function ReachProvider({ children, createSocket }: { children: ReactNode; createSocket?: SocketFactory }) {
  const [locked, setLocked] = useState(true);
  const [foreground, setForeground] = useState(AppState.currentState !== 'background');
  const [pc, setPc] = useState<PairedPc | null | undefined>(undefined);
  const [client, setClient] = useState<ReachClient | null>(null);
  const phoneKey = useRef<KeyPair | null>(null);
  const pairCode = useRef<string | undefined>(undefined);
  const backgroundedAt = useRef<number | null>(null);

  useEffect(() => {
    // 'inactive' (Face ID prompt, Control Center) is not leaving the app.
    const subscription = AppState.addEventListener('change', (next) => {
      if (next === 'background') {
        backgroundedAt.current = Date.now();
        setForeground(false);
      } else if (next === 'active') {
        if (mustRelock(backgroundedAt.current, Date.now())) setLocked(true);
        backgroundedAt.current = null;
        setForeground(true);
      }
    });
    return () => subscription.remove();
  }, []);

  useEffect(() => {
    if (!client || locked || !foreground) return;
    client.start(pairCode.current);
    pairCode.current = undefined;
    return () => client.stop();
  }, [client, locked, foreground]);

  const unlock = useCallback(async () => {
    const result = await LocalAuthentication.authenticateAsync({ promptMessage: 'Unlock Reach' });
    if (!result.success) return unlockError(result.error);
    if (!phoneKey.current) {
      // Keys are read from the Keychain only after authentication (spec §3.6).
      phoneKey.current = await loadPhoneKey();
      const stored = await loadPairedPc();
      setPc(stored);
      setClient(stored ? makeClient(stored, phoneKey.current, createSocket) : null);
    }
    setLocked(false);
    return null;
  }, [createSocket]);

  const pair = useCallback(
    (info: PairingInfo) =>
      new Promise<boolean>((resolve) => {
        const previous = client;
        const target: PairedPc = { hosts: info.hosts, port: info.port, pcKey: info.pcKey, pcName: info.pcName };
        const next = makeClient(target, phoneKey.current!, createSocket);
        const unsubscribe = next.onChange(() => {
          const { status, problem, pcName } = next.state;
          // Busy means the PC accepted the code but another phone is using it: paired, and the
          // client keeps checking back until it's this phone's turn.
          if (status === 'connected' || problem === 'busy') {
            unsubscribe();
            const saved = { ...target, pcName: pcName || info.pcName };
            void savePairedPc(saved);
            setPc(saved);
            resolve(true);
          } else if (problem !== null) {
            unsubscribe();
            setClient(previous); // keep using the PC paired before, if any
            resolve(false);
          }
        });
        pairCode.current = info.code;
        setClient(next);
      }),
    [client, createSocket],
  );

  return <ReachContext.Provider value={{ locked, unlock, pc, client, pair }}>{children}</ReachContext.Provider>;
}
