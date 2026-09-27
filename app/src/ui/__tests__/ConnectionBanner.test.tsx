import { generateKeyPair } from '@reach/protocol';
import { fireEvent, render, screen } from '@testing-library/react-native';
import { type ClientState, ReachClient } from '../../net/client';
import { ReachContext } from '../../state/reach';
import { ConnectionBanner } from '../ConnectionBanner';

jest.mock('expo-router', () => ({ router: { push: jest.fn() } }));

async function renderBanner(state: ClientState) {
  const client = new ReachClient({ hosts: [], port: 1, pcKey: new Uint8Array(32), phoneKey: generateKeyPair(), deviceName: 'x', appVersion: '1' });
  jest.spyOn(client, 'state', 'get').mockReturnValue(state);
  const start = jest.spyOn(client, 'start').mockImplementation(() => {});
  await render(
    <ReachContext.Provider value={{ locked: false, unlock: async () => null, pc: null, client, pair: async () => false }}>
      <ConnectionBanner />
    </ReachContext.Provider>,
  );
  return start;
}

it('says which phone is using the PC, with nothing to press', async () => {
  await renderBanner({ status: 'offline', problem: 'busy', pcName: 'DESKTOP', busyWith: 'Kitchen Pixel' });

  expect(screen.getByText('Kitchen Pixel is using DESKTOP. Reach connects when it disconnects.')).toBeTruthy();
  expect(screen.queryByRole('button')).toBeNull();
});

it('offers Reconnect after the PC disconnected this phone', async () => {
  const start = await renderBanner({ status: 'offline', problem: 'disconnected', pcName: 'DESKTOP' });

  expect(screen.getByText('DESKTOP disconnected this phone.')).toBeTruthy();
  await fireEvent.press(screen.getByText('Reconnect'));

  expect(start).toHaveBeenCalledWith();
});
