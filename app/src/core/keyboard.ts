import type { Message } from '@reach/protocol';

export type Modifier = 'ctrl' | 'alt' | 'shift' | 'win';
export const MODIFIERS: Modifier[] = ['ctrl', 'alt', 'shift', 'win'];

/** Sticky modifiers (spec §6.3): tap once to arm for the next key, twice to lock, again to release. */
export type ModifierMode = 'off' | 'armed' | 'locked';
export type Modifiers = Record<Modifier, ModifierMode>;
export const noModifiers: Modifiers = { ctrl: 'off', alt: 'off', shift: 'off', win: 'off' };

const NEXT_MODE: Record<ModifierMode, ModifierMode> = { off: 'armed', armed: 'locked', locked: 'off' };

/** One tap on a sticky modifier key. */
export const nextModifierMode = (mode: ModifierMode): ModifierMode => NEXT_MODE[mode];

export function tapModifier(modifiers: Modifiers, modifier: Modifier): Modifiers {
  return { ...modifiers, [modifier]: nextModifierMode(modifiers[modifier]) };
}

export function activeModifiers(modifiers: Modifiers): Modifier[] {
  return MODIFIERS.filter((m) => modifiers[m] !== 'off');
}

/** After a key goes out, armed modifiers are used up; locked ones stay. */
function consume(modifiers: Modifiers): Modifiers {
  const next = { ...modifiers };
  for (const m of MODIFIERS) if (next[m] === 'armed') next[m] = 'off';
  return next;
}

export interface KeyResult {
  messages: Message[];
  modifiers: Modifiers;
}

/** A named key (accessory row, Enter, Backspace on an empty field) with the active modifiers. */
export function pressKey(modifiers: Modifiers, key: string): KeyResult {
  return { messages: [{ type: 'key.combo', keys: [...activeModifiers(modifiers), key] }], modifiers: consume(modifiers) };
}

/** The key.combo name for a typed character, if it has one: a–z, 0–9 and space. */
export function keyNameFor(char: string): string | null {
  const lower = char.toLowerCase();
  if (/^[a-z0-9]$/.test(lower)) return lower;
  return char === ' ' ? 'space' : null;
}

const isRegionalIndicator = (cp: number) => cp >= 0x1f1e6 && cp <= 0x1f1ff;

/** Code points that belong to the emoji before them: variation selectors, skin tones, keycap, tags, ZWJ. */
const extendsEmoji = (cp: number) =>
  cp === 0xfe0e || cp === 0xfe0f || (cp >= 0x1f3fb && cp <= 0x1f3ff) || cp === 0x20e3 || (cp >= 0xe0020 && cp <= 0xe007f) || cp === 0x200d;

/**
 * Splits text into what one Backspace deletes: whole emoji (❤️, 👍🏽, 🇺🇸, ZWJ families), otherwise
 * single code points, so Arabic vowel marks still delete one at a time as they do on Windows.
 */
function deletable(text: string): string[] {
  const units: string[] = [];
  let flagOpen = false;
  for (const char of text) {
    const cp = char.codePointAt(0)!;
    const last = units.length - 1;
    const afterJoiner = last >= 0 && units[last].endsWith('‍');
    if (last >= 0 && (extendsEmoji(cp) || afterJoiner || (flagOpen && isRegionalIndicator(cp)))) {
      units[last] += char;
      flagOpen = false;
    } else {
      units.push(char);
      flagOpen = isRegionalIndicator(cp);
    }
  }
  return units;
}

/**
 * The hidden text field changed from `previous` to `next` (spec §6.3). Deleted characters
 * become Backspace presses and new text becomes key.text, which also covers autocorrect and
 * predictive text replacing a word. With a modifier active, a single typed letter, digit or
 * space becomes a key.combo instead (Ctrl + c); other text is typed as-is and leaves the
 * modifiers armed.
 */
export function typedText(modifiers: Modifiers, previous: string, next: string): KeyResult {
  const before = deletable(previous);
  const after = deletable(next);
  let same = 0;
  while (same < before.length && same < after.length && before[same] === after[same]) same++;

  const messages: Message[] = [];
  for (let i = same; i < before.length; i++) messages.push({ type: 'key.combo', keys: ['backspace'] });
  const inserted = after.slice(same).join('');
  if (inserted === '') return { messages, modifiers };

  const name = after.length - same === 1 ? keyNameFor(inserted) : null;
  if (name !== null && activeModifiers(modifiers).length > 0) {
    const pressed = pressKey(modifiers, name);
    return { messages: [...messages, ...pressed.messages], modifiers: pressed.modifiers };
  }
  messages.push({ type: 'key.text', text: inserted });
  return { messages, modifiers };
}

/** What the terminal accessory row's keys send (spec §6.4). */
export const TERMINAL_KEYS: Record<'esc' | 'tab' | 'up' | 'down' | 'left' | 'right', string> = {
  esc: '\x1b',
  tab: '\t',
  up: '\x1b[A',
  down: '\x1b[B',
  right: '\x1b[C',
  left: '\x1b[D',
};

const CONTROL_PUNCTUATION: Record<string, number> = { '@': 0, ' ': 0, '[': 27, '\\': 28, ']': 29, '^': 30, _: 31, '?': 127 };

/** Applies a sticky Ctrl to what xterm.js typed: Ctrl + c → "\x03". Longer input (paste) is sent unchanged. */
export function terminalInput(ctrl: ModifierMode, data: string): { data: string; ctrl: ModifierMode } {
  if (ctrl === 'off' || data.length !== 1) return { data, ctrl };
  const lower = data.toLowerCase();
  const code = /^[a-z]$/.test(lower) ? lower.charCodeAt(0) - 96 : CONTROL_PUNCTUATION[data];
  if (code === undefined) return { data, ctrl };
  return { data: String.fromCharCode(code), ctrl: ctrl === 'armed' ? 'off' : ctrl };
}
