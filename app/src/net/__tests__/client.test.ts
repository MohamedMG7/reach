import { generateKeyPair, type Message } from '@reach/protocol';
import { encodeBase64Url } from '../../core/base64url';
import { FakePc } from '../../testing/fakePc';
import { ATTEMPT_TIMEOUT_MS, IDLE_TIMEOUT_MS, ReachClient } from '../client';

const HOME = '192.168.1.20';
const VPN = '10.8.0.2';
const tick = (ms = 0) => jest.advanceTimersByTimeAsync(ms);

let pc: FakePc;
const phoneKey = generateKeyPair();

function makeClient(hosts = [HOME]) {
  return new ReachClient({
    hosts,
    port: 47800,
    pcKey: pc.key.publicKey,
    phoneKey,
    deviceName: 'Bedroom iPhone',
    appVersion: '1.0.0',
    createSocket: pc.createSocket,
  });
}

beforeEach(() => {
  jest.useFakeTimers();
  pc = new FakePc();
  pc.paired.add(encodeBase64Url(phoneKey.publicKey));
});

afterEach(() => jest.useRealTimers());

it('connects, says hello and learns the PC name', async () => {
  const client = makeClient();
  client.start();
  expect(client.state.status).toBe('reconnecting');

  await tick();

  expect(pc.last.host).toBe(HOME);
  expect(pc.received[0]).toEqual({ type: 'hello', deviceName: 'Bedroom iPhone', appVersion: '1.0.0' });
  expect(client.state).toEqual({ status: 'connected', problem: null, pcName: 'DESKTOP' });
});

it('sends while connected and drops input while not', async () => {
  const client = makeClient();
  expect(client.send({ type: 'mouse.move', dx: 1, dy: 1 })).toBe(false);

  client.start();
  await tick();
  expect(client.send({ type: 'mouse.move', dx: 3, dy: 4 })).toBe(true);
  await tick();

  expect(pc.received).toContainEqual({ type: 'mouse.move', dx: 3, dy: 4 });
  expect(pc.received).not.toContainEqual({ type: 'mouse.move', dx: 1, dy: 1 });
});

it('delivers PC messages to subscribers', async () => {
  const client = makeClient();
  const seen: Message[] = [];
  client.onMessage((m) => seen.push(m));
  client.start();
  await tick();

  pc.send(pc.live!, { type: 'cmd.listed', commands: [] });
  await tick();

  expect(seen).toContainEqual({ type: 'cmd.listed', commands: [] });
});

it('tries each host in turn', async () => {
  pc.reachable.clear();
  pc.reachable.add(VPN);
  const client = makeClient([HOME, VPN]);

  client.start();
  await tick();

  expect(pc.sockets.map((s) => s.host)).toEqual([HOME, VPN]);
  expect(client.state.status).toBe('connected');
});

it('gives up on a host that never answers and tries the next', async () => {
  pc.hanging.add(HOME);
  pc.reachable.clear();
  pc.reachable.add(VPN);
  const client = makeClient([HOME, VPN]);

  client.start();
  await tick(ATTEMPT_TIMEOUT_MS);

  expect(pc.sockets[0].closedByClient).toBe(true);
  expect(client.state.status).toBe('connected');
});

it('keeps retrying an unreachable PC with backoff 1 s, 2 s, then every 5 s', async () => {
  pc.reachable.clear();
  const client = makeClient();

  client.start();
  await tick();
  expect(client.state).toMatchObject({ status: 'offline', problem: 'unreachable' });

  const attemptsAt: number[] = [];
  for (let ms = 0; ms <= 20_000; ms += 500) {
    const before = pc.sockets.length;
    await tick(500);
    if (pc.sockets.length > before) attemptsAt.push(ms + 500);
  }
  expect(attemptsAt.slice(0, 5)).toEqual([1000, 3000, 8000, 13000, 18000]);
});

it('stops and reports a phone the PC does not know', async () => {
  pc.paired.clear();
  const client = makeClient();

  client.start();
  await tick(60_000);

  expect(client.state).toMatchObject({ status: 'offline', problem: 'not_paired' });
  expect(pc.sockets).toHaveLength(1);
});

it('treats a PC whose key changed (reinstalled) as not paired', async () => {
  const client = new ReachClient({
    hosts: [HOME],
    port: 47800,
    pcKey: generateKeyPair().publicKey, // the QR code's key no longer matches the PC
    phoneKey,
    deviceName: 'x',
    appVersion: '1',
    createSocket: pc.createSocket,
  });

  client.start();
  await tick();

  expect(client.state).toMatchObject({ status: 'offline', problem: 'not_paired' });
  expect(pc.received).toEqual([]);
});

it('reconnects one second after a session drops', async () => {
  const client = makeClient();
  client.start();
  await tick();

  pc.live!.drop();
  await tick();
  expect(client.state.status).toBe('reconnecting');
  await tick(999);
  expect(pc.sockets).toHaveLength(1);
  await tick(1);

  expect(pc.sockets).toHaveLength(2);
  expect(client.state.status).toBe('connected');
});

it('answers pings and sends its own heartbeat', async () => {
  const client = makeClient();
  client.start();
  await tick();

  pc.send(pc.live!, { type: 'ping' });
  await tick(5000);

  expect(pc.received).toContainEqual({ type: 'pong' });
  expect(pc.received).toContainEqual({ type: 'ping' });
  expect(client.state.status).toBe('connected');
});

it('closes a session that has gone silent for 15 s and reconnects', async () => {
  const client = makeClient();
  client.start();
  await tick();
  const first = pc.live!;
  pc.answerHello = false;
  first.send = () => {}; // the PC stops hearing us, so it stops answering pings

  await tick(IDLE_TIMEOUT_MS + 5000); // the first heartbeat after 15 s of silence
  expect(first.closedByClient).toBe(true);
  await tick(1000);

  expect(pc.sockets.length).toBeGreaterThan(1);
});

it('ends the session when a frame fails to decrypt', async () => {
  const client = makeClient();
  client.start();
  await tick();
  const first = pc.live!;

  first.deliver(new Uint8Array(40));
  await tick();

  expect(first.closedByClient).toBe(true);
  expect(client.state.status).toBe('reconnecting');
});

it('ignores a malformed but authentic message', async () => {
  const client = makeClient();
  client.start();
  await tick();

  pc.send(pc.live!, { type: 'term.output' } as unknown as Message);
  await tick();

  expect(client.state.status).toBe('connected');
});

it('pairs with a code in the first message, then reconnects without it', async () => {
  pc.paired.clear();
  pc.pairCodes.add('AAECAwQFBgcICQoLDA0ODw');
  const client = makeClient();

  client.start('AAECAwQFBgcICQoLDA0ODw');
  await tick();
  expect(client.state.status).toBe('connected');

  pc.live!.drop();
  await tick(1000);

  expect(pc.pairCodesUsed).toEqual(['AAECAwQFBgcICQoLDA0ODw']);
  expect(client.state.status).toBe('connected');
});

it('reports a used or expired pairing code once, without retrying', async () => {
  pc.paired.clear();
  const client = makeClient();

  client.start('AAECAwQFBgcICQoLDA0ODw');
  await tick(60_000);

  expect(client.state).toMatchObject({ status: 'offline', problem: 'pair_failed' });
  expect(pc.sockets).toHaveLength(1);
});

it('reports an unreachable PC while pairing, without retrying', async () => {
  pc.reachable.clear();
  const client = makeClient();

  client.start('AAECAwQFBgcICQoLDA0ODw');
  await tick(60_000);

  expect(client.state).toMatchObject({ status: 'offline', problem: 'pair_failed' });
  expect(pc.sockets).toHaveLength(1);
});

it('says which phone is using the PC, and connects once it is free', async () => {
  pc.busyWith = 'Kitchen Pixel';
  const client = makeClient();

  client.start();
  await tick();
  expect(client.state).toMatchObject({ status: 'offline', problem: 'busy', busyWith: 'Kitchen Pixel' });
  expect(client.send({ type: 'mouse.move', dx: 1, dy: 1 })).toBe(false);

  pc.busyWith = null;
  await tick(5000);

  expect(client.state).toMatchObject({ status: 'connected', problem: null });
});

it('pairing while another phone uses the PC still pairs, then waits its turn', async () => {
  pc.paired.clear();
  pc.pairCodes.add('AAECAwQFBgcICQoLDA0ODw');
  pc.busyWith = 'Kitchen Pixel';
  const client = makeClient();

  client.start('AAECAwQFBgcICQoLDA0ODw');
  await tick();
  expect(client.state.problem).toBe('busy');

  pc.busyWith = null;
  await tick(5000);

  expect(client.state.status).toBe('connected');
  expect(pc.pairCodesUsed).toEqual(['AAECAwQFBgcICQoLDA0ODw']); // the code is not sent again
});

it('stays disconnected when the PC disconnects this phone', async () => {
  const client = makeClient();
  client.start();
  await tick();

  pc.send(pc.live!, { type: 'bye' });
  pc.live!.drop();
  await tick(60_000);

  expect(client.state).toMatchObject({ status: 'offline', problem: 'disconnected' });
  expect(pc.sockets).toHaveLength(1);
});

it('stop closes the connection and stops reconnecting', async () => {
  const client = makeClient();
  client.start();
  await tick();

  client.stop();
  await tick(60_000);

  expect(pc.sockets).toHaveLength(1);
  expect(pc.sockets[0].closedByClient).toBe(true);
  expect(client.state.status).toBe('offline');
});
