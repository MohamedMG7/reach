/** Messages the terminal page (assets/terminal.html) posts to the app. */
export type PageEvent = { type: 'size'; cols: number; rows: number } | { type: 'input'; data: string };

const isSize = (n: unknown): n is number => Number.isInteger(n) && (n as number) > 0 && (n as number) <= 1000;

/** Parses a WebView message; null for anything unexpected. */
export function parsePageEvent(text: string): PageEvent | null {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return null;
  }
  if (typeof value !== 'object' || value === null) return null;
  const v = value as Record<string, unknown>;
  if (v.type === 'size' && isSize(v.cols) && isSize(v.rows)) return { type: 'size', cols: v.cols, rows: v.rows };
  if (v.type === 'input' && typeof v.data === 'string') return { type: 'input', data: v.data };
  return null;
}

/** JavaScript for WebView.injectJavaScript. JSON.stringify makes any output text a safe string literal. */
export const writeScript = (data: string) => `window.reach.write(${JSON.stringify(data)});true;`;
export const resetScript = 'window.reach.reset();true;';
export const focusScript = 'window.reach.focus();true;';
export const blurScript = 'window.reach.blur();true;';
