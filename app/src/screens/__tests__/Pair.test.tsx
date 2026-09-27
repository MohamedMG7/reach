import { act, fireEvent, render, screen } from '@testing-library/react-native';
import * as Linking from 'expo-linking';
import { router } from 'expo-router';
import { Alert } from 'react-native';
import type { PairingInfo } from '../../core/pairingUri';
import { ReachContext } from '../../state/reach';
import type { PairedPc } from '../../storage/pairing';
import PairScreen from '../Pair';

jest.mock('expo-router', () => ({ router: { back: jest.fn(), replace: jest.fn(), canGoBack: jest.fn() } }));
jest.mock('expo-linking', () => ({ useURL: jest.fn(() => null) }));

const camera: { scan?: (data: string) => Promise<void> } = {};
jest.mock('expo-camera', () => ({
  useCameraPermissions: () => [{ granted: true }, jest.fn()],
  CameraView: ({ onBarcodeScanned }: { onBarcodeScanned: (r: { data: string }) => Promise<void> }) => {
    camera.scan = (data) => onBarcodeScanned({ data });
    return null;
  },
}));

const CODE =
  'reach://pair?v=1&h=192.168.1.20&p=47800&k=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&c=AAECAwQFBgcICQoLDA0ODw&n=DESKTOP';
const PAIRED: PairedPc = { hosts: ['192.168.1.20'], port: 47800, pcKey: 'A'.repeat(43), pcName: 'DESKTOP' };

const pair = jest.fn(async (_info: PairingInfo) => true);

const app = (pc: PairedPc | null, locked = false) => (
  <ReachContext.Provider value={{ locked, unlock: async () => null, pc, client: null, pair }}>
    <PairScreen />
  </ReachContext.Provider>
);

async function renderPair(pc: PairedPc | null, canGoBack: boolean, locked = false) {
  (router.canGoBack as jest.Mock).mockReturnValue(canGoBack);
  return render(app(pc, locked));
}

/** The link the Camera app opens Reach with. */
const openedWith = (url: string | null) => (Linking.useURL as jest.Mock).mockReturnValue(url);

beforeEach(() => {
  jest.clearAllMocks();
  openedWith(null);
});

describe('opened from the Camera app with the PC’s code', () => {
  it('asks before pairing, and pairs when you confirm', async () => {
    const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
    openedWith(CODE);
    await renderPair(PAIRED, true);

    expect(alert).toHaveBeenCalledWith('Pair with DESKTOP?', expect.any(String), expect.any(Array));
    expect(pair).not.toHaveBeenCalled();

    const buttons = alert.mock.calls[0][2]!;
    await act(async () => buttons.find((b) => b.text === 'Pair')!.onPress!());

    expect(pair).toHaveBeenCalledWith(expect.objectContaining({ pcName: 'DESKTOP', hosts: ['192.168.1.20'], code: 'AAECAwQFBgcICQoLDA0ODw' }));
    expect(router.back).toHaveBeenCalled();
  });

  it('does nothing when you cancel', async () => {
    const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
    openedWith(CODE);
    await renderPair(PAIRED, true);

    await act(async () => alert.mock.calls[0][2]!.find((b) => b.text === 'Cancel')!.onPress?.());

    expect(pair).not.toHaveBeenCalled();
  });

  it('waits until Reach is unlocked', async () => {
    const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
    openedWith(CODE);
    const view = await renderPair(PAIRED, true, true);
    expect(alert).not.toHaveBeenCalled();

    await view.rerender(app(PAIRED, false));

    expect(alert).toHaveBeenCalledTimes(1);
  });

  it('explains a link that is not a usable code', async () => {
    const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
    openedWith(CODE.replace('v=1', 'v=2'));
    await renderPair(PAIRED, true);

    expect(await screen.findByText('This pairing code needs a newer version of the Reach app.')).toBeTruthy();
    expect(alert).not.toHaveBeenCalled();
  });

  it('ignores other links, such as the one Expo Go opens the app with', async () => {
    const alert = jest.spyOn(Alert, 'alert').mockImplementation(() => {});
    openedWith('exp://192.168.1.12:8081');
    await renderPair(PAIRED, true);

    expect(alert).not.toHaveBeenCalled();
  });
});

it('goes back to the tabs on Cancel instead of stacking a second copy of them', async () => {
  await renderPair(PAIRED, true);

  await fireEvent.press(screen.getByText('Cancel'));

  expect(router.back).toHaveBeenCalled();
  expect(router.replace).not.toHaveBeenCalled();
});

it('goes back to the tabs after re-pairing from the Pair button', async () => {
  await renderPair(PAIRED, true);

  await act(() => camera.scan!(CODE));

  expect(router.back).toHaveBeenCalled();
  expect(router.replace).not.toHaveBeenCalled();
});

it('opens the tabs after the first pairing, when there is nothing to go back to', async () => {
  await renderPair(null, false);

  await act(() => camera.scan!(CODE));

  expect(router.replace).toHaveBeenCalledWith('/');
});
