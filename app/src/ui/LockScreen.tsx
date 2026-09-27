import { useCallback, useEffect, useState } from 'react';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { useReach } from '../state/reach';
import { Logo } from './Logo';
import { colors, radius } from './theme';

/** Covers the app until Face ID (or the passcode) succeeds (spec §3.6). Asks once on appearing. */
export function LockScreen() {
  const { unlock } = useReach();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const attempt = useCallback(async () => {
    setBusy(true);
    setError(await unlock());
    setBusy(false);
  }, [unlock]);

  useEffect(() => {
    void attempt();
  }, [attempt]);

  return (
    <View style={styles.screen}>
      <Logo size={96} />
      <View style={styles.titles}>
        <Text style={styles.title}>Reach</Text>
        <Text style={styles.subtitle}>Your PC, in your hand</Text>
      </View>
      {error && <Text style={styles.error}>{error}</Text>}
      <Pressable
        accessibilityRole="button"
        disabled={busy}
        onPress={attempt}
        style={({ pressed }) => [styles.button, (pressed || busy) && styles.dimmed]}
      >
        <Text style={styles.buttonText}>Unlock</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 28,
    padding: 32,
    backgroundColor: colors.background,
  },
  titles: { alignItems: 'center', gap: 6 },
  title: { color: colors.text, fontSize: 34, fontWeight: '700', letterSpacing: 0.5 },
  subtitle: { color: colors.muted, fontSize: 15 },
  error: { color: colors.offline, textAlign: 'center' },
  button: { minWidth: 200, alignItems: 'center', paddingVertical: 15, borderRadius: radius.medium, backgroundColor: colors.accent },
  dimmed: { opacity: 0.6 },
  buttonText: { color: '#fff', fontSize: 17, fontWeight: '600' },
});
