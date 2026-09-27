import type { Message } from '@reach/protocol';
import { BASE_GAIN, gain, MAX_GAIN, PointerPipeline, SCROLL_UNITS_PER_POINT } from '../pointer';

function pipeline() {
  const sent: Message[] = [];
  const frames: (() => void)[] = [];
  const p = new PointerPipeline((m) => sent.push(m), (flush) => frames.push(flush));
  const nextFrame = () => frames.splice(0).forEach((f) => f());
  return { p, sent, frames, nextFrame };
}

it('slow moves use the base gain and fast ones are accelerated up to a cap', () => {
  expect(gain(1, 0, 100)).toBeCloseTo(BASE_GAIN, 1);
  expect(gain(40, 0, 16)).toBeGreaterThan(3 * BASE_GAIN);
  expect(gain(1000, 0, 1)).toBe(MAX_GAIN);
});

it('treats touch events that arrive bunched together as one 120 Hz frame apart', () => {
  const bunched = pipeline();
  const steady = pipeline();
  bunched.p.push([{ type: 'move', dx: 4, dy: 0, t: 0 }, { type: 'move', dx: 4, dy: 0, t: 1 }]);
  steady.p.push([{ type: 'move', dx: 4, dy: 0, t: 0 }, { type: 'move', dx: 4, dy: 0, t: 8 }]);
  bunched.nextFrame();
  steady.nextFrame();
  expect(bunched.sent).toEqual(steady.sent);
});

it('sends one mouse.move per frame with the summed, rounded movement', () => {
  const { p, sent, frames, nextFrame } = pipeline();
  p.push([{ type: 'move', dx: 1, dy: 0, t: 100 }]);
  p.push([{ type: 'move', dx: 1, dy: 0, t: 200 }]);
  expect(frames).toHaveLength(1);
  expect(sent).toEqual([]);

  nextFrame();

  expect(sent).toEqual([{ type: 'mouse.move', dx: 3, dy: 0 }]);
});

it('carries fractions so very slow movement still arrives', () => {
  const { p, sent, nextFrame } = pipeline();
  for (let t = 0; t < 10; t++) {
    p.push([{ type: 'move', dx: 0.3, dy: -0.3, t: t * 100 }]);
    nextFrame();
  }
  const total = sent.reduce((sum, m) => (m.type === 'mouse.move' ? sum + m.dx : sum), 0);
  // The first move has no previous timestamp and counts as one 16 ms frame.
  expect(total).toBe(Math.trunc(0.3 * gain(0.3, -0.3, 16) + 9 * 0.3 * gain(0.3, -0.3, 100)));
  expect(total).toBeGreaterThan(0);
  expect(sent.every((m) => m.type === 'mouse.move' && Number.isInteger(m.dx) && Number.isInteger(m.dy))).toBe(true);
});

it('scrolls naturally: fingers moving up scroll the page down, fingers moving left scroll right', () => {
  const { p, sent, nextFrame } = pipeline();
  p.push([{ type: 'scroll', dx: -10, dy: -20 }]);
  nextFrame();
  expect(sent).toEqual([{ type: 'mouse.scroll', dx: 10 * SCROLL_UNITS_PER_POINT, dy: -20 * SCROLL_UNITS_PER_POINT }]);
});

it('sends pending movement before a button, so a drag ends where the finger did', () => {
  const { p, sent } = pipeline();
  p.push([
    { type: 'button', button: 'left', down: true },
    { type: 'move', dx: 10, dy: 0, t: 16 },
    { type: 'button', button: 'left', down: false },
  ]);
  expect(sent.map((m) => m.type)).toEqual(['mouse.button', 'mouse.move', 'mouse.button']);
  expect(sent[2]).toEqual({ type: 'mouse.button', button: 'left', down: false });
});
