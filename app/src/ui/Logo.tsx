import Svg, { Defs, LinearGradient, Path, Rect, Stop } from 'react-native-svg';

/** The Reach mark: a cursor reaching out with two signal arcs, on the accent gradient. Same drawing as assets/icon.png. */
export function Logo({ size = 72 }: { size?: number }) {
  return (
    <Svg width={size} height={size} viewBox="0 0 100 100" accessibilityLabel="Reach logo">
      <Defs>
        <LinearGradient id="reach" x1="0" y1="0" x2="1" y2="1">
          <Stop offset="0" stopColor="#6D93FF" />
          <Stop offset="1" stopColor="#3A5BF0" />
        </LinearGradient>
      </Defs>
      <Rect width="100" height="100" rx="22" fill="url(#reach)" />
      <Path d="M30 30 L30 76 L41 65 L48.5 81 L56 77.5 L48.5 62 L63 62 Z" fill="#fff" stroke="#fff" strokeWidth={3} strokeLinejoin="round" />
      <Path d="M50 26 A 22 22 0 0 1 66 42 M54 14 A 34 34 0 0 1 78 38" fill="none" stroke="#fff" strokeWidth={6} strokeLinecap="round" />
    </Svg>
  );
}
