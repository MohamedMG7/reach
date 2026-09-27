import { decodeBase64Url } from './base64url';

/** What the PC's pairing QR code carries (spec §3.3). */
export interface PairingInfo {
  hosts: string[];
  port: number;
  /** The PC's static public key, base64url (32 bytes). */
  pcKey: string;
  /** Single-use pairing code, passed to the PC unchanged. */
  code: string;
  pcName: string;
}

export type PairingParse = { ok: true; info: PairingInfo } | { ok: false; error: string };

const PREFIX = 'reach://pair?';
const MAX_HOSTS = 8;
const MAX_NAME = 64;
const NOT_REACH = "That QR code isn't a Reach pairing code.";

const fail = (error: string): PairingParse => ({ ok: false, error });

function isIPv4(host: string): boolean {
  const parts = host.split('.');
  return parts.length === 4 && parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) <= 255);
}

/**
 * Parses reach://pair?v=1&h=<ip>[,<ip>…]&p=<port>&k=<key>&c=<code>&n=<name>. Parsed by hand
 * rather than with URL, whose handling of custom schemes differs between Hermes and Node.
 */
export function parsePairingUri(text: string): PairingParse {
  if (!text.startsWith(PREFIX)) return fail(NOT_REACH);
  const params = new Map<string, string>();
  for (const pair of text.slice(PREFIX.length).split('&')) {
    const eq = pair.indexOf('=');
    if (eq < 0) return fail(NOT_REACH);
    try {
      params.set(pair.slice(0, eq), decodeURIComponent(pair.slice(eq + 1)));
    } catch {
      return fail(NOT_REACH);
    }
  }
  if (params.get('v') !== '1') return fail('This pairing code needs a newer version of the Reach app.');

  // The PC lists its best addresses first; a PC with many adapters (VMs, WSL, VPN) may list more.
  const hosts = (params.get('h') ?? '').split(',').filter((h) => h.length > 0).slice(0, MAX_HOSTS);
  const port = Number(params.get('p'));
  const pcKey = params.get('k') ?? '';
  const code = params.get('c') ?? '';
  if (hosts.length === 0) return fail('The PC has no network address. Connect it to Wi-Fi and show a new code.');
  if (!hosts.every(isIPv4)) return fail(NOT_REACH);
  if (!Number.isInteger(port) || port < 1 || port > 65535) return fail(NOT_REACH);
  if (decodeBase64Url(pcKey)?.length !== 32) return fail(NOT_REACH);
  if (decodeBase64Url(code)?.length !== 16) return fail(NOT_REACH);

  const pcName = (params.get('n') ?? '').trim().slice(0, MAX_NAME) || 'PC';
  return { ok: true, info: { hosts, port, pcKey, code, pcName } };
}
