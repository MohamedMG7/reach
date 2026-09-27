import { generateKeyPair } from '@reach/protocol';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { Alert } from 'react-native';
import { encodeBase64Url } from '../../core/base64url';
import { ReachClient } from '../../net/client';
import { ReachContext } from '../../state/reach';
import { FakePc } from '../../testing/fakePc';
import CommandsScreen from '../Commands';

jest.mock('expo-router', () => ({ useIsFocused: () => true, router: { push: jest.fn() } }));

let pc: FakePc;
let client: ReachClient;

async function renderConnected() {
  const phoneKey = generateKeyPair();
  pc = new FakePc();
  pc.paired.add(encodeBase64Url(phoneKey.publicKey));
  pc.handler = (message, reply) => {
    if (message.type === 'cmd.list')
      reply({
        type: 'cmd.listed',
        commands: [
          { id: 'mute', label: 'Mute / Unmute', icon: 'volume-mute', confirm: false },
          { id: 'sleep', label: 'Sleep', icon: 'no-such-icon', confirm: true },
        ],
      });
    if (message.type === 'cmd.run')
      reply(message.id === 'mute' ? { type: 'cmd.result', id: 'mute', ok: true, exitCode: 0 } : { type: 'cmd.result', id: message.id, ok: false, exitCode: 1, error: 'exit code 1' });
  };
  client = new ReachClient({ hosts: ['192.168.1.20'], port: 47800, pcKey: pc.key.publicKey, phoneKey, deviceName: 'x', appVersion: '1', createSocket: pc.createSocket });
  client.start();
  await render(
    <ReachContext.Provider value={{ locked: false, unlock: async () => null, pc: null, client, pair: async () => false }}>
      <CommandsScreen />
    </ReachContext.Provider>,
  );
  await screen.findByText('Sleep');
}

afterEach(() => act(async () => client.stop()));

it("lists the PC's commands once connected", async () => {
  await renderConnected();
  expect(screen.getByText('Mute / Unmute')).toBeTruthy();
  expect(pc.received).toContainEqual({ type: 'cmd.list' });
});

it('runs a command straight away and shows the result', async () => {
  await renderConnected();

  await fireEvent.press(screen.getByLabelText('Mute / Unmute'));

  expect(await screen.findByText('Mute / Unmute: done')).toBeTruthy();
  expect(pc.received).toContainEqual({ type: 'cmd.run', id: 'mute' });
});

it('asks before running a command marked confirm, and shows a failure', async () => {
  const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
  await renderConnected();

  await fireEvent.press(screen.getByLabelText('Sleep'));
  expect(pc.received).not.toContainEqual({ type: 'cmd.run', id: 'sleep' });
  const buttons = alert.mock.calls[0][2]!;
  await act(async () => buttons.find((b) => b.text === 'Run')!.onPress!());

  expect(await screen.findByText('Sleep failed: exit code 1')).toBeTruthy();
});

it('does nothing when the confirmation is cancelled', async () => {
  const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
  await renderConnected();

  await fireEvent.press(screen.getByLabelText('Sleep'));
  const buttons = alert.mock.calls[0][2]!;
  await act(async () => buttons.find((b) => b.text === 'Cancel')!.onPress?.());

  expect(pc.received.filter((m) => m.type === 'cmd.run')).toEqual([]);
});

it('disables the commands while the PC is not connected', async () => {
  await renderConnected();
  await act(async () => pc.live!.drop());

  expect(screen.getByLabelText('Mute / Unmute')).toBeDisabled();
});
