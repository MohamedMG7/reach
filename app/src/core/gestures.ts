/**
 * The trackpad gesture recognizer (spec §6.2): a pure function from touch events to pointer
 * outputs, so every gesture is unit-testable without a screen.
 *
 * - 1-finger move → move; 1-finger tap → left click (sent once the double-tap window passes)
 * - tap, then touch again within 250 ms and move → drag (left held until lift)
 * - 2-finger tap → right click; 2-finger drag → scroll
 */

export type TouchInput =
  | { type: 'down'; id: number; x: number; y: number }
  | { type: 'move'; id: number; x: number; y: number }
  | { type: 'up'; id: number }
  /** Time passing with no touch event; lets a pending click go out. */
  | { type: 'tick' };

/** Distances in screen points, `t` in ms. The pointer pipeline turns these into messages. */
export type GestureOutput =
  | { type: 'move'; dx: number; dy: number; t: number }
  | { type: 'scroll'; dx: number; dy: number }
  | { type: 'button'; button: 'left' | 'right'; down: boolean };

export const TAP_MAX_MS = 250;
export const DOUBLE_TAP_MS = 250;
/** Movement below this is still a tap (finger wobble). */
export const TAP_SLOP = 8;

type Mode =
  | 'idle'
  /** One finger down, not yet moved beyond the slop. */
  | 'touching'
  | 'pointing'
  /** Second touch within the double-tap window of a tap: a drag if it moves, a double click if not. */
  | 'tapHeld'
  | 'dragging'
  /** Two fingers down, not yet moved beyond the slop. */
  | 'twoTouching'
  | 'scrolling'
  /** The gesture is over; ignore fingers until all are lifted. */
  | 'done';

interface Point {
  x: number;
  y: number;
}

export interface GestureState {
  mode: Mode;
  fingers: Record<number, Point>;
  /** Where the current gesture started (a finger, or the two-finger midpoint). */
  start: Point;
  startedAt: number;
  /** A tap's left click waits here until this time, in case a drag follows. */
  pendingClickUntil: number | null;
}

export const initialGestureState: GestureState = { mode: 'idle', fingers: {}, start: { x: 0, y: 0 }, startedAt: 0, pendingClickUntil: null };

export interface GestureResult {
  state: GestureState;
  outputs: GestureOutput[];
}

const click = (button: 'left' | 'right'): GestureOutput[] => [
  { type: 'button', button, down: true },
  { type: 'button', button, down: false },
];

const count = (fingers: Record<number, Point>) => Object.keys(fingers).length;

function midpoint(fingers: Record<number, Point>): Point {
  const points = Object.values(fingers).slice(0, 2);
  return { x: (points[0].x + points[1].x) / 2, y: (points[0].y + points[1].y) / 2 };
}

const beyondSlop = (a: Point, b: Point) => Math.hypot(a.x - b.x, a.y - b.y) > TAP_SLOP;

/** When the caller should send a 'tick' so a pending click goes out; null if none is pending. */
export function nextDeadline(state: GestureState): number | null {
  return state.mode === 'idle' ? state.pendingClickUntil : null;
}

export function recognize(state: GestureState, input: TouchInput, now: number): GestureResult {
  const outputs: GestureOutput[] = [];
  let s: GestureState = state;

  // An expired pending click goes out before whatever happens next.
  if (s.pendingClickUntil !== null && s.mode === 'idle' && now >= s.pendingClickUntil) {
    outputs.push(...click('left'));
    s = { ...s, pendingClickUntil: null };
  }

  switch (input.type) {
    case 'tick':
      return { state: s, outputs };

    case 'down': {
      const fingers = { ...s.fingers, [input.id]: { x: input.x, y: input.y } };
      const n = count(fingers);
      if (n === 1) {
        const mode: Mode = s.pendingClickUntil !== null ? 'tapHeld' : 'touching';
        return { state: { ...s, mode, fingers, start: { x: input.x, y: input.y }, startedAt: now }, outputs };
      }
      if (n === 2 && (s.mode === 'touching' || s.mode === 'pointing' || s.mode === 'tapHeld')) {
        if (s.pendingClickUntil !== null) outputs.push(...click('left'));
        return { state: { ...s, mode: 'twoTouching', fingers, start: midpoint(fingers), pendingClickUntil: null }, outputs };
      }
      // A third finger ends a two-finger gesture; extra fingers during a drag are ignored.
      const mode: Mode = s.mode === 'dragging' ? 'dragging' : 'done';
      return { state: { ...s, mode, fingers }, outputs };
    }

    case 'move': {
      const previous = s.fingers[input.id];
      if (!previous) return { state: s, outputs };
      const fingers = { ...s.fingers, [input.id]: { x: input.x, y: input.y } };
      const here = { x: input.x, y: input.y };
      switch (s.mode) {
        case 'touching':
        case 'tapHeld':
          if (!beyondSlop(here, s.start)) return { state: { ...s, fingers }, outputs };
          if (s.mode === 'tapHeld') outputs.push({ type: 'button', button: 'left', down: true });
          outputs.push({ type: 'move', dx: here.x - s.start.x, dy: here.y - s.start.y, t: now });
          return { state: { ...s, fingers, mode: s.mode === 'tapHeld' ? 'dragging' : 'pointing', pendingClickUntil: null }, outputs };
        case 'pointing':
        case 'dragging':
          outputs.push({ type: 'move', dx: here.x - previous.x, dy: here.y - previous.y, t: now });
          return { state: { ...s, fingers }, outputs };
        case 'twoTouching':
        case 'scrolling': {
          const before = midpoint(s.fingers);
          const after = midpoint(fingers);
          if (s.mode === 'twoTouching') {
            if (!beyondSlop(after, s.start)) return { state: { ...s, fingers }, outputs };
            outputs.push({ type: 'scroll', dx: after.x - s.start.x, dy: after.y - s.start.y });
            return { state: { ...s, fingers, mode: 'scrolling' }, outputs };
          }
          outputs.push({ type: 'scroll', dx: after.x - before.x, dy: after.y - before.y });
          return { state: { ...s, fingers }, outputs };
        }
        default:
          return { state: { ...s, fingers }, outputs };
      }
    }

    case 'up': {
      if (!s.fingers[input.id]) return { state: s, outputs };
      const fingers = { ...s.fingers };
      delete fingers[input.id];
      const quick = now - s.startedAt <= TAP_MAX_MS;
      let mode: Mode = s.mode;
      let pendingClickUntil = s.pendingClickUntil;
      switch (s.mode) {
        case 'touching':
          if (quick) pendingClickUntil = now + DOUBLE_TAP_MS;
          mode = 'done';
          break;
        case 'tapHeld':
          outputs.push(...click('left')); // the first tap's click
          if (quick) outputs.push(...click('left')); // a double tap
          pendingClickUntil = null;
          mode = 'done';
          break;
        case 'dragging':
          if (count(fingers) > 0) break; // held until the last finger lifts
          outputs.push({ type: 'button', button: 'left', down: false });
          break;
        case 'twoTouching':
          if (quick) outputs.push(...click('right'));
          mode = 'done';
          break;
        case 'pointing':
        case 'scrolling':
          mode = 'done';
          break;
      }
      if (count(fingers) === 0) mode = 'idle';
      return { state: { ...s, mode, fingers, pendingClickUntil }, outputs };
    }
  }
}
