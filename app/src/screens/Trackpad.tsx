import Ionicons from '@expo/vector-icons/Ionicons';
import * as Haptics from 'expo-haptics';
import { activateKeepAwakeAsync, deactivateKeepAwake } from 'expo-keep-awake';
import { useFocusEffect } from 'expo-router';
import { useCallback, useEffect, useMemo, useRef } from 'react';
import { type GestureResponderEvent, Platform, Pressable, StyleSheet, Text, View } from 'react-native';
import { type GestureOutput, initialGestureState, nextDeadline, recognize, type TouchInput } from '../core/gestures';
import { PointerPipeline, STRIP_SCROLL_SPEED } from '../core/pointer';
import { useReach } from '../state/reach';
import { ConnectionBanner } from '../ui/ConnectionBanner';
import { type KeyboardHandle, KeyboardInput } from '../ui/KeyboardInput';
import { colors, radius } from '../ui/theme';
import { useKeyboardOverlap } from '../ui/useKeyboardOverlap';

const KEEP_AWAKE_TAG = 'trackpad';

const isClick = (o: GestureOutput) => o.type === 'button' && o.down;

/** Trackpad, click buttons and the keyboard (spec §6.2, §6.3). */
export default function TrackpadScreen() {
  const { client } = useReach();
  // Each touch event is sent as it arrives: waiting for the next frame only added lag.
  const pipeline = useMemo(() => new PointerPipeline((m) => client?.send(m), (flush) => flush()), [client]);
  const gesture = useRef(initialGestureState);
  const tick = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const keyboard = useRef<KeyboardHandle>(null);
  // On Android the key row is part of this screen, so the screen lifts itself above the keyboard.
  // (On iOS the row rides on the keyboard, which covers the lower part of the trackpad.)
  const container = useRef<View>(null);
  const overlap = useKeyboardOverlap(container);

  useEffect(() => () => clearTimeout(tick.current), []);

  // Screen stays awake while this tab is visible (spec §6.2).
  useFocusEffect(
    useCallback(() => {
      void activateKeepAwakeAsync(KEEP_AWAKE_TAG);
      return () => void deactivateKeepAwake(KEEP_AWAKE_TAG);
    }, []),
  );

  const feed = (input: TouchInput) => {
    const now = Date.now();
    const { state, outputs } = recognize(gesture.current, input, now);
    gesture.current = state;
    if (outputs.some(isClick)) void Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    pipeline.push(outputs);
    clearTimeout(tick.current);
    const deadline = nextDeadline(state);
    if (deadline !== null) tick.current = setTimeout(() => feed({ type: 'tick' }), Math.max(0, deadline - now));
  };

  const touches = (type: 'down' | 'move' | 'up') => (event: GestureResponderEvent) => {
    for (const t of event.nativeEvent.changedTouches) {
      const id = Number(t.identifier);
      feed(type === 'up' ? { type, id } : { type, id, x: t.pageX, y: t.pageY });
    }
  };

  // The scroll strip: one finger, vertical only, faster than two-finger scrolling.
  const stripY = useRef<number | null>(null);
  const onStripTouch = (event: GestureResponderEvent) => {
    const y = event.nativeEvent.changedTouches[0]?.pageY;
    if (y === undefined) return;
    if (stripY.current !== null) pipeline.push([{ type: 'scroll', dx: 0, dy: (y - stripY.current) * STRIP_SCROLL_SPEED }]);
    stripY.current = y;
  };

  const button = (name: 'left' | 'right', down: boolean) => {
    if (down) void Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    client?.send({ type: 'mouse.button', button: name, down });
  };

  return (
    <View ref={container} style={[styles.screen, Platform.OS === 'android' && { paddingBottom: overlap }]}>
      <ConnectionBanner />
      <View style={styles.pads}>
        <View
          accessibilityLabel="Trackpad"
          style={styles.surface}
          onTouchStart={touches('down')}
          onTouchMove={touches('move')}
          onTouchEnd={touches('up')}
          onTouchCancel={touches('up')}
        >
          <Text style={styles.hint}>One finger to move · tap to click · two fingers to scroll · tap, then drag</Text>
        </View>
        <View
          accessibilityLabel="Scroll strip"
          style={styles.strip}
          onTouchStart={onStripTouch}
          onTouchMove={onStripTouch}
          onTouchEnd={() => (stripY.current = null)}
          onTouchCancel={() => (stripY.current = null)}
        >
          <Ionicons name="chevron-up" size={16} color={colors.muted} style={styles.stripIcon} />
          <View style={styles.grip} />
          <Ionicons name="chevron-down" size={16} color={colors.muted} style={styles.stripIcon} />
        </View>
      </View>
      <View style={styles.buttons}>
        <Pressable
          accessibilityLabel="Left click"
          style={({ pressed }) => [styles.mouseButton, pressed && styles.mousePressed]}
          onPressIn={() => button('left', true)}
          onPressOut={() => button('left', false)}
        >
          <Text style={styles.mouseLabel}>Left</Text>
        </Pressable>
        <Pressable
          accessibilityLabel="Keyboard"
          style={({ pressed }) => [styles.keyboardButton, pressed && styles.mousePressed]}
          onPress={() => keyboard.current?.toggle()}
        >
          <Ionicons name="keypad" size={24} color={colors.accent} />
        </Pressable>
        <Pressable
          accessibilityLabel="Right click"
          style={({ pressed }) => [styles.mouseButton, pressed && styles.mousePressed]}
          onPressIn={() => button('right', true)}
          onPressOut={() => button('right', false)}
        >
          <Text style={styles.mouseLabel}>Right</Text>
        </Pressable>
      </View>
      <KeyboardInput ref={keyboard} send={(m) => client?.send(m)} />
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  pads: { flex: 1, flexDirection: 'row', gap: 8, margin: 12 },
  strip: {
    width: 44,
    borderRadius: radius.large,
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: 14,
  },
  stripIcon: { pointerEvents: 'none' },
  grip: { width: 4, height: 48, borderRadius: 2, backgroundColor: colors.border, pointerEvents: 'none' },
  surface: {
    flex: 1,
    borderRadius: radius.large,
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
    justifyContent: 'flex-end',
    padding: 18,
  },
  hint: { color: colors.muted, textAlign: 'center', fontSize: 12, opacity: 0.8, pointerEvents: 'none' },
  buttons: { flexDirection: 'row', gap: 10, paddingHorizontal: 12, paddingBottom: 12, height: 76 },
  mouseButton: {
    flex: 1,
    borderRadius: radius.medium,
    backgroundColor: colors.surfaceRaised,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
    justifyContent: 'center',
  },
  mousePressed: { backgroundColor: colors.accentSoft, borderColor: colors.accent },
  mouseLabel: { color: colors.muted, fontSize: 13, fontWeight: '600', letterSpacing: 0.5 },
  keyboardButton: {
    width: 68,
    borderRadius: radius.medium,
    backgroundColor: colors.accentSoft,
    borderWidth: 1,
    borderColor: 'rgba(79,124,255,0.35)',
    alignItems: 'center',
    justifyContent: 'center',
  },
});
