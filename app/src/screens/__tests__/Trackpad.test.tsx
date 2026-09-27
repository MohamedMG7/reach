import { generateKeyPair, type Message } from '@reach/protocol';
import { fireEvent, render, screen } from '@testing-library/react-native';
import { SCROLL_UNITS_PER_POINT, STRIP_SCROLL_SPEED } from '../../core/pointer';
import { ReachClient } from '../../net/client';
import { ReachContext } from '../../state/reach';
import TrackpadScreen from '../Trackpad';

jest.mock('expo-router', () => {
  const { useEffect } = jest.requireActual('react');
  return { useFocusEffect: (effect: () => () => void) => useEffect(effect, []), router: { push: jest.fn() } };
});

let sent: Message[];

async function renderTrackpad() {
  sent = [];
  const client = new ReachClient({ hosts: [], port: 1, pcKey: new Uint8Array(32), phoneKey: generateKeyPair(), deviceName: 'x', appVersion: '1' });
  jest.spyOn(client, 'send').mockImplementation((m) => (sent.push(m), true));
  await render(
    <ReachContext.Provider value={{ locked: false, unlock: async () => null, pc: null, client, pair: async () => false }}>
      <TrackpadScreen />
    </ReachContext.Provider>,
  );
  return screen.getByLabelText('Trackpad');
}

const touch = (identifier: number, pageX: number, pageY: number) => ({ identifier, pageX, pageY });
const touches = (...changedTouches: ReturnType<typeof touch>[]) => ({ nativeEvent: { changedTouches } });

it('turns a two-finger tap on the surface into a right click', async () => {
  const surface = await renderTrackpad();

  await fireEvent(surface, 'touchStart', touches(touch(1, 100, 100), touch(2, 160, 100)));
  await fireEvent(surface, 'touchEnd', touches(touch(1, 100, 100), touch(2, 160, 100)));

  expect(sent).toEqual([
    { type: 'mouse.button', button: 'right', down: true },
    { type: 'mouse.button', button: 'right', down: false },
  ]);
});

it('sends finger movement at once, without waiting for the next frame', async () => {
  const surface = await renderTrackpad();

  await fireEvent(surface, 'touchStart', touches(touch(1, 100, 100)));
  await fireEvent(surface, 'touchMove', touches(touch(1, 130, 100)));

  expect(sent).toEqual([expect.objectContaining({ type: 'mouse.move', dy: 0 })]);
});

it('scrolls fast with one finger on the scroll strip beside the pad', async () => {
  await renderTrackpad();
  const strip = screen.getByLabelText('Scroll strip');

  await fireEvent(strip, 'touchStart', touches(touch(1, 350, 300)));
  await fireEvent(strip, 'touchMove', touches(touch(1, 350, 260)));
  await fireEvent(strip, 'touchEnd', touches(touch(1, 350, 260)));

  expect(sent).toEqual([{ type: 'mouse.scroll', dx: 0, dy: -40 * SCROLL_UNITS_PER_POINT * STRIP_SCROLL_SPEED }]);
});

it('holds the left button for as long as the Left button is pressed', async () => {
  await renderTrackpad();

  await fireEvent(screen.getByLabelText('Left click'), 'pressIn');
  await fireEvent(screen.getByLabelText('Left click'), 'pressOut');

  expect(sent).toEqual([
    { type: 'mouse.button', button: 'left', down: true },
    { type: 'mouse.button', button: 'left', down: false },
  ]);
});
