import { act, renderHook } from '@testing-library/react-native';
import { Keyboard, Platform, type View } from 'react-native';
import { useKeyboardOverlap } from '../useKeyboardOverlap';

const listeners = new Map<string, (event?: unknown) => void>();
// A view from y = 100 to y = 900 on screen.
const view = { current: { measureInWindow: (cb: (x: number, y: number, w: number, h: number) => void) => cb(0, 100, 400, 800) } as unknown as View };

beforeEach(() => {
  listeners.clear();
  jest.spyOn(Keyboard, 'addListener').mockImplementation((event, listener) => {
    listeners.set(event, listener as (event?: unknown) => void);
    return { remove: jest.fn() } as never;
  });
});
afterEach(() => jest.restoreAllMocks());

it.each([
  ['ios', 'keyboardWillShow', 'keyboardWillHide'],
  ['android', 'keyboardDidShow', 'keyboardDidHide'],
] as const)('on %s, measures how much of the view the keyboard covers', async (os, showEvent, hideEvent) => {
  jest.replaceProperty(Platform, 'OS', os);
  const { result } = await renderHook(() => useKeyboardOverlap(view));

  await act(async () => listeners.get(showEvent)!({ endCoordinates: { screenY: 600 } }));
  expect(result.current).toBe(300);

  await act(async () => listeners.get(hideEvent)!());
  expect(result.current).toBe(0);
});
