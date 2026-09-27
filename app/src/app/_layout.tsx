import '../polyfills';
import { Stack } from 'expo-router';
import { StatusBar } from 'expo-status-bar';
import { View } from 'react-native';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { ReachProvider, useReach } from '../state/reach';
import { LockScreen } from '../ui/LockScreen';
import { colors } from '../ui/theme';

export default function RootLayout() {
  return (
    <SafeAreaProvider>
      <ReachProvider>
        <StatusBar style="light" />
        <Gate />
      </ReachProvider>
    </SafeAreaProvider>
  );
}

/** The screens stay mounted underneath; the lock screen covers them until Face ID succeeds. */
function Gate() {
  const { locked } = useReach();
  return (
    <View style={{ flex: 1, backgroundColor: colors.background }}>
      <Stack screenOptions={{ headerShown: false, contentStyle: { backgroundColor: colors.background } }} />
      {locked && <LockScreen />}
    </View>
  );
}
