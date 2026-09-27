/** Face ID again after more than this long in the background (spec §3.6). */
export const RELOCK_AFTER_MS = 60_000;

export function mustRelock(backgroundedAt: number | null, now: number): boolean {
  return backgroundedAt !== null && now - backgroundedAt > RELOCK_AFTER_MS;
}
