import type { Message } from '@reach/protocol';
import type { GestureOutput } from './gestures';

/** Screen points → PC pixels when moving slowly. */
export const BASE_GAIN = 1.5;
/** Extra gain per point/ms of finger speed. */
export const ACCELERATION = 1.2;
export const MAX_GAIN = 6;
/** Wheel units per point of two-finger travel; 120 units is one notch (about 3 lines). */
export const SCROLL_UNITS_PER_POINT = 3;
/** The one-finger scroll strip beside the trackpad scrolls this many times faster than two fingers. */
export const STRIP_SCROLL_SPEED = 3;
/**
 * Touch events can reach JavaScript bunched together, a millisecond apart; timing them as they
 * arrive would read as a very fast finger and jump the cursor. One 120 Hz touch frame is the floor.
 */
export const MIN_MOVE_INTERVAL_MS = 8;

/** Pointer acceleration: slow moves stay precise, fast flicks cross the screen. */
export function gain(dx: number, dy: number, dtMs: number): number {
  const speed = Math.hypot(dx, dy) / Math.max(dtMs, 1);
  return Math.min(MAX_GAIN, BASE_GAIN * (1 + ACCELERATION * speed));
}

/**
 * Turns gesture outputs into messages (spec §6.2): moves are accelerated and accumulated, then
 * sent at most once per animation frame as one mouse.move; scrolling likewise. Fractions carry
 * over, so slow moves aren't lost. A button flushes pending movement first, keeping order.
 * Scrolling is "natural": the content follows the fingers.
 */
export class PointerPipeline {
  private moveX = 0;
  private moveY = 0;
  private scrollX = 0;
  private scrollY = 0;
  private lastMoveAt: number | null = null;
  private scheduled = false;

  constructor(
    private readonly send: (message: Message) => void,
    private readonly schedule: (flush: () => void) => void,
  ) {}

  push(outputs: GestureOutput[]): void {
    for (const o of outputs) {
      switch (o.type) {
        case 'move': {
          const dt = this.lastMoveAt === null ? 16 : Math.min(Math.max(o.t - this.lastMoveAt, MIN_MOVE_INTERVAL_MS), 100);
          this.lastMoveAt = o.t;
          const g = gain(o.dx, o.dy, dt);
          this.moveX += o.dx * g;
          this.moveY += o.dy * g;
          this.requestFlush();
          break;
        }
        case 'scroll':
          this.scrollX -= o.dx * SCROLL_UNITS_PER_POINT;
          this.scrollY += o.dy * SCROLL_UNITS_PER_POINT;
          this.requestFlush();
          break;
        case 'button':
          this.flush();
          this.send({ type: 'mouse.button', button: o.button, down: o.down });
          break;
      }
    }
  }

  flush(): void {
    this.scheduled = false;
    const dx = Math.trunc(this.moveX);
    const dy = Math.trunc(this.moveY);
    if (dx !== 0 || dy !== 0) {
      this.moveX -= dx;
      this.moveY -= dy;
      this.send({ type: 'mouse.move', dx, dy });
    }
    const sx = Math.trunc(this.scrollX);
    const sy = Math.trunc(this.scrollY);
    if (sx !== 0 || sy !== 0) {
      this.scrollX -= sx;
      this.scrollY -= sy;
      this.send({ type: 'mouse.scroll', dx: sx, dy: sy });
    }
  }

  private requestFlush(): void {
    if (this.scheduled) return;
    this.scheduled = true;
    this.schedule(() => this.flush());
  }
}
