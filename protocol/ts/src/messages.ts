/** Application messages carried inside the Noise transport (spec §4). */

export interface CommandInfo {
  id: string;
  label: string;
  icon: string;
  confirm: boolean;
}

export type Message =
  | { type: 'hello'; deviceName: string; appVersion: string }
  | { type: 'welcome'; pcName: string; agentVersion: string }
  /** Instead of welcome: another phone is connected; the PC then closes the connection. */
  | { type: 'busy'; deviceName: string }
  /** The PC disconnected this phone on purpose; the phone should not reconnect by itself. */
  | { type: 'bye' }
  | { type: 'ping' }
  | { type: 'pong' }
  | { type: 'mouse.move'; dx: number; dy: number }
  | { type: 'mouse.button'; button: string; down: boolean }
  | { type: 'mouse.scroll'; dx: number; dy: number }
  | { type: 'key.text'; text: string }
  | { type: 'key.combo'; keys: string[] }
  | { type: 'term.open'; cols: number; rows: number }
  | { type: 'term.opened'; resumed: boolean }
  | { type: 'term.input'; data: string }
  | { type: 'term.output'; data: string }
  | { type: 'term.resize'; cols: number; rows: number }
  | { type: 'term.exited'; code: number }
  | { type: 'cmd.list' }
  | { type: 'cmd.listed'; commands: CommandInfo[] }
  | { type: 'cmd.run'; id: string }
  | { type: 'cmd.result'; id: string; ok: boolean; exitCode: number; error?: string }
  | { type: 'error'; code: string; message: string };

export type MessageType = Message['type'];

/** Payload of the first handshake message: `pairCode` is present only when pairing. */
export interface ConnectRequest {
  pairCode?: string;
}

export class ProtocolError extends Error {}

type FieldKind = 'string' | 'int' | 'boolean' | 'string[]' | 'commands' | 'string?';

const SCHEMA: Record<MessageType, Record<string, FieldKind>> = {
  hello: { deviceName: 'string', appVersion: 'string' },
  welcome: { pcName: 'string', agentVersion: 'string' },
  busy: { deviceName: 'string' },
  bye: {},
  ping: {},
  pong: {},
  'mouse.move': { dx: 'int', dy: 'int' },
  'mouse.button': { button: 'string', down: 'boolean' },
  'mouse.scroll': { dx: 'int', dy: 'int' },
  'key.text': { text: 'string' },
  'key.combo': { keys: 'string[]' },
  'term.open': { cols: 'int', rows: 'int' },
  'term.opened': { resumed: 'boolean' },
  'term.input': { data: 'string' },
  'term.output': { data: 'string' },
  'term.resize': { cols: 'int', rows: 'int' },
  'term.exited': { code: 'int' },
  'cmd.list': {},
  'cmd.listed': { commands: 'commands' },
  'cmd.run': { id: 'string' },
  'cmd.result': { id: 'string', ok: 'boolean', exitCode: 'int', error: 'string?' },
  error: { code: 'string', message: 'string' },
};

function isCommandInfo(v: unknown): v is CommandInfo {
  if (typeof v !== 'object' || v === null) return false;
  const c = v as Record<string, unknown>;
  return typeof c.id === 'string' && typeof c.label === 'string' && typeof c.icon === 'string' && typeof c.confirm === 'boolean';
}

function matches(kind: FieldKind, value: unknown): boolean {
  switch (kind) {
    case 'string':
      return typeof value === 'string';
    case 'string?':
      return value === undefined || typeof value === 'string';
    case 'int':
      // Same range as C#'s int, so both sides accept exactly the same messages.
      return Number.isInteger(value) && (value as number) >= -2147483648 && (value as number) <= 2147483647;
    case 'boolean':
      return typeof value === 'boolean';
    case 'string[]':
      return Array.isArray(value) && value.every((k) => typeof k === 'string');
    case 'commands':
      return Array.isArray(value) && value.every(isCommandInfo);
  }
}

export function encodeMessage(message: Message): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(message));
}

/** Parses and validates a message. Throws ProtocolError for malformed JSON, unknown types, or wrong field types. */
export function decodeMessage(bytes: Uint8Array): Message {
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    throw new ProtocolError('invalid JSON');
  }
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ProtocolError('message must be an object');
  const obj = value as Record<string, unknown>;
  const type = obj.type;
  if (typeof type !== 'string' || !Object.prototype.hasOwnProperty.call(SCHEMA, type)) throw new ProtocolError(`unknown message type: ${String(type)}`);
  for (const [field, kind] of Object.entries(SCHEMA[type as MessageType])) {
    if (!matches(kind, obj[field])) throw new ProtocolError(`${type}: bad or missing field "${field}"`);
  }
  return obj as Message;
}

export function encodeConnectRequest(request: ConnectRequest): Uint8Array {
  return new TextEncoder().encode(JSON.stringify(request));
}
