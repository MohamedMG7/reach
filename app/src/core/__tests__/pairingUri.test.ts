import { decodeBase64Url, encodeBase64Url } from '../base64url';
import { parsePairingUri } from '../pairingUri';

// Exactly what Reach.Agent's PairingUri.Build produces (agent PairingTests.BuildsTheSpecFormat).
const FROM_AGENT =
  'reach://pair?v=1&h=192.168.1.20,10.0.0.5&p=47800&k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&c=AAECAwQFBgcICQoLDA0ODw&n=DESKTOP%20PC%261';

describe('base64url', () => {
  it('round-trips every length', () => {
    for (let n = 0; n < 40; n++) {
      const bytes = Uint8Array.from({ length: n }, (_, i) => (i * 37 + n) & 0xff);
      expect(decodeBase64Url(encodeBase64Url(bytes))).toEqual(bytes);
    }
  });

  it('matches the RFC 4648 alphabet without padding', () => {
    expect(encodeBase64Url(Uint8Array.of(0xfb, 0xff))).toBe('-_8');
    expect(decodeBase64Url('AAECAwQFBgcICQoLDA0ODw')).toEqual(Uint8Array.from({ length: 16 }, (_, i) => i));
  });

  it.each(['A', 'AA==', 'AB', 'a+b/', 'ab c'])('rejects %p', (text) => {
    expect(decodeBase64Url(text)).toBeNull();
  });
});

describe('parsePairingUri', () => {
  it("reads the agent's format", () => {
    expect(parsePairingUri(FROM_AGENT)).toEqual({
      ok: true,
      info: {
        hosts: ['192.168.1.20', '10.0.0.5'],
        port: 47800,
        pcKey: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
        code: 'AAECAwQFBgcICQoLDA0ODw',
        pcName: 'DESKTOP PC&1',
      },
    });
  });

  it.each([
    'https://example.com',
    'WIFI:S:home;T:WPA;P:secret;;',
    'reach://pair',
    FROM_AGENT.replace('k=AAAA', 'k=AA'),
    FROM_AGENT.replace('c=AAECAw', 'c=AAEC'),
    FROM_AGENT.replace('p=47800', 'p=99999'),
    FROM_AGENT.replace('192.168.1.20', '192.168.1.300'),
    FROM_AGENT.replace('192.168.1.20', 'evil.example.com'),
    FROM_AGENT.replace('n=DESKTOP%20PC%261', 'n=%E0%A4%A'),
  ])('rejects %p as not a Reach code', (text) => {
    expect(parsePairingUri(text)).toEqual({ ok: false, error: "That QR code isn't a Reach pairing code." });
  });

  it('asks for an update when the version is newer', () => {
    const result = parsePairingUri(FROM_AGENT.replace('v=1', 'v=2'));
    expect(result).toEqual({ ok: false, error: 'This pairing code needs a newer version of the Reach app.' });
  });

  it('explains a PC with no network address', () => {
    const result = parsePairingUri(FROM_AGENT.replace('h=192.168.1.20,10.0.0.5', 'h='));
    expect(result.ok).toBe(false);
  });

  it('keeps the first 8 addresses of a PC with many network adapters', () => {
    const hosts = Array.from({ length: 10 }, (_, i) => `10.0.${i}.1`);
    const result = parsePairingUri(FROM_AGENT.replace('h=192.168.1.20,10.0.0.5', `h=${hosts.join(',')}`));
    expect(result.ok && result.info.hosts).toEqual(hosts.slice(0, 8));
  });

  it('caps an overlong PC name and fills in an empty one', () => {
    const long = parsePairingUri(FROM_AGENT.replace('n=DESKTOP%20PC%261', `n=${'x'.repeat(100)}`));
    const empty = parsePairingUri(FROM_AGENT.replace('n=DESKTOP%20PC%261', 'n='));
    expect(long.ok && long.info.pcName).toBe('x'.repeat(64));
    expect(empty.ok && empty.info.pcName).toBe('PC');
  });
});
