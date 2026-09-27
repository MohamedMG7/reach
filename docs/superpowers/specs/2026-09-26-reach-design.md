# Reach — Design Spec

**Date:** 2026-09-26
**Status:** Draft, awaiting review

## 1. Purpose

Reach lets me control my Windows PC from my iPhone while I'm away from the desk (e.g. in bed),
using the phone as a trackpad, a keyboard, and a PowerShell terminal, plus one-tap saved
commands (sleep, lock, shutdown timer…). Access must be secure: only my paired phone, only
after Face ID, only on my home network.

### Success criteria

- From bed, I can move the cursor, click, scroll, drag, and type (including shortcuts like
  Ctrl+C, Alt+Tab, Win) with no noticeable lag on home Wi-Fi.
- I can open a real interactive PowerShell session on the phone, and it survives the phone
  sleeping or briefly disconnecting.
- I can sleep / lock / schedule shutdown of the PC with one tap.
- A device that hasn't been paired cannot connect, read traffic, or inject anything.
  Someone holding my unlocked phone can't use Reach without passing Face ID.

### Decisions made during brainstorming

| Topic | Decision |
|---|---|
| Network scope | Home Wi-Fi (LAN) only. Nothing exposed to the internet. |
| Screen viewing | Not in v1 — I can see my monitor. Screen streaming is a future version. |
| Shell | Full interactive terminal **plus** saved one-tap commands. |
| Access control | QR pairing + encrypted, mutually authenticated channel + Face ID on the app. |
| Stack | C# .NET 10 (LTS) tray agent on PC; Expo (React Native, TypeScript) app on iPhone. |

### Assumptions (correct me if wrong)

- One PC. Multiple phones may be paired, but only one session is active at a time.
- The agent runs only while I'm logged into Windows (not as a service).
- The iPhone app is personal use — run via Expo Go / a dev build, not the App Store.
- Development happens on Windows (no Mac), which rules out a native Swift app.

### Out of scope for v1

Screen viewing, access from outside the LAN, automatic PC discovery (mDNS), clipboard sync,
file transfer, multiple PCs, editing saved commands on the phone, App Store distribution.
Controlling the UAC prompt or the Windows lock screen is **impossible** by design of Windows
(injected input is blocked on the secure desktop) — the "Lock" command therefore cannot be
undone from the phone.

## 2. Architecture

```
iPhone (Expo app)                          Windows PC (Reach.Agent, .NET 10 tray app)
┌──────────────────────┐                   ┌────────────────────────────────────┐
│ Face ID gate         │                   │ WebSocket listener :47800          │
│ Trackpad / Keyboard  │  WebSocket over   │  └─ Session (Noise channel)        │
│ Terminal (xterm.js)  │◄─ home Wi-Fi ────►│      ├─ InputService   → SendInput │
│ Saved commands       │  (every frame     │      ├─ TerminalService → ConPTY   │
│ Crypto + Keychain    │   encrypted)      │      └─ CommandService → config    │
└──────────────────────┘                   │ Tray icon: show QR, paired devices │
                                           └────────────────────────────────────┘
```

### Repository layout

```
Reach/
  agent/
    Reach.Agent/          WinForms tray app, Kestrel WebSocket host, services
    Reach.Protocol/       Noise implementation, message types, framing (no Windows deps)
    Reach.Protocol.Tests/ xUnit
    Reach.Agent.Tests/    xUnit
  app/                    Expo app (TypeScript); depends on protocol/ts
    src/gestures/         Pure gesture recognizer
    src/screens/          Pair, Lock, Trackpad, Terminal, Commands
    assets/terminal.html  xterm.js page loaded in the WebView
  protocol/
    ts/                   @reach/protocol: Noise IK + message types (mirrors Reach.Protocol)
    test-vectors/         JSON vectors shared by both test suites
  docs/superpowers/specs/
```

## 3. Security

### 3.1 Cryptography

Both sides implement the **Noise Protocol Framework** handshake
`Noise_IK_25519_ChaChaPoly_SHA256` (the pattern WireGuard uses), for both pairing and normal
sessions:

- The phone (initiator) always knows the PC's static key — from the QR code.
- The phone's static key travels **encrypted** inside handshake message 1, so the PC learns
  which phone is connecting and checks it against `devices.json`.
- Both sides are authenticated; each session has forward secrecy via ephemeral keys.
- **Prologue:** the ASCII string `reach/1` (binds the protocol version into the handshake).

> Change from the first draft (which used NK for pairing and KK for sessions): in KK the PC
> must know the phone's key *before* reading message 1, which it can't when several phones are
> paired. IK solves that and covers pairing with the same handshake.

Primitives:

| | PC (C#) | Phone (TypeScript) |
|---|---|---|
| X25519 | `Sodium.Core` (libsodium) `ScalarMult` | `@noble/curves` |
| ChaCha20-Poly1305 | `System.Security.Cryptography.ChaCha20Poly1305` | `@noble/ciphers` |
| SHA-256 / HMAC | `System.Security.Cryptography` | `@noble/hashes` |
| Randomness | `RandomNumberGenerator` | `expo-crypto` `getRandomValues` |

> The phone uses the audited `@noble/*` libraries instead of tweetnacl, because tweetnacl
> lacks ChaCha20-Poly1305 and SHA-256 and so can't implement a standard Noise suite.
> `@noble/*` is pure JS and works in Expo Go.

Each side's Noise implementation covers only the IK pattern plus transport encryption and
**must pass the official Noise IK test vector** (cacophony), plus a Reach-specific
cross-language vector in `protocol/test-vectors/`.

### 3.2 Keys and storage

- **PC static key pair:** generated on first run. Private key stored in
  `%APPDATA%\Reach\identity.key`, encrypted with DPAPI (`CurrentUser` scope).
- **Phone static key pair:** generated during pairing, private key stored in the iOS Keychain
  via `expo-secure-store` (`WHEN_UNLOCKED_THIS_DEVICE_ONLY`).
- **Paired devices:** `%APPDATA%\Reach\devices.json` — list of
  `{ id, name, publicKey, pairedAt, lastSeenAt }`.

### 3.3 Pairing flow

1. User picks **Pair new phone** from the tray menu. The agent generates a random 16-byte
   pairing code, valid for **2 minutes, single use**, and shows a QR code window containing:
   `reach://pair?v=1&h=<ip>[,<ip>…]&p=47800&k=<pcStaticPub b64url>&c=<code b64url>&n=<PC name>`
   (`h` lists every private IPv4 address of the PC's active interfaces.)
2. The phone scans it (`expo-camera`), generates its static key pair, and tries each host in
   turn.
3. It runs the IK handshake. The payload of handshake message 1 is the JSON
   `{ "pairCode": "<b64url>" }`.
4. The PC checks the code (constant-time compare, not expired, not used), marks it used, and
   saves the phone's static key (learned from message 1) as a new device. It then completes
   the handshake with an empty `{}` payload, and the connection **continues as a normal
   session** — no reconnect needed. The device name is filled in from the `hello` message.
5. Any failure → the PC closes the connection without replying. The QR window closes on
   success or expiry.

Rescanning a QR code from a PC the phone is already paired with (same `k`) only updates the
stored host addresses — it does not create a new identity.

### 3.4 Session flow

1. Phone opens `ws://<host>:47800/` and runs the IK handshake with payload `{}` (handshake
   messages are WebSocket binary messages too).
2. After reading message 1, the PC accepts only if the phone's static key is in
   `devices.json` (or the payload carries a valid pairing code, §3.3); otherwise it closes the
   connection without replying.
3. After the handshake, every WebSocket binary message is exactly one Noise transport message
   (max 65,535 bytes) whose plaintext is a UTF-8 JSON application message (§4).
4. Noise's per-direction counter nonces reject replayed, reordered, or tampered messages; any
   decryption failure closes the session.
5. **One active session:** a new authenticated session replaces the existing one (the old one
   is closed). This lets a phone that changed networks reconnect immediately.
6. Revoking a device in the tray menu removes it from `devices.json` and closes its session.

### 3.5 Network hardening

- On first run, the agent asks for elevation once to add a Windows Firewall inbound rule for
  TCP 47800 on the **Private** profile only.
- **Rate limit:** 5 failed handshakes (or pairings) per source IP per minute → that IP is
  ignored for 1 minute.
- **Handshake timeout:** 10 s from TCP connect to completed handshake.
- **Heartbeat:** `ping`/`pong` every 5 s; no traffic for 15 s → session closed.

### 3.6 Face ID

- `expo-local-authentication`, with the device passcode as fallback.
- Required on cold start and when returning from background after **more than 60 s**.
- Keys are only read from the Keychain after authentication succeeds; no connection is made
  while locked.

### 3.7 Logging

`%APPDATA%\Reach\logs\` (rolling daily, 7 days). Logs contain connections, pairing events,
errors, and **which** saved command ran. They **never** contain keystrokes, text, terminal
input or output, or key material.

## 4. Application messages

JSON objects with a `type` field. Phone → PC unless noted.

| Type | Fields | Notes |
|---|---|---|
| `hello` | `deviceName`, `appVersion` | First message after handshake. |
| `welcome` (PC→) | `pcName`, `agentVersion` | Reply to `hello`. |
| `busy` (PC→) | `deviceName` | Reply to `hello` instead of `welcome` while another phone is connected (one phone at a time; the same phone reconnecting replaces its old session). The PC then closes; the phone shows who is connected and checks back every 5 s. |
| `bye` (PC→) | — | The PC disconnected this phone on purpose (tray → Disconnect). The phone stops reconnecting and offers Reconnect. |
| `ping` / `pong` | — | Heartbeat, either direction. |
| `mouse.move` | `dx`, `dy` (ints) | Phone batches to ≤ 60/s, already accelerated. |
| `mouse.button` | `button` (`left`/`right`/`middle`), `down` (bool) | |
| `mouse.scroll` | `dx`, `dy` (ints, wheel units) | Vertical and horizontal. |
| `key.text` | `text` | Any Unicode (Arabic, emoji). |
| `key.combo` | `keys` (e.g. `["ctrl","shift","esc"]`) | Special keys and shortcuts; single keys too (`["enter"]`). |
| `term.open` | `cols`, `rows` | Attaches to the existing session or starts a new one. |
| `term.opened` (PC→) | `resumed` (bool) | Followed by the replay buffer as `term.output`. |
| `term.input` | `data` | Raw keystrokes (xterm.js `onData`). |
| `term.output` (PC→) | `data` | UTF-8 text including VT sequences. |
| `term.resize` | `cols`, `rows` | |
| `term.exited` (PC→) | `code` | The shell exited; the next `term.open` starts a fresh one. |
| `cmd.list` | — | |
| `cmd.listed` (PC→) | `commands: [{ id, label, icon, confirm }]` | |
| `cmd.run` | `id` | |
| `cmd.result` (PC→) | `id`, `ok`, `exitCode`, `error?` | |
| `error` (PC→) | `code`, `message` | e.g. `unknown_message`, `bad_args`. |

Unknown or malformed messages get an `error` reply and are otherwise ignored (the session
stays open). Key names for `key.combo`: `ctrl alt shift win esc tab enter backspace delete
up down left right home end pageup pagedown f1…f12 a…z 0…9 space`.

## 5. PC agent (`Reach.Agent`)

- **Host:** .NET 10 (LTS; .NET 8 support ends Nov 2026), WinForms `NotifyIcon` (no main window), single instance (named mutex).
  Kestrel serves WebSockets on `0.0.0.0:47800` (no URL ACL / admin required).
- **Tray menu:** Pair new phone · Paired devices (list + revoke) · Edit commands (opens
  `commands.json` in the default editor) · Start with Windows (HKCU `Run` key toggle) · Quit.
- **Presence:** tray icon changes color while a session is active; a Windows toast shows
  "<device> connected" on every new session.

### 5.1 InputService

- Behind an `IInputSink` interface; the real implementation calls Win32 `SendInput`.
- **Mouse move:** applied as an absolute move (current position + delta, via
  `MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK`), so Windows' "Enhance pointer
  precision" doesn't add a second acceleration on top of the phone's curve. Clamped to the
  virtual desktop.
- **Text:** `KEYEVENTF_UNICODE` per UTF-16 code unit; `\n` and `\b` map to the Enter and
  Backspace virtual keys.
- **Combos:** press modifiers in order, press the key, release in reverse order.
- **Safety:** tracks held keys/buttons; releases all of them when the session ends for any
  reason.

### 5.2 TerminalService

- ConPTY (`CreatePseudoConsole`) running `pwsh.exe` if found on PATH, else
  `powershell.exe`, started in the user's home directory.
- Output is decoded with a streaming UTF-8 decoder (so multi-byte characters split across
  reads are handled) and forwarded as `term.output`.
- **Ring buffer:** the last 64 KB of output is kept. On re-attach (`resumed: true`) it's sent
  starting at the first newline in the buffer.
- **Lifetime:** survives phone disconnects; killed after **30 minutes** with no session
  attached. `exit` in the shell → `term.exited`.

### 5.3 CommandService

- Reads `%APPDATA%\Reach\commands.json` (reloaded when the file changes):
  `[{ "id", "label", "icon", "confirm", "run" }]`, where `run` is a PowerShell script.
- Each run: a new hidden `powershell.exe -NoProfile -NonInteractive -Command <run>`, a 30 s
  timeout, and the exit code is returned in `cmd.result`.
- Seeded defaults on first run:

| id | label | confirm | run |
|---|---|---|---|
| `sleep` | Sleep | yes | `Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::SetSuspendState('Suspend', $false, $false)` |
| `lock` | Lock | yes | `rundll32.exe user32.dll,LockWorkStation` |
| `mute` | Mute / Unmute | no | `(New-Object -ComObject WScript.Shell).SendKeys([char]173)` |
| `vol-up` | Volume + | no | `(New-Object -ComObject WScript.Shell).SendKeys([char]175)` |
| `vol-down` | Volume − | no | `(New-Object -ComObject WScript.Shell).SendKeys([char]174)` |
| `shutdown-30` | Shut down in 30 min | yes | `shutdown /s /t 1800` |
| `shutdown-cancel` | Cancel shutdown | no | `shutdown /a` |

## 6. Phone app (`app/`)

Expo SDK (latest at project start), TypeScript, `expo-router` with a bottom-tabs layout.
Libraries: `expo-camera`, `expo-secure-store`, `expo-local-authentication`, `expo-haptics`,
`expo-keep-awake`, `expo-crypto`, `react-native-webview`, `react-native-gesture-handler`,
`@noble/curves`, `@noble/ciphers`, `@noble/hashes`.

### 6.1 Screens

1. **Pair** — first run, or from settings. Camera QR scanner → pairing flow (§3.3).
2. **Lock** — Face ID gate (§3.6).
3. **Main** — bottom tabs **Trackpad | Terminal | Commands**, with a connection status dot
   (green connected / yellow reconnecting / red offline) and the PC name.

### 6.2 Trackpad

| Gesture | Action |
|---|---|
| 1-finger move | Move cursor, with an acceleration curve |
| 1-finger tap | Left click |
| 2-finger tap | Right click |
| Tap, then touch again within 250 ms and move | Drag (left button held until lift) |
| 2-finger drag | Scroll (vertical + horizontal) |

- Optional Left / Right click buttons under the surface.
- Haptic tick on every click. Screen kept awake while this tab is visible.
- The gesture recognizer is a pure function
  `(state, touchEvent, now) → { state, outputs: Message[] }` so it's unit-testable.
- Moves are accumulated and flushed once per animation frame as a single `mouse.move`.

### 6.3 Keyboard

- ⌨ button on the Trackpad tab shows the iOS keyboard via a hidden `TextInput`. Typed text →
  `key.text`; Backspace / Enter → `key.combo`.
- Accessory row above the keyboard: **Ctrl Alt Shift Win Esc Tab ← ↑ ↓ →** and an **Fn**
  toggle that swaps in F1–F12.
- **Sticky modifiers:** tap a modifier once to arm it for the next key; tap twice to lock it
  (highlighted); tap again to release. An armed modifier + a typed letter → `key.combo`.
- The trackpad stays usable while the keyboard is up.

### 6.4 Terminal

- `react-native-webview` loading `assets/terminal.html` with xterm.js and its fit addon;
  bridged with `postMessage`.
- The same accessory row (Esc, Tab, Ctrl, arrows) is shown; Ctrl is sticky.
- On tab open → `term.open` with the fitted size; on layout change → `term.resize`.

### 6.5 Commands

- Grid of buttons from `cmd.listed`. `confirm: true` → native "Are you sure?" alert first.
- A toast shows the `cmd.result` outcome.

### 6.6 Connection behavior

- Auto-reconnect with backoff 1 s → 2 s → 5 s (then every 5 s) while the app is in the
  foreground; tries each stored host.
- Input produced while not connected is **dropped**, never queued.
- On background: the socket is closed. On foreground: Face ID check if > 60 s, then reconnect;
  the Terminal tab re-attaches to the existing shell.

## 7. Error handling summary

| Situation | Behavior |
|---|---|
| Wi-Fi drop / PC asleep | Status dot turns yellow/red; auto-reconnect; input dropped. |
| PC IP changed | Reconnect fails → app shows "Can't reach PC — rescan QR" (rescan updates hosts only). |
| Unpaired / revoked phone | PC closes during handshake; app shows "This phone isn't paired" and offers Pair. |
| Tampered / replayed frame | Decryption fails → session closed → normal reconnect. |
| Too many failed handshakes | IP ignored for 1 minute. |
| Session ends mid-drag or with keys held | Agent releases all held keys and buttons. |
| Shell exits | `term.exited`; terminal shows "[session ended — tap to restart]". |
| Saved command fails / times out | `cmd.result ok=false` → error toast. |
| Bad `commands.json` | Agent keeps the last valid list and shows a tray notification. |

## 8. Testing

- **Protocol (both sides):**
  - Official Noise test vector for `Noise_IK_25519_ChaChaPoly_SHA256`.
  - Shared `protocol/test-vectors/*.json`: fixed keys/ephemerals → expected handshake bytes
    and transport ciphertexts, run by both xUnit and Jest, proving C# ↔ TS interop.
  - Negative tests: tampered ciphertext, replayed message, unknown static key, expired /
    reused pairing code.
- **Agent:**
  - Message → `IInputSink` call translation (fake sink; no real input).
  - Held-key release on session end.
  - `TerminalService` integration test: real ConPTY, `echo hi`, output contains `hi`;
    ring-buffer replay.
  - `CommandService`: config parsing, timeout, exit codes.
  - Rate limiter and pairing-code expiry with a fake clock.
- **App:**
  - Gesture recognizer (Jest): tap → left click, 2-finger tap → right click, tap-tap-drag →
    button down/move/up, 2-finger drag → scroll.
  - Sticky-modifier state machine.
- **Manual end-to-end checklist** on the real iPhone + PC: pair, Face ID, every gesture,
  typing Arabic/emoji, Alt+Tab, terminal with `vim`/`git`, reconnect after phone sleep,
  revoke device, each default command.
