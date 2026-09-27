import {
  decodeMessage,
  encodeConnectRequest,
  encodeMessage,
  generateKeyPair,
  IKHandshake,
  type Message,
  Transport,
} from '@reach/protocol';
import { encodeBase64Url } from '../core/base64url';
import { PROLOGUE } from '../net/client';
import type { FrameSocket } from '../net/socket';

/** Delivers after the current call returns, like a real socket event. */
const later = (action: () => void) => void Promise.resolve().then(action);

export class FakeSocket implements FrameSocket {
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  closedByClient = false;
  closed = false;

  constructor(
    readonly host: string,
    private readonly pc: FakePc,
  ) {}

  send(data: Uint8Array): void {
    if (this.closed) throw new Error('socket closed');
    this.pc.receive(this, data.slice());
  }

  close(): void {
    this.closed = this.closedByClient = true;
  }

  /** PC → phone. */
  deliver(frame: Uint8Array): void {
    later(() => {
      if (!this.closed) this.onmessage?.({ data: frame.slice().buffer });
    });
  }

  /** The PC (or the network) ends the connection. */
  drop(): void {
    later(() => {
      if (this.closed) return;
      this.closed = true;
      this.onclose?.();
    });
  }
}

/**
 * A PC agent in memory: a real Noise IK responder that accepts paired keys and pairing codes,
 * answers hello and ping, and records what the phone sent.
 */
export class FakePc {
  readonly key = generateKeyPair();
  readonly sockets: FakeSocket[] = [];
  readonly received: Message[] = [];
  /** Hosts that accept connections. Hosts in `hanging` never answer; any other host refuses. */
  readonly reachable = new Set(['192.168.1.20']);
  readonly hanging = new Set<string>();
  readonly paired = new Set<string>();
  readonly pairCodes = new Set<string>();
  readonly pairCodesUsed: string[] = [];
  answerHello = true;
  /** While set, hello is answered with busy (this phone is using the PC) and the connection closed. */
  busyWith: string | null = null;
  /** Answers other messages, e.g. cmd.list. */
  handler: ((message: Message, reply: (message: Message) => void) => void) | undefined;
  private readonly transports = new Map<FakeSocket, Transport>();

  createSocket = (url: string): FrameSocket => {
    const host = url.slice('ws://'.length, url.lastIndexOf(':'));
    const socket = new FakeSocket(host, this);
    this.sockets.push(socket);
    if (this.reachable.has(host)) later(() => socket.onopen?.());
    else if (!this.hanging.has(host)) socket.drop();
    return socket;
  };

  get last(): FakeSocket {
    return this.sockets[this.sockets.length - 1];
  }

  /** The newest socket that is still open. */
  get live(): FakeSocket | undefined {
    return this.sockets.filter((s) => !s.closed && this.transports.has(s)).pop();
  }

  send(socket: FakeSocket, message: Message): void {
    socket.deliver(this.transports.get(socket)!.encrypt(encodeMessage(message)));
  }

  receive(socket: FakeSocket, frame: Uint8Array): void {
    const transport = this.transports.get(socket);
    if (transport) {
      const message = decodeMessage(transport.decrypt(frame));
      this.received.push(message);
      if (message.type === 'hello' && this.busyWith !== null) {
        this.send(socket, { type: 'busy', deviceName: this.busyWith });
        socket.drop();
        return;
      }
      if (message.type === 'hello' && this.answerHello) this.send(socket, { type: 'welcome', pcName: 'DESKTOP', agentVersion: '1.0.0' });
      if (message.type === 'ping') this.send(socket, { type: 'pong' });
      this.handler?.(message, (reply) => this.send(socket, reply));
      return;
    }
    const handshake = new IKHandshake({ initiator: false, prologue: PROLOGUE, s: this.key });
    let request: { pairCode?: string };
    try {
      request = JSON.parse(new TextDecoder().decode(handshake.readMessage(frame)));
    } catch {
      return socket.drop(); // e.g. encrypted to another PC's key
    }
    const phone = encodeBase64Url(handshake.remoteStaticKey!);
    if (request.pairCode !== undefined) {
      this.pairCodesUsed.push(request.pairCode);
      if (!this.pairCodes.delete(request.pairCode)) return socket.drop();
      this.paired.add(phone);
    }
    if (!this.paired.has(phone)) return socket.drop(); // unknown phone: close without replying
    socket.deliver(handshake.writeMessage(encodeConnectRequest({})));
    this.transports.set(socket, handshake.split());
  }
}
