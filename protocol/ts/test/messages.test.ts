import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { decodeMessage, encodeMessage, ProtocolError, type Message } from '../src/messages';

const samples = JSON.parse(readFileSync(new URL('../../test-vectors/messages.json', import.meta.url), 'utf8'));
const utf8 = (s: string) => new TextEncoder().encode(s);

describe('message codec', () => {
  it.each(samples.valid as Message[])('decodes $type', (sample) => {
    expect(decodeMessage(utf8(JSON.stringify(sample)))).toEqual(sample);
  });

  it.each(samples.valid as Message[])('round-trips $type', (sample) => {
    expect(decodeMessage(encodeMessage(sample))).toEqual(sample);
  });

  it.each(samples.invalid as { json: string; reason: string }[])('rejects $reason', ({ json }) => {
    expect(() => decodeMessage(utf8(json))).toThrow(ProtocolError);
  });

  it('ignores unknown extra fields so newer peers stay compatible', () => {
    expect(decodeMessage(utf8('{"type":"ping","future":1}')).type).toBe('ping');
  });
});
