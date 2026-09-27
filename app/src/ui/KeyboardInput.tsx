import type { Message } from '@reach/protocol';
import { type Ref, useEffect, useImperativeHandle, useRef, useState } from 'react';
import { InputAccessoryView, Keyboard, Platform, ScrollView, StyleSheet, TextInput } from 'react-native';
import { type KeyResult, type Modifier, MODIFIERS, noModifiers, pressKey, tapModifier, typedText } from '../core/keyboard';
import { KeyButton } from './KeyButton';
import { colors } from './theme';

export interface KeyboardHandle {
  /** Shows the iOS keyboard, or hides it if it's showing. */
  toggle(): void;
}

const ACCESSORY_ID = 'reach-keyboard-row';
const MODIFIER_LABELS: Record<Modifier, string> = { ctrl: 'Ctrl', alt: 'Alt', shift: 'Shift', win: 'Win' };
const NAV_KEYS: [string, string][] = [
  ['Esc', 'esc'],
  ['Tab', 'tab'],
  ['←', 'left'],
  ['↑', 'up'],
  ['↓', 'down'],
  ['→', 'right'],
];
const F_KEYS: [string, string][] = Array.from({ length: 12 }, (_, i) => [`F${i + 1}`, `f${i + 1}`]);
/** The hidden field is emptied at a word break once it grows past this. */
const MAX_FIELD_LENGTH = 200;

/**
 * The iOS keyboard for typing on the PC (spec §6.3): a hidden text field whose changes become
 * key.text / Backspace, plus an accessory row with sticky Ctrl Alt Shift Win, Esc, Tab, arrows,
 * and an Fn toggle for F1–F12. Long-press a modifier to press it alone (Win opens Start).
 */
export function KeyboardInput({ send, ref }: { send(message: Message): void; ref?: Ref<KeyboardHandle> }) {
  const input = useRef<TextInput>(null);
  const text = useRef('');
  const modifiers = useRef(noModifiers);
  const [shown, setShown] = useState(noModifiers);
  const [fn, setFn] = useState(false);

  const keyboardShown = useAndroidKeyboardShown();

  useImperativeHandle(ref, () => ({
    toggle: () => {
      if (Platform.OS !== 'android') return input.current?.isFocused() ? input.current.blur() : input.current?.focus();
      // Android's Back button hides the keyboard but leaves the field focused, and focusing a
      // focused field doesn't bring the keyboard back; so go by the keyboard, and refocus.
      if (keyboardShown) return Keyboard.dismiss();
      input.current?.blur();
      input.current?.focus();
    },
  }));

  const setModifiers = (next: typeof noModifiers) => {
    modifiers.current = next;
    setShown(next);
  };
  const apply = (result: KeyResult) => {
    result.messages.forEach(send);
    setModifiers(result.modifiers);
  };

  const onChangeText = (next: string) => {
    apply(typedText(modifiers.current, text.current, next));
    text.current = next;
    if (next.length > MAX_FIELD_LENGTH && next.endsWith(' ')) {
      text.current = ''; // first, so a change event from clearing sends nothing
      input.current?.clear();
    }
  };

  const row = (
    <ScrollView horizontal keyboardShouldPersistTaps="always" contentContainerStyle={styles.row} style={styles.bar}>
      {/* The Keyboard button on the trackpad is under the keyboard once it's up. */}
      <KeyButton label="Hide keyboard" icon="chevron-down" onPress={() => Keyboard.dismiss()} />
      {MODIFIERS.map((m) => (
        <KeyButton
          key={m}
          label={MODIFIER_LABELS[m]}
          mode={shown[m]}
          onPress={() => setModifiers(tapModifier(modifiers.current, m))}
          onLongPress={() => pressKey(noModifiers, m).messages.forEach(send)}
        />
      ))}
      <KeyButton label="Fn" mode={fn ? 'locked' : 'off'} onPress={() => setFn(!fn)} />
      {(fn ? F_KEYS : NAV_KEYS).map(([label, key]) => (
        <KeyButton key={key} label={label} onPress={() => apply(pressKey(modifiers.current, key))} />
      ))}
    </ScrollView>
  );

  return (
    <>
      <TextInput
        ref={input}
        accessibilityLabel="Type on the PC"
        style={styles.hidden}
        autoCorrect={false}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck={false}
        submitBehavior="submit"
        inputAccessoryViewID={ACCESSORY_ID}
        onChangeText={onChangeText}
        onKeyPress={({ nativeEvent }) => {
          // With text in the field, the deletion arrives through onChangeText instead.
          if (nativeEvent.key === 'Backspace' && text.current === '') apply(pressKey(modifiers.current, 'backspace'));
        }}
        onSubmitEditing={() => apply(pressKey(modifiers.current, 'enter'))}
      />
      {/* iOS carries the row on top of the keyboard; Android has no such view, so the row is shown
          in the layout while the keyboard is open, and the screen pads itself above the keyboard. */}
      {Platform.OS === 'ios' ? <InputAccessoryView nativeID={ACCESSORY_ID}>{row}</InputAccessoryView> : keyboardShown && row}
    </>
  );
}

/** Whether the Android keyboard is open; always false elsewhere. */
function useAndroidKeyboardShown(): boolean {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    if (Platform.OS !== 'android') return;
    const show = Keyboard.addListener('keyboardDidShow', () => setShown(true));
    const hide = Keyboard.addListener('keyboardDidHide', () => setShown(false));
    return () => {
      show.remove();
      hide.remove();
    };
  }, []);
  return shown;
}

const styles = StyleSheet.create({
  hidden: { position: 'absolute', width: 1, height: 1, opacity: 0 },
  bar: { backgroundColor: colors.surface, borderTopWidth: 1, borderTopColor: colors.border },
  row: { gap: 6, padding: 8 },
});
