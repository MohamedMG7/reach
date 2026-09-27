import Ionicons from '@expo/vector-icons/Ionicons';
import { Redirect, router } from 'expo-router';
import { Tabs } from 'expo-router/js-tabs';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { Status } from '../../net/client';
import { useClientState, useReach } from '../../state/reach';
import { colors, radius } from '../../ui/theme';

const DOT: Record<Status, string> = { connected: colors.connected, reconnecting: colors.connecting, offline: colors.offline };

/** The PC name in a pill with a green / yellow / red connection dot (spec §6.1). */
function StatusTitle() {
  const { client, pc } = useReach();
  const state = useClientState(client);
  return (
    <View style={styles.pill} accessibilityLabel={`${state.pcName || pc?.pcName}: ${state.status}`}>
      <View style={[styles.dot, { backgroundColor: DOT[state.status] }]} />
      <Text style={styles.pillText} numberOfLines={1}>
        {state.pcName || pc?.pcName}
      </Text>
    </View>
  );
}

export default function TabsLayout() {
  const { pc } = useReach();
  if (pc === undefined) return <View style={styles.blank} />; // locked, or loading the pairing
  if (pc === null) return <Redirect href="/pair" />;
  return (
    <Tabs
      screenOptions={{
        headerTitle: () => <StatusTitle />,
        headerTitleAlign: 'center',
        headerStyle: { backgroundColor: colors.background },
        headerShadowVisible: false,
        headerRight: () => (
          <Pressable accessibilityRole="button" style={styles.pair} onPress={() => router.push('/pair')}>
            <Ionicons name="qr-code-outline" size={16} color={colors.accent} />
            <Text style={styles.pairText}>Pair</Text>
          </Pressable>
        ),
        tabBarActiveTintColor: colors.accent,
        tabBarInactiveTintColor: colors.muted,
        tabBarStyle: { backgroundColor: colors.background, borderTopColor: colors.border },
        tabBarLabelStyle: { fontSize: 11, fontWeight: '600' },
        sceneStyle: { backgroundColor: colors.background },
      }}
    >
      <Tabs.Screen
        name="index"
        options={{ title: 'Trackpad', tabBarIcon: ({ color, size, focused }) => <Ionicons name={focused ? 'hand-left' : 'hand-left-outline'} color={color} size={size} /> }}
      />
      <Tabs.Screen
        name="terminal"
        options={{ title: 'Terminal', tabBarIcon: ({ color, size, focused }) => <Ionicons name={focused ? 'terminal' : 'terminal-outline'} color={color} size={size} /> }}
      />
      <Tabs.Screen
        name="commands"
        options={{ title: 'Commands', tabBarIcon: ({ color, size, focused }) => <Ionicons name={focused ? 'flash' : 'flash-outline'} color={color} size={size} /> }}
      />
    </Tabs>
  );
}

const styles = StyleSheet.create({
  blank: { flex: 1, backgroundColor: colors.background },
  pill: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 8,
    maxWidth: 220,
    paddingVertical: 6,
    paddingHorizontal: 14,
    borderRadius: 999,
    backgroundColor: colors.surface,
    borderWidth: 1,
    borderColor: colors.border,
  },
  dot: { width: 9, height: 9, borderRadius: 5 },
  pillText: { color: colors.text, fontSize: 15, fontWeight: '600' },
  pair: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    marginRight: 12,
    paddingVertical: 6,
    paddingHorizontal: 12,
    borderRadius: radius.small,
    backgroundColor: colors.accentSoft,
  },
  pairText: { color: colors.accent, fontSize: 15, fontWeight: '600' },
});
