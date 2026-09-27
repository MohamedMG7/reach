import { type RefObject, useEffect, useState } from 'react';
import { Keyboard, Platform, type View } from 'react-native';

/** How much of `view` the on-screen keyboard covers, so content can pad itself above it. */
export function useKeyboardOverlap(view: RefObject<View | null>): number {
  const [overlap, setOverlap] = useState(0);
  useEffect(() => {
    // iOS announces the keyboard before it animates in; Android only after it is shown.
    const [showEvent, hideEvent] = Platform.OS === 'ios' ? (['keyboardWillShow', 'keyboardWillHide'] as const) : (['keyboardDidShow', 'keyboardDidHide'] as const);
    const show = Keyboard.addListener(showEvent, (event) => {
      view.current?.measureInWindow((_x, y, _width, height) => setOverlap(Math.max(0, y + height - event.endCoordinates.screenY)));
    });
    const hide = Keyboard.addListener(hideEvent, () => setOverlap(0));
    return () => {
      show.remove();
      hide.remove();
    };
  }, [view]);
  return overlap;
}
