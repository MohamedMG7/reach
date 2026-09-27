import { useIsFocused } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { WebView } from 'react-native-webview';
import { type ModifierMode, nextModifierMode, TERMINAL_KEYS, terminalInput } from '../core/keyboard';
import { useClientState, usePcMessages, useReach } from '../state/reach';
import { blurScript, focusScript, parsePageEvent, resetScript, writeScript } from '../terminal/bridge';
import { terminalPage } from '../terminal/terminalPage.generated';
import { ConnectionBanner } from '../ui/ConnectionBanner';
import { KeyButton } from '../ui/KeyButton';
import { colors, radius } from '../ui/theme';
import { useKeyboardOverlap } from '../ui/useKeyboardOverlap';

const KEYS: [string, keyof typeof TERMINAL_KEYS][] = [
  ['Esc', 'esc'],
  ['Tab', 'tab'],
  ['←', 'left'],
  ['↑', 'up'],
  ['↓', 'down'],
  ['→', 'right'],
];

/**
 * PowerShell on the PC in xterm.js (spec §6.4). Opens the terminal when the tab is visible and
 * connected, and again after every reconnect, which re-attaches to the same shell and replays
 * its recent output.
 */
export default function TerminalScreen() {
  const { client } = useReach();
  const { status } = useClientState(client);
  const focused = useIsFocused();
  const web = useRef<WebView>(null);
  const container = useRef<View>(null);
  const overlap = useKeyboardOverlap(container);
  const size = useRef<{ cols: number; rows: number } | null>(null);
  const attached = useRef(false);
  const output = useRef('');
  const flushing = useRef(false);
  const [ctrl, setCtrl] = useState<ModifierMode>('off');
  const ctrlRef = useRef<ModifierMode>('off');
  const [exited, setExited] = useState(false);

  const open = () => {
    if (!size.current || !client) return;
    if (client.send({ type: 'term.open', ...size.current })) {
      attached.current = true;
      setExited(false);
    }
  };

  useEffect(() => {
    if (status !== 'connected') attached.current = false;
    else if (focused && !attached.current) open();
  }, [status, focused]);

  usePcMessages(client, (message) => {
    switch (message.type) {
      case 'term.opened':
        output.current = ''; // the PC replays what the screen should show
        web.current?.injectJavaScript(resetScript);
        break;
      case 'term.output':
        output.current += message.data;
        if (!flushing.current) {
          flushing.current = true;
          requestAnimationFrame(() => {
            flushing.current = false;
            const data = output.current;
            output.current = '';
            if (data) web.current?.injectJavaScript(writeScript(data));
          });
        }
        break;
      case 'term.exited':
        attached.current = false;
        setExited(true);
        break;
    }
  });

  const sendInput = (data: string) => {
    if (!exited) client?.send({ type: 'term.input', data });
  };

  const setCtrlMode = (mode: ModifierMode) => {
    ctrlRef.current = mode;
    setCtrl(mode);
  };

  const onPageMessage = (text: string) => {
    const event = parsePageEvent(text);
    if (event?.type === 'size') {
      size.current = { cols: event.cols, rows: event.rows };
      if (attached.current) client?.send({ type: 'term.resize', ...size.current });
      else if (focused && status === 'connected' && !exited) open();
    } else if (event?.type === 'input') {
      const result = terminalInput(ctrlRef.current, event.data);
      setCtrlMode(result.ctrl);
      sendInput(result.data);
    }
  };

  return (
    <View ref={container} style={[styles.screen, { paddingBottom: overlap }]}>
      <ConnectionBanner />
      <WebView
        ref={web}
        style={styles.web}
        source={{ html: terminalPage }}
        originWhitelist={['*']}
        onMessage={(e) => onPageMessage(e.nativeEvent.data)}
        onContentProcessDidTerminate={() => {
          // iOS ended the page (memory pressure in the background). Reload it; its first size
          // message opens the terminal again, which re-attaches to the shell and replays it.
          attached.current = false;
          size.current = null;
          web.current?.reload();
        }}
        scrollEnabled={false}
        bounces={false}
        hideKeyboardAccessoryView
        keyboardDisplayRequiresUserAction={false}
      />
      {exited && (
        <Pressable accessibilityRole="button" style={styles.exited} onPress={open}>
          <Text style={styles.exitedText}>[session ended — tap to restart]</Text>
        </Pressable>
      )}
      <ScrollView horizontal keyboardShouldPersistTaps="always" style={styles.bar} contentContainerStyle={styles.row}>
        <KeyButton label="Hide keyboard" icon="chevron-down" onPress={() => web.current?.injectJavaScript(blurScript)} />
        <KeyButton label="Ctrl" mode={ctrl} onPress={() => setCtrlMode(nextModifierMode(ctrlRef.current))} />
        {KEYS.map(([label, key]) => (
          <KeyButton key={key} label={label} onPress={() => sendInput(TERMINAL_KEYS[key])} />
        ))}
        <KeyButton label="⌨" onPress={() => web.current?.injectJavaScript(focusScript)} />
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  web: { flex: 1, backgroundColor: colors.background },
  exited: {
    position: 'absolute',
    left: 16,
    right: 16,
    bottom: 72,
    padding: 16,
    alignItems: 'center',
    borderRadius: radius.medium,
    backgroundColor: colors.surfaceRaised,
    borderWidth: 1,
    borderColor: colors.accent,
  },
  exitedText: { color: colors.text, fontFamily: 'Menlo' },
  bar: { flexGrow: 0, backgroundColor: colors.surface, borderTopWidth: 1, borderTopColor: colors.border },
  row: { gap: 6, padding: 8 },
});
