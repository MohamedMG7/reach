import { router } from 'expo-router';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import type { ClientState } from '../net/client';
import { useClientState, useReach } from '../state/reach';
import { colors, radius } from './theme';

type Action = 'pair' | 'reconnect' | null;

/** What to tell the user about the connection, or null when there's nothing to say (spec §7). */
export function connectionMessage(state: ClientState, pcName: string): { text: string; action: Action } | null {
  switch (state.problem) {
    case 'not_paired':
      return { text: `This phone isn't paired with ${pcName}.`, action: 'pair' };
    case 'unreachable':
      return { text: `Can't reach ${pcName}. Is it on and on the same Wi-Fi? If its address changed, rescan the QR code.`, action: 'pair' };
    case 'busy':
      return { text: `${state.busyWith || 'Another phone'} is using ${pcName}. Reach connects when it disconnects.`, action: null };
    case 'disconnected':
      return { text: `${pcName} disconnected this phone.`, action: 'reconnect' };
    default:
      return state.status === 'reconnecting' ? { text: `Connecting to ${pcName}…`, action: null } : null;
  }
}

/** Shown at the top of each tab when the PC isn't connected. */
export function ConnectionBanner() {
  const { client, pc } = useReach();
  const state = useClientState(client);
  const message = connectionMessage(state, state.pcName || pc?.pcName || 'your PC');
  if (!message) return null;
  return (
    <View style={styles.banner}>
      <Text style={styles.text}>{message.text}</Text>
      {message.action && (
        <Pressable accessibilityRole="button" onPress={() => (message.action === 'pair' ? router.push('/pair') : client?.start())}>
          <Text style={styles.action}>{message.action === 'pair' ? 'Pair' : 'Reconnect'}</Text>
        </Pressable>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  banner: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    marginHorizontal: 12,
    marginTop: 8,
    paddingVertical: 10,
    paddingHorizontal: 14,
    borderRadius: radius.medium,
    backgroundColor: colors.warningBackground,
    borderWidth: 1,
    borderColor: 'rgba(255,214,10,0.25)',
  },
  text: { flex: 1, color: colors.connecting, fontSize: 14 },
  action: { color: colors.accent, fontWeight: '700', fontSize: 15 },
});
