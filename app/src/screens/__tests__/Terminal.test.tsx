import { generateKeyPair } from '@reach/protocol';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react-native';
import { encodeBase64Url } from '../../core/base64url';
import { ReachClient } from '../../net/client';
import { ReachContext } from '../../state/reach';
import { FakePc } from '../../testing/fakePc';
import TerminalScreen from '../Terminal';

jest.mock('expo-router', () => ({ useIsFocused: () => true, router: { push: jest.fn() } }));

// The page itself runs only on the phone; here the WebView records what the app injects and
// lets the test post page events.
const page: { injected: string[]; reloads: number; post?: (data: string) => void; kill?: () => void } = { injected: [], reloads: 0 };
jest.mock('react-native-webview', () => {
  const { useImperativeHandle } = jest.requireActual('react');
  return {
    WebView: ({
      ref,
      onMessage,
      onContentProcessDidTerminate,
    }: {
      ref: unknown;
      onMessage: (e: { nativeEvent: { data: string } }) => void;
      onContentProcessDidTerminate?: () => void;
    }) => {
      useImperativeHandle(ref, () => ({ injectJavaScript: (js: string) => page.injected.push(js), reload: () => page.reloads++ }));
      page.post = (data) => onMessage({ nativeEvent: { data } });
      page.kill = () => onContentProcessDidTerminate?.();
      return null;
    },
  };
});

let pc: FakePc;
let client: ReachClient;

const post = (event: object) => act(async () => page.post!(JSON.stringify(event)));
const opens = () => pc.received.filter((m) => m.type === 'term.open');

async function renderTerminal() {
  const phoneKey = generateKeyPair();
  pc = new FakePc();
  pc.paired.add(encodeBase64Url(phoneKey.publicKey));
  client = new ReachClient({ hosts: ['192.168.1.20'], port: 47800, pcKey: pc.key.publicKey, phoneKey, deviceName: 'x', appVersion: '1', createSocket: pc.createSocket });
  client.start();
  await waitFor(() => expect(client.state.status).toBe('connected'));
  await render(
    <ReachContext.Provider value={{ locked: false, unlock: async () => null, pc: null, client, pair: async () => false }}>
      <TerminalScreen />
    </ReachContext.Provider>,
  );
}

beforeEach(() => {
  page.injected = [];
  page.reloads = 0;
});

afterEach(() => act(async () => client.stop()));

it('opens the terminal at the size the page fitted, and resizes it after', async () => {
  await renderTerminal();
  expect(opens()).toEqual([]); // no size yet

  await post({ type: 'size', cols: 80, rows: 24 });
  await post({ type: 'size', cols: 60, rows: 30 });

  await waitFor(() => expect(pc.received).toContainEqual({ type: 'term.resize', cols: 60, rows: 30 }));
  expect(opens()).toEqual([{ type: 'term.open', cols: 80, rows: 24 }]);
});

it('clears the screen on term.opened and writes the output', async () => {
  await renderTerminal();
  await post({ type: 'size', cols: 80, rows: 24 });

  await act(async () => {
    pc.send(pc.live!, { type: 'term.opened', resumed: true });
    pc.send(pc.live!, { type: 'term.output', data: 'PS C:\\> ' });
  });

  await waitFor(() => expect(page.injected).toContain('window.reach.write("PS C:\\\\> ");true;'));
  expect(page.injected[0]).toBe('window.reach.reset();true;');
});

it('re-attaches to the shell after reconnecting', async () => {
  await renderTerminal();
  await post({ type: 'size', cols: 80, rows: 24 });
  await waitFor(() => expect(opens()).toHaveLength(1));

  await act(async () => pc.live!.drop());

  await waitFor(() => expect(opens()).toHaveLength(2), { timeout: 3000 });
});

it('reloads the page when iOS ends it, and re-attaches once the new page reports its size', async () => {
  await renderTerminal();
  await post({ type: 'size', cols: 80, rows: 24 });
  await waitFor(() => expect(opens()).toHaveLength(1));

  await act(async () => page.kill!());
  expect(page.reloads).toBe(1);
  await post({ type: 'size', cols: 80, rows: 24 });

  await waitFor(() => expect(opens()).toHaveLength(2));
  expect(pc.received).not.toContainEqual({ type: 'term.resize', cols: 80, rows: 24 });
});

it('sends typed input, with a sticky Ctrl turning c into Ctrl+C', async () => {
  await renderTerminal();
  await post({ type: 'size', cols: 80, rows: 24 });

  await post({ type: 'input', data: 'ls\r' });
  await fireEvent.press(screen.getByLabelText('Ctrl'));
  await post({ type: 'input', data: 'c' });
  await post({ type: 'input', data: 'c' });

  await waitFor(() =>
    expect(pc.received.filter((m) => m.type === 'term.input')).toEqual([
      { type: 'term.input', data: 'ls\r' },
      { type: 'term.input', data: '\x03' },
      { type: 'term.input', data: 'c' },
    ]),
  );
});

it('hides the keyboard from the key row', async () => {
  await renderTerminal();

  await fireEvent.press(screen.getByLabelText('Hide keyboard'));

  expect(page.injected).toContain('window.reach.blur();true;');
});

it('offers a restart after the shell exits', async () => {
  await renderTerminal();
  await post({ type: 'size', cols: 80, rows: 24 });

  await act(async () => pc.send(pc.live!, { type: 'term.exited', code: 0 }));
  await fireEvent.press(await screen.findByText('[session ended — tap to restart]'));

  await waitFor(() => expect(opens()).toHaveLength(2));
});
