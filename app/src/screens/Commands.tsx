import Ionicons from '@expo/vector-icons/Ionicons';
import type { CommandInfo } from '@reach/protocol';
import { useIsFocused } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { Alert, Pressable, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useClientState, usePcMessages, useReach } from '../state/reach';
import { ConnectionBanner } from '../ui/ConnectionBanner';
import { colors, radius } from '../ui/theme';
import { useToast } from '../ui/Toast';

const FALLBACK_ICON = 'flash';

/** The icon named in commands.json, if Ionicons has it (the file is hand-edited). */
function iconFor(name: string): keyof typeof Ionicons.glyphMap {
  return (name in Ionicons.glyphMap ? name : FALLBACK_ICON) as keyof typeof Ionicons.glyphMap;
}

/** One-tap saved commands from the PC's commands.json (spec §6.5). */
export default function CommandsScreen() {
  const { client } = useReach();
  const { status, pcName } = useClientState(client);
  const focused = useIsFocused();
  const [commands, setCommands] = useState<CommandInfo[] | null>(null);
  const labels = useRef(new Map<string, string>());
  const toast = useToast();

  useEffect(() => {
    if (focused && status === 'connected') client?.send({ type: 'cmd.list' });
  }, [focused, status, client]);

  usePcMessages(client, (message) => {
    if (message.type === 'cmd.listed') {
      labels.current = new Map(message.commands.map((c) => [c.id, c.label]));
      setCommands(message.commands);
    } else if (message.type === 'cmd.result') {
      const label = labels.current.get(message.id) ?? message.id;
      toast.show(message.ok ? `${label}: done` : `${label} failed: ${message.error ?? `exit code ${message.exitCode}`}`);
    }
  });

  const run = (command: CommandInfo) => {
    const go = () => {
      if (!client?.send({ type: 'cmd.run', id: command.id })) toast.show(`Not connected to ${pcName || 'the PC'}.`);
    };
    if (!command.confirm) return go();
    Alert.alert(command.label, `Run "${command.label}" on ${pcName || 'the PC'}?`, [
      { text: 'Cancel', style: 'cancel' },
      { text: 'Run', style: 'destructive', onPress: go },
    ]);
  };

  const connected = status === 'connected';
  return (
    <View style={styles.screen}>
      <ConnectionBanner />
      <ScrollView contentContainerStyle={styles.grid}>
        {commands?.map((command) => (
          <Pressable
            key={command.id}
            accessibilityRole="button"
            accessibilityLabel={command.label}
            disabled={!connected}
            onPress={() => run(command)}
            style={({ pressed }) => [styles.tile, !connected && styles.disabled, pressed && styles.pressed]}
          >
            <View style={styles.iconCircle}>
              <Ionicons name={iconFor(command.icon)} size={26} color={colors.accent} />
            </View>
            <Text style={styles.label} numberOfLines={2}>
              {command.label}
            </Text>
          </Pressable>
        ))}
        {commands?.length === 0 && <Text style={styles.empty}>No saved commands. Add some with "Edit commands" in the Reach tray menu.</Text>}
      </ScrollView>
      {toast.element}
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  grid: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'space-between', rowGap: 12, padding: 12 },
  tile: {
    width: '48.5%',
    aspectRatio: 1.25,
    borderRadius: radius.large,
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
    padding: 12,
  },
  iconCircle: { width: 52, height: 52, borderRadius: 26, backgroundColor: colors.accentSoft, alignItems: 'center', justifyContent: 'center' },
  disabled: { opacity: 0.4 },
  pressed: { borderColor: colors.accent, backgroundColor: colors.surfaceRaised },
  label: { color: colors.text, fontSize: 15, fontWeight: '600', textAlign: 'center' },
  empty: { color: colors.muted, padding: 12, lineHeight: 20 },
});
