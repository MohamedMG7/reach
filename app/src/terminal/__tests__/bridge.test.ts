import { parsePageEvent, writeScript } from '../bridge';

it('parses size and input events from the page', () => {
  expect(parsePageEvent('{"type":"size","cols":80,"rows":24}')).toEqual({ type: 'size', cols: 80, rows: 24 });
  expect(parsePageEvent('{"type":"input","data":"ls\\r"}')).toEqual({ type: 'input', data: 'ls\r' });
});

it.each(['not json', 'null', '[]', '{"type":"size","cols":0,"rows":24}', '{"type":"size","cols":80.5,"rows":24}', '{"type":"input"}', '{"type":"eval"}'])(
  'ignores %p',
  (text) => {
    expect(parsePageEvent(text)).toBeNull();
  },
);

it('passes any output text to the page as a string literal', () => {
  const nasty = 'a"b\\c\n </script>\x1b[31m👋';
  const script = writeScript(nasty);
  const prefix = 'window.reach.write(';
  const suffix = ');true;';
  expect(script.startsWith(prefix) && script.endsWith(suffix)).toBe(true);
  expect(JSON.parse(script.slice(prefix.length, -suffix.length))).toBe(nasty);
  expect(script).not.toMatch(/[\n\r]/); // one line, so the literal can't be broken out of
});
