import type { Message } from '@reach/protocol';
import { act, fireEvent, render, screen } from '@testing-library/react-native';
import { Keyboard, Platform } from 'react-native';
import { KeyboardInput } from '../KeyboardInput';

let sent: Message[];

async function renderKeyboard() {
  sent = [];
  await render(<KeyboardInput send={(m) => sent.push(m)} />);
  return screen.getByLabelText('Type on the PC');
}

it('types text and turns deletions into Backspace', async () => {
  const field = await renderKeyboard();

  await fireEvent.changeText(field, 'hi');
  await fireEvent.changeText(field, 'h');

  expect(sent).toEqual([
    { type: 'key.text', text: 'hi' },
    { type: 'key.combo', keys: ['backspace'] },
  ]);
});

it('sends Backspace from an empty field, and Enter on return', async () => {
  const field = await renderKeyboard();

  await fireEvent(field, 'keyPress', { nativeEvent: { key: 'Backspace' } });
  await fireEvent(field, 'submitEditing');

  expect(sent).toEqual([
    { type: 'key.combo', keys: ['backspace'] },
    { type: 'key.combo', keys: ['enter'] },
  ]);
});

it('applies an armed modifier from the accessory row to the next letter', async () => {
  const field = await renderKeyboard();

  await fireEvent.press(screen.getByLabelText('Ctrl'));
  await fireEvent.changeText(field, 'c');
  await fireEvent.changeText(field, 'cv');

  expect(sent).toEqual([
    { type: 'key.combo', keys: ['ctrl', 'c'] },
    { type: 'key.text', text: 'v' },
  ]);
});

it('hides the iOS keyboard from the key row, which stays visible above it', async () => {
  const dismiss = jest.spyOn(Keyboard, 'dismiss');
  await renderKeyboard();

  await fireEvent.press(screen.getByLabelText('Hide keyboard'));

  expect(dismiss).toHaveBeenCalled();
  expect(sent).toEqual([]);
});

describe('on Android, which has no keyboard accessory views', () => {
  const listeners = new Map<string, () => void>();
  beforeEach(() => {
    jest.replaceProperty(Platform, 'OS', 'android');
    jest.spyOn(Keyboard, 'addListener').mockImplementation((event, listener) => {
      listeners.set(event, listener as () => void);
      return { remove: jest.fn() } as never;
    });
  });
  afterEach(() => jest.restoreAllMocks());

  it('shows the key row while the keyboard is open, and it still works', async () => {
    const field = await renderKeyboard();
    expect(screen.queryByLabelText('Ctrl')).toBeNull();

    await act(async () => listeners.get('keyboardDidShow')!());
    await fireEvent.press(screen.getByLabelText('Ctrl'));
    await fireEvent.changeText(field, 'c');
    expect(sent).toEqual([{ type: 'key.combo', keys: ['ctrl', 'c'] }]);

    await act(async () => listeners.get('keyboardDidHide')!());
    expect(screen.queryByLabelText('Ctrl')).toBeNull();
  });
});

it('presses Win alone on a long press, and F-keys behind Fn', async () => {
  await renderKeyboard();

  await fireEvent(screen.getByLabelText('Win'), 'longPress');
  await fireEvent.press(screen.getByLabelText('Fn'));
  await fireEvent.press(screen.getByLabelText('F5'));

  expect(sent).toEqual([
    { type: 'key.combo', keys: ['win'] },
    { type: 'key.combo', keys: ['f5'] },
  ]);
});
