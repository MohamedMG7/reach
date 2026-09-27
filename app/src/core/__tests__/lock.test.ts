import { mustRelock, RELOCK_AFTER_MS } from '../lock';

it('relocks only after more than 60 s in the background', () => {
  expect(mustRelock(null, 1_000_000)).toBe(false);
  expect(mustRelock(0, RELOCK_AFTER_MS)).toBe(false);
  expect(mustRelock(0, RELOCK_AFTER_MS + 1)).toBe(true);
});
