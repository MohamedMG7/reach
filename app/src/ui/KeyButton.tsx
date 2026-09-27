import Ionicons from '@expo/vector-icons/Ionicons';
import { Pressable, StyleSheet, Text } from 'react-native';
import type { ModifierMode } from '../core/keyboard';
import { colors, radius } from './theme';

interface Props {
  label: string;
  /** Shown instead of the label text, which remains the accessibility label. */
  icon?: keyof typeof Ionicons.glyphMap;
  onPress(): void;
  onLongPress?(): void;
  /** For modifiers: armed keys are outlined in the accent colour, locked keys filled with it. */
  mode?: ModifierMode;
}

/** One key cap of an accessory row. */
export function KeyButton({ label, icon, onPress, onLongPress, mode = 'off' }: Props) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityState={{ selected: mode !== 'off' }}
      onPress={onPress}
      onLongPress={onLongPress}
      style={({ pressed }) => [styles.key, mode === 'armed' && styles.armed, mode === 'locked' && styles.locked, pressed && styles.pressed]}
    >
      {icon ? (
        <Ionicons name={icon} size={18} color={colors.text} />
      ) : (
        <Text style={[styles.label, mode === 'armed' && styles.armedLabel]}>{label}</Text>
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  key: {
    minWidth: 40,
    height: 38,
    paddingHorizontal: 10,
    borderRadius: radius.small,
    backgroundColor: colors.surfaceRaised,
    borderWidth: 1,
    borderColor: colors.border,
    borderBottomWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  armed: { backgroundColor: colors.accentSoft, borderColor: colors.accent },
  locked: { backgroundColor: colors.accent, borderColor: colors.accentStrong },
  pressed: { opacity: 0.6, transform: [{ translateY: 1 }] },
  label: { color: colors.text, fontSize: 14, fontWeight: '600' },
  armedLabel: { color: colors.accent },
});
