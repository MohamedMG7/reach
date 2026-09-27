import { type BarcodeScanningResult, CameraView, useCameraPermissions } from 'expo-camera';
import * as Linking from 'expo-linking';
import { router } from 'expo-router';
import { useEffect, useRef, useState } from 'react';
import { Alert, Pressable, StyleSheet, Text, View } from 'react-native';
import { type PairingInfo, parsePairingUri } from '../core/pairingUri';
import { useReach } from '../state/reach';
import { Logo } from '../ui/Logo';
import { colors, radius } from '../ui/theme';

const INTRO = 'On your PC, open the Reach tray menu and choose "Pair new phone…", then point the camera at the code.';
/** After a bad scan, wait before reading codes again so the message can be read. */
const RESCAN_DELAY_MS = 2000;

/**
 * Back to the tabs: pop this screen when the tabs are under it (opened with the Pair button),
 * since replacing would stack a second, still-mounted copy of them.
 */
const leave = () => (router.canGoBack() ? router.back() : router.replace('/'));

/** Scans the PC's QR code and pairs (spec §3.3). */
export default function PairScreen() {
  const { pair, pc, locked } = useReach();
  const [permission, requestPermission] = useCameraPermissions();
  const [message, setMessage] = useState(INTRO);
  const busy = useRef(false);

  const pairWith = async (info: PairingInfo) => {
    busy.current = true;
    setMessage(`Pairing with ${info.pcName}…`);
    if (await pair(info)) {
      leave();
      return;
    }
    setMessage(
      `Couldn't pair with ${info.pcName}. Check this phone is on the same Wi-Fi as the PC, then choose "Pair new phone…" again for a fresh code.`,
    );
    setTimeout(() => (busy.current = false), RESCAN_DELAY_MS);
  };

  const onScanned = async ({ data }: BarcodeScanningResult) => {
    if (busy.current) return;
    busy.current = true;
    const parsed = parsePairingUri(data);
    if (!parsed.ok) {
      setMessage(parsed.error);
      setTimeout(() => (busy.current = false), RESCAN_DELAY_MS);
      return;
    }
    await pairWith(parsed.info);
  };

  // The PC's code scanned with the phone's own Camera app opens Reach with the reach://pair link
  // (in an installed build; Expo Go opens its own exp:// links). Any app or web page can open such
  // a link, so it never pairs without asking, and only once Face ID has unlocked Reach.
  const link = Linking.useURL();
  const handledLink = useRef<string | null>(null);
  useEffect(() => {
    if (locked || !link?.startsWith('reach://pair') || handledLink.current === link) return;
    handledLink.current = link;
    const parsed = parsePairingUri(link);
    if (!parsed.ok) {
      setMessage(parsed.error);
      return;
    }
    const { info } = parsed;
    Alert.alert(`Pair with ${info.pcName}?`, `Only pair with a PC you own. ${info.pcName} will be able to receive what you type and do on this phone's trackpad.`, [
      { text: 'Cancel', style: 'cancel' },
      { text: 'Pair', onPress: () => void pairWith(info) },
    ]);
  }, [link, locked]);

  if (!permission) return <View style={styles.screen} />;
  if (!permission.granted) {
    return (
      <View style={[styles.screen, styles.center]}>
        <Logo size={72} />
        <Text style={styles.message}>Reach needs the camera to scan the pairing code shown on your PC.</Text>
        <Pressable accessibilityRole="button" style={styles.button} onPress={requestPermission}>
          <Text style={styles.buttonText}>Allow camera</Text>
        </Pressable>
      </View>
    );
  }
  return (
    <View style={styles.screen}>
      <View style={styles.camera}>
        <CameraView style={styles.fill} facing="back" barcodeScannerSettings={{ barcodeTypes: ['qr'] }} onBarcodeScanned={onScanned} />
        <View style={styles.overlay}>
          <Text style={styles.title}>Pair with your PC</Text>
          <View style={styles.frame} />
        </View>
      </View>
      <View style={styles.panel}>
        <Text style={styles.message}>{message}</Text>
        {pc && (
          <Pressable accessibilityRole="button" onPress={leave} style={styles.cancel}>
            <Text style={styles.link}>Cancel</Text>
          </Pressable>
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background },
  center: { alignItems: 'center', justifyContent: 'center', gap: 24, padding: 32 },
  camera: { flex: 1, overflow: 'hidden' },
  fill: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0 },
  overlay: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, pointerEvents: 'none', alignItems: 'center', justifyContent: 'center', gap: 28 },
  title: { color: '#fff', fontSize: 22, fontWeight: '700', textShadowColor: 'rgba(0,0,0,0.6)', textShadowRadius: 8 },
  frame: { width: 240, height: 240, borderRadius: radius.large, borderWidth: 3, borderColor: colors.accent },
  panel: {
    padding: 24,
    paddingBottom: 36,
    gap: 16,
    alignItems: 'center',
    backgroundColor: colors.surface,
    borderTopLeftRadius: radius.large,
    borderTopRightRadius: radius.large,
    marginTop: -radius.large,
  },
  message: { color: colors.text, fontSize: 16, lineHeight: 22, textAlign: 'center' },
  button: { paddingHorizontal: 28, paddingVertical: 14, borderRadius: radius.medium, backgroundColor: colors.accent },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  cancel: { paddingVertical: 6, paddingHorizontal: 16 },
  link: { color: colors.accent, fontSize: 16, fontWeight: '600' },
});
