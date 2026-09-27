import { activeModifiers, noModifiers, pressKey, tapModifier, terminalInput, typedText } from '../keyboard';

describe('sticky modifiers', () => {
  it('cycle off → armed → locked → off', () => {
    const armed = tapModifier(noModifiers, 'ctrl');
    const locked = tapModifier(armed, 'ctrl');
    expect(armed.ctrl).toBe('armed');
    expect(locked.ctrl).toBe('locked');
    expect(tapModifier(locked, 'ctrl').ctrl).toBe('off');
  });

  it('an armed modifier applies to the next key only', () => {
    const first = pressKey(tapModifier(noModifiers, 'alt'), 'tab');
    expect(first.messages).toEqual([{ type: 'key.combo', keys: ['alt', 'tab'] }]);
    expect(pressKey(first.modifiers, 'tab').messages).toEqual([{ type: 'key.combo', keys: ['tab'] }]);
  });

  it('a locked modifier stays for every key until released', () => {
    const locked = tapModifier(tapModifier(noModifiers, 'shift'), 'shift');
    const first = pressKey(locked, 'right');
    const second = pressKey(first.modifiers, 'right');
    expect(second.messages).toEqual([{ type: 'key.combo', keys: ['shift', 'right'] }]);
    expect(second.modifiers.shift).toBe('locked');
  });

  it('combines modifiers in a fixed order: ctrl alt shift win', () => {
    const m = tapModifier(tapModifier(tapModifier(noModifiers, 'shift'), 'win'), 'ctrl');
    expect(activeModifiers(m)).toEqual(['ctrl', 'shift', 'win']);
    expect(pressKey(m, 'esc').messages).toEqual([{ type: 'key.combo', keys: ['ctrl', 'shift', 'win', 'esc'] }]);
  });
});

describe('typedText', () => {
  it('sends new text as key.text', () => {
    expect(typedText(noModifiers, 'hel', 'hello').messages).toEqual([{ type: 'key.text', text: 'lo' }]);
  });

  it('keeps Arabic and emoji whole', () => {
    expect(typedText(noModifiers, '', 'مرحبا 👋').messages).toEqual([{ type: 'key.text', text: 'مرحبا 👋' }]);
    expect(typedText(noModifiers, 'a👋', 'a').messages).toEqual([{ type: 'key.combo', keys: ['backspace'] }]);
  });

  it.each(['❤️', '👍🏽', '🇺🇸', '👨‍👩‍👧', '1️⃣'])('deletes the emoji %p with one Backspace', (emoji) => {
    expect(typedText(noModifiers, `ok ${emoji}`, 'ok ').messages).toEqual([{ type: 'key.combo', keys: ['backspace'] }]);
  });

  it('deletes two flags as two Backspaces', () => {
    expect(typedText(noModifiers, '🇺🇸🇫🇷', '').messages).toHaveLength(2);
  });

  it('deletes Arabic vowel marks one at a time, as Windows does', () => {
    expect(typedText(noModifiers, 'مَ', 'م').messages).toEqual([{ type: 'key.combo', keys: ['backspace'] }]);
    expect(typedText(noModifiers, 'مَ', '').messages).toHaveLength(2);
  });

  it('turns deletions into Backspace presses', () => {
    expect(typedText(noModifiers, 'hello', 'he').messages).toEqual([
      { type: 'key.combo', keys: ['backspace'] },
      { type: 'key.combo', keys: ['backspace'] },
      { type: 'key.combo', keys: ['backspace'] },
    ]);
  });

  it('replays an autocorrected word as backspaces then the new word', () => {
    expect(typedText(noModifiers, 'teh ', 'the ').messages).toEqual([
      { type: 'key.combo', keys: ['backspace'] },
      { type: 'key.combo', keys: ['backspace'] },
      { type: 'key.combo', keys: ['backspace'] },
      { type: 'key.text', text: 'he ' },
    ]);
  });

  it('with Ctrl armed, a typed letter becomes a shortcut and disarms Ctrl', () => {
    const result = typedText(tapModifier(noModifiers, 'ctrl'), '', 'C');
    expect(result.messages).toEqual([{ type: 'key.combo', keys: ['ctrl', 'c'] }]);
    expect(result.modifiers.ctrl).toBe('off');
  });

  it('with a modifier armed, text without a key name is typed and the modifier stays armed', () => {
    const result = typedText(tapModifier(noModifiers, 'ctrl'), '', 'é');
    expect(result.messages).toEqual([{ type: 'key.text', text: 'é' }]);
    expect(result.modifiers.ctrl).toBe('armed');
  });

  it('sends nothing when nothing changed', () => {
    expect(typedText(noModifiers, 'same', 'same').messages).toEqual([]);
  });
});

describe('terminalInput', () => {
  it('passes input through when Ctrl is off', () => {
    expect(terminalInput('off', 'c')).toEqual({ data: 'c', ctrl: 'off' });
  });

  it('turns Ctrl + letter into a control code and disarms an armed Ctrl', () => {
    expect(terminalInput('armed', 'c')).toEqual({ data: '\x03', ctrl: 'off' });
    expect(terminalInput('armed', 'D')).toEqual({ data: '\x04', ctrl: 'off' });
    expect(terminalInput('locked', 'z')).toEqual({ data: '\x1a', ctrl: 'locked' });
    expect(terminalInput('armed', '[')).toEqual({ data: '\x1b', ctrl: 'off' });
  });

  it('leaves pasted text and unmapped characters alone', () => {
    expect(terminalInput('armed', 'ls -la')).toEqual({ data: 'ls -la', ctrl: 'armed' });
    expect(terminalInput('armed', '1')).toEqual({ data: '1', ctrl: 'armed' });
  });
});
