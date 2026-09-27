# Reach — phone app (iPhone and Android)

The Expo (SDK 57) app that controls a Windows PC running `Reach.Agent`: trackpad, keyboard,
PowerShell terminal and one-tap saved commands, on iPhone and Android.
Design: `docs/superpowers/specs/2026-09-26-reach-design.md`.

## Run it on your phone

1. Install **Expo Go** from the App Store (iPhone) or Google Play (Android). The phone and the PC
   must be on the same Wi-Fi.
2. Install dependencies (from `app/`): `npm ci --prefix ../protocol/ts`, then `npm ci`.
3. `npm start`, then scan the QR code it prints: with the Camera app on iPhone, or from inside
   Expo Go on Android. If Windows asks whether Node.js may use the network, allow **Private networks**.
4. In Reach, unlock (Face ID, fingerprint or your screen lock), then scan the code from the PC's
   tray menu → **Pair new phone…**.

In Expo Go, scan the PC's code from inside Reach (**Pair**). In an installed build, the phone's own
Camera app can scan it too: it opens Reach, which asks before pairing.

## Develop

- `npm start` runs the app at full speed (no debug mode); use `npm run dev` for development
  (fast refresh, error overlays), which makes the trackpad noticeably laggier.
- `npm test` — Jest: pure logic, the connection client against an in-memory PC, and the screens.
- `npm run typecheck`
- `npm run bundle` / `npm run bundle:android` — builds the iOS / Android JavaScript bundle into
  `dist/` to prove it compiles.
- `src/terminal/terminalPage.generated.ts` is rebuilt from `assets/terminal.html` on every
  `npm install` / `npm ci` (xterm.js is inlined, so the terminal works offline).
