import { DOUBLE_TAP_MS, type GestureOutput, initialGestureState, nextDeadline, recognize, type TouchInput } from '../gestures';

/** Feeds timed touch inputs through the recognizer and collects every output. */
function run(steps: [number, TouchInput][]) {
  let state = initialGestureState;
  const outputs: GestureOutput[] = [];
  for (const [t, input] of steps) {
    const result = recognize(state, input, t);
    state = result.state;
    outputs.push(...result.outputs);
  }
  return { state, outputs, buttons: outputs.filter((o) => o.type === 'button') };
}

const leftClick = [
  { type: 'button', button: 'left', down: true },
  { type: 'button', button: 'left', down: false },
];

it('a tap is a left click once the double-tap window has passed', () => {
  const tap = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [80, { type: 'up', id: 1 }],
  ]);
  expect(tap.outputs).toEqual([]);
  expect(nextDeadline(tap.state)).toBe(80 + DOUBLE_TAP_MS);

  const later = recognize(tap.state, { type: 'tick' }, 80 + DOUBLE_TAP_MS);
  expect(later.outputs).toEqual(leftClick);
  expect(nextDeadline(later.state)).toBeNull();
});

it('a small wobble is still a tap', () => {
  const { state } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [30, { type: 'move', id: 1, x: 104, y: 103 }],
    [80, { type: 'up', id: 1 }],
  ]);
  expect(recognize(state, { type: 'tick' }, 1000).outputs).toEqual(leftClick);
});

it('a long press without moving is not a click', () => {
  const { state, outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [600, { type: 'up', id: 1 }],
  ]);
  expect(outputs).toEqual([]);
  expect(nextDeadline(state)).toBeNull();
});

it('one finger moving moves the pointer', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [16, { type: 'move', id: 1, x: 110, y: 100 }],
    [32, { type: 'move', id: 1, x: 115, y: 98 }],
    [48, { type: 'up', id: 1 }],
  ]);
  expect(outputs).toEqual([
    { type: 'move', dx: 10, dy: 0, t: 16 },
    { type: 'move', dx: 5, dy: -2, t: 32 },
  ]);
});

it('two fingers tapped together are a right click', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [20, { type: 'down', id: 2, x: 160, y: 100 }],
    [120, { type: 'up', id: 1 }],
    [130, { type: 'up', id: 2 }],
  ]);
  expect(outputs).toEqual([
    { type: 'button', button: 'right', down: true },
    { type: 'button', button: 'right', down: false },
  ]);
});

it('tap, then touch again and move, drags with the left button held until lift', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [80, { type: 'up', id: 1 }],
    [200, { type: 'down', id: 2, x: 100, y: 100 }],
    [220, { type: 'move', id: 2, x: 120, y: 100 }],
    [240, { type: 'move', id: 2, x: 130, y: 110 }],
    [900, { type: 'up', id: 2 }],
  ]);
  expect(outputs).toEqual([
    { type: 'button', button: 'left', down: true },
    { type: 'move', dx: 20, dy: 0, t: 220 },
    { type: 'move', dx: 10, dy: 10, t: 240 },
    { type: 'button', button: 'left', down: false },
  ]);
});

it('a double tap is two clicks, not a drag', () => {
  const { buttons } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [80, { type: 'up', id: 1 }],
    [200, { type: 'down', id: 2, x: 101, y: 100 }],
    [260, { type: 'up', id: 2 }],
  ]);
  expect(buttons).toEqual([...leftClick, ...leftClick]);
});

it('touching again after the window is a new gesture, and the first click is sent first', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [80, { type: 'up', id: 1 }],
    [500, { type: 'down', id: 2, x: 100, y: 100 }],
    [520, { type: 'move', id: 2, x: 120, y: 100 }],
  ]);
  expect(outputs).toEqual([...leftClick, { type: 'move', dx: 20, dy: 0, t: 520 }]);
});

it('two fingers dragging scroll by the midpoint movement', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 300 }],
    [10, { type: 'down', id: 2, x: 160, y: 300 }],
    [30, { type: 'move', id: 1, x: 100, y: 280 }],
    [31, { type: 'move', id: 2, x: 160, y: 280 }],
    [50, { type: 'move', id: 1, x: 100, y: 270 }],
    [400, { type: 'up', id: 1 }],
    [410, { type: 'up', id: 2 }],
  ]);
  expect(outputs).toEqual([
    { type: 'scroll', dx: 0, dy: -10 },
    { type: 'scroll', dx: 0, dy: -10 },
    { type: 'scroll', dx: 0, dy: -5 },
  ]);
});

it('lifting one finger of a scroll does not start pointing', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 300 }],
    [10, { type: 'down', id: 2, x: 160, y: 300 }],
    [30, { type: 'move', id: 2, x: 160, y: 250 }],
    [40, { type: 'up', id: 2 }],
    [60, { type: 'move', id: 1, x: 150, y: 300 }],
    [70, { type: 'up', id: 1 }],
  ]);
  expect(outputs.every((o) => o.type === 'scroll')).toBe(true);
});

it('a tap followed quickly by a two-finger gesture still clicks first', () => {
  const { buttons } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [80, { type: 'up', id: 1 }],
    [150, { type: 'down', id: 2, x: 100, y: 100 }],
    [160, { type: 'down', id: 3, x: 150, y: 100 }],
    [240, { type: 'up', id: 2 }],
    [250, { type: 'up', id: 3 }],
  ]);
  expect(buttons).toEqual([
    ...leftClick,
    { type: 'button', button: 'right', down: true },
    { type: 'button', button: 'right', down: false },
  ]);
});

it('three fingers do nothing', () => {
  const { outputs } = run([
    [0, { type: 'down', id: 1, x: 100, y: 100 }],
    [5, { type: 'down', id: 2, x: 150, y: 100 }],
    [10, { type: 'down', id: 3, x: 200, y: 100 }],
    [50, { type: 'move', id: 1, x: 100, y: 150 }],
    [90, { type: 'up', id: 1 }],
    [95, { type: 'up', id: 2 }],
    [100, { type: 'up', id: 3 }],
  ]);
  expect(outputs).toEqual([]);
});

it('ignores events for fingers it never saw go down', () => {
  const { outputs, state } = run([
    [0, { type: 'move', id: 7, x: 1, y: 1 }],
    [5, { type: 'up', id: 7 }],
  ]);
  expect(outputs).toEqual([]);
  expect(state).toEqual(initialGestureState);
});
