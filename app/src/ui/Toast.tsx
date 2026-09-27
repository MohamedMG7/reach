import { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { colors, radius } from './theme';

export const TOAST_MS = 3000;

/** A short message over the bottom of the screen: `show` it, render `element`. */
export function useToast(): { element: React.ReactNode; show(text: string): void } {
  const [text, setText] = useState<string | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const show = useCallback((next: string) => {
    clearTimeout(timer.current);
    setText(next);
    timer.current = setTimeout(() => setText(null), TOAST_MS);
  }, []);
  const element =
    text === null ? null : (
      <View style={styles.toast} accessibilityLiveRegion="polite">
        <Text style={styles.text}>{text}</Text>
      </View>
    );
  return { element, show };
}

const styles = StyleSheet.create({
  toast: {
    pointerEvents: 'none',
    position: 'absolute',
    left: 16,
    right: 16,
    bottom: 24,
    paddingVertical: 12,
    paddingHorizontal: 16,
    borderRadius: radius.medium,
    backgroundColor: colors.surfaceRaised,
    borderWidth: 1,
    borderColor: colors.border,
  },
  text: { color: colors.text, textAlign: 'center', fontSize: 15 },
});
