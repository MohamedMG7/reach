import {
  decodeMessage,
  encodeConnectRequest,
  encodeMessage,
  IKHandshake,
  type KeyPair,
  type Message,
  ProtocolError,
  Transport,
} from '@reach/protocol';
import { createWebSocket, type FrameSocket, type SocketFactory } from './socket';

export const PROLOGUE = new TextEncoder().encode('reach/1');
/** Waits between reconnect rounds: 1 s, 2 s, then every 5 s (spec §6.6). */
export const RETRY_DELAYS_MS = [1000, 2000, 5000];
/** One host gets this long to open and finish the handshake before the next is tried. */
export const ATTEMPT_TIMEOUT_MS = 5000;
export const HEARTBEAT_MS = 5000;
export const IDLE_TIMEOUT_MS = 15000;

/** Green / yellow / red status dot (spec §6.1). */
export type Status = 'connected' | 'reconnecting' | 'offline';
/**
 * Why the client is offline: the PC refused this phone's key; no host answered (it keeps
 * retrying); a pairing attempt failed (code expired or used, or PC unreachable); another phone
 * is using the PC (it keeps retrying); or the PC disconnected this phone (it waits for Reconnect).
 */
export type Problem = 'not_paired' | 'unreachable' | 'pair_failed' | 'busy' | 'disconnected';

export interface ClientState {
  status: Status;
  problem: Problem | null;
  /** From the PC's welcome; empty until the first session. */
  pcName: string;
  /** With problem 'busy': the phone that is using the PC. */
  busyWith?: string;
}

export interface ClientOptions {
  hosts: string[];
  port: number;
  pcKey: Uint8Array;
  phoneKey: KeyPair;
  deviceName: string;
  appVersion: string;
  createSocket?: SocketFactory;
}

/**
 * How one connection ended: never opened, closed during the handshake, ran as a session, the PC
 * said another phone is using it, or the PC disconnected this phone.
 */
type Outcome = 'unreachable' | 'rejected' | 'ended' | 'stopped' | 'busy' | 'bye';

/**
 * One phone-to-PC connection (spec §3.4, §6.6): Noise IK handshake, hello, heartbeat, and
 * reconnecting across the PC's hosts while started. Input sent while not connected is dropped.
 */
export class ReachClient {
  private readonly createSocket: SocketFactory;
  private readonly messageListeners = new Set<(message: Message) => void>();
  private readonly stateListeners = new Set<() => void>();
  private current: ClientState = { status: 'offline', problem: null, pcName: '' };
  private generation = 0;
  private live: { socket: FrameSocket; transport: Transport } | null = null;
  private abortAttempt: (() => void) | null = null;
  private cancelWait: (() => void) | null = null;

  constructor(private readonly options: ClientOptions) {
    this.createSocket = options.createSocket ?? createWebSocket;
  }

  get state(): ClientState {
    return this.current;
  }

  /** Starts connecting, and keeps reconnecting until stop(). With a pairing code, one round only. */
  start(pairCode?: string): void {
    this.stop();
    const generation = ++this.generation;
    this.update({ status: 'reconnecting', problem: null });
    void this.run(generation, pairCode);
  }

  /** Closes the connection and stops reconnecting (app in background, re-pairing). */
  stop(): void {
    this.generation++;
    this.abortAttempt?.();
    this.cancelWait?.();
    this.update({ status: 'offline', problem: null });
  }

  /** Sends if connected; otherwise drops the message and returns false (spec §6.6). */
  send(message: Message): boolean {
    if (this.current.status !== 'connected' || !this.live) return false;
    try {
      this.live.socket.send(this.live.transport.encrypt(encodeMessage(message)));
      return true;
    } catch {
      return false;
    }
  }

  onMessage(listener: (message: Message) => void): () => void {
    this.messageListeners.add(listener);
    return () => this.messageListeners.delete(listener);
  }

  onChange(listener: () => void): () => void {
    this.stateListeners.add(listener);
    return () => this.stateListeners.delete(listener);
  }

  private update(change: Partial<ClientState>): void {
    this.current = { ...this.current, ...change };
    for (const listener of this.stateListeners) listener();
  }

  private async run(generation: number, pairCode: string | undefined): Promise<void> {
    let failures = 0;
    while (generation === this.generation) {
      let outcome: Outcome = 'unreachable';
      let reachedPc = false;
      let busyWith = '';
      for (const host of this.options.hosts) {
        outcome = await this.attempt(host, pairCode, (who) => {
          reachedPc = true;
          busyWith = who ?? '';
        });
        if (generation !== this.generation || outcome === 'stopped') return;
        if (outcome !== 'unreachable') break;
      }
      if (reachedPc) pairCode = undefined; // paired: later reconnects are normal sessions
      if (outcome === 'bye') {
        this.generation++;
        this.update({ status: 'offline', problem: 'disconnected' });
        return;
      }
      if (outcome === 'rejected' || (pairCode !== undefined && outcome === 'unreachable')) {
        this.generation++;
        this.update({ status: 'offline', problem: pairCode !== undefined ? 'pair_failed' : 'not_paired' });
        return;
      }
      if (outcome === 'busy') {
        failures = RETRY_DELAYS_MS.length; // check back every 5 s until the other phone leaves
        this.update({ status: 'offline', problem: 'busy', busyWith });
      } else if (outcome === 'ended' && reachedPc) {
        failures = 0;
        this.update({ status: 'reconnecting', problem: null });
      } else {
        failures++;
        this.update({ status: 'offline', problem: 'unreachable' });
      }
      const delay = RETRY_DELAYS_MS[Math.min(Math.max(failures - 1, 0), RETRY_DELAYS_MS.length - 1)];
      await new Promise<void>((resolve) => {
        const timer = setTimeout(resolve, delay);
        this.cancelWait = () => {
          clearTimeout(timer);
          resolve();
        };
      });
      this.cancelWait = null;
    }
  }

  /**
   * One connection to one host, from opening the socket until it fails or its session ends.
   * `onReachedPc` runs when the PC accepted this phone: on welcome, or on busy with who is connected.
   */
  private attempt(host: string, pairCode: string | undefined, onReachedPc: (busyWith?: string) => void): Promise<Outcome> {
    return new Promise((resolve) => {
      const { port, pcKey, phoneKey, deviceName, appVersion } = this.options;
      const socket = this.createSocket(`ws://${host}:${port}/`);
      const handshake = new IKHandshake({ initiator: true, prologue: PROLOGUE, s: phoneKey, rs: pcKey });
      let opened = false;
      let transport: Transport | null = null;
      let lastReceived = Date.now();
      let heartbeat: ReturnType<typeof setInterval> | undefined;

      const finish = (outcome: Outcome) => {
        if (this.abortAttempt !== abort) return;
        this.abortAttempt = null;
        clearTimeout(timeout);
        clearInterval(heartbeat);
        socket.onopen = socket.onmessage = socket.onclose = socket.onerror = null;
        socket.close();
        if (this.live?.socket === socket) this.live = null;
        resolve(outcome);
      };
      const abort = () => finish('stopped');
      this.abortAttempt = abort;
      const timeout = setTimeout(() => finish('unreachable'), ATTEMPT_TIMEOUT_MS);

      const sendMessage = (message: Message) => {
        try {
          socket.send(transport!.encrypt(encodeMessage(message)));
        } catch {
          finish('ended'); // socket already closing
        }
      };

      socket.onopen = () => {
        opened = true;
        try {
          socket.send(handshake.writeMessage(encodeConnectRequest(pairCode === undefined ? {} : { pairCode })));
        } catch {
          finish('unreachable');
        }
      };
      socket.onclose = socket.onerror = () => finish(transport ? 'ended' : opened ? 'rejected' : 'unreachable');
      socket.onmessage = ({ data }) => {
        if (!(data instanceof ArrayBuffer)) return finish(transport ? 'ended' : 'rejected');
        const frame = new Uint8Array(data);
        lastReceived = Date.now();
        if (!transport) {
          try {
            handshake.readMessage(frame);
            transport = handshake.split();
          } catch {
            return finish('rejected'); // not the PC in the QR code
          }
          clearTimeout(timeout);
          this.live = { socket, transport };
          sendMessage({ type: 'hello', deviceName, appVersion }); // completes the handshake on the PC
          heartbeat = setInterval(() => {
            if (Date.now() - lastReceived > IDLE_TIMEOUT_MS) finish('ended');
            else sendMessage({ type: 'ping' });
          }, HEARTBEAT_MS);
          return;
        }
        let message: Message;
        try {
          message = decodeMessage(transport.decrypt(frame));
        } catch (e) {
          if (e instanceof ProtocolError) return; // authentic but malformed: ignore
          return finish('ended'); // tampered, replayed or reordered (spec §3.4)
        }
        if (message.type === 'busy') {
          onReachedPc(message.deviceName);
          return finish('busy');
        }
        if (message.type === 'bye') return finish('bye');
        if (message.type === 'ping') sendMessage({ type: 'pong' });
        if (message.type === 'welcome') {
          onReachedPc();
          this.update({ status: 'connected', problem: null, pcName: message.pcName });
        }
        for (const listener of this.messageListeners) listener(message);
      };
    });
  }
}
