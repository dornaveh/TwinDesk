# Validation scope

The initial public export comes from a working Windows + Apple Silicon Mac
installation. Local source and hardware evidence is summarized here without
including machine identities, pairing data, private logs or screenshots.

## Verified before export

- Windows release builds and the real console runner passed 58 checks. Those
  checks cover protocol, authenticated loopback TLS, identity persistence,
  duplicate peers, command acknowledgements, malformed command IDs, input
  normalization, keyboard shortcuts, monitor-routing mocks, PCM queue behavior
  and three real silent playback checks.
- Realtek shared WASAPI accepted a 10 ms engine period. A measured device queue
  was 22 ms with no application backlog. A different Bluetooth default output
  used standard shared WASAPI at 20 ms because its mix format was 44.1 kHz.
  These are Windows buffer measurements, not end-to-end/acoustic latency.
- Windows scheduled startup, authenticated Mac reconnection, incoming audio,
  and both individual monitor directions were observed on the development rig.
- Users confirmed keyboard/mouse control, right Shift punctuation, and Mac
  audio through the PC with the Mac's system speaker mute on. Audio-only Core
  Audio capture was confirmed with video playback in one protected-video app.

## Remaining limitations

- Repeated two-monitor switching remains intermittent on the tested Samsung
  U32J59x setup. One screen may stay on the other input, and input readback can
  disappear. Physical input buttons remain necessary for recovery.
- A successful API return or submitted audio byte count does not prove a
  picture changed, audible sound occurred, or a protected video can play.
- Ad-hoc Mac builds can invalidate Accessibility/audio permissions when replaced.
  Stable signing is supported by the build but not supplied by this repository.
- Mac reboot/sleep cases, arbitrary hardware, protected playback compatibility,
  long-duration audio stability, and Bluetooth delay are not comprehensively
  qualified. Windows default-output changes during a stream are not automatic.
- Full-drive SMB shares preserve underlying NTFS access restrictions. The app
  cannot provide keyboard control at secure login, FileVault unlock or recovery.

The public console runner skips actual playback unless `--hardware-audio` is
specified. Hardware checks must not be reported as passed when skipped.

## Clean public-export checks

- The reorganized Windows source passed all 58 checks with `--hardware-audio`.
  Its release build and all exported PowerShell syntax checks passed.
- The reorganized Mac main app and audio test app built successfully from a
  separate temporary checkout. Signature verification and shell/plist checks
  passed; PCM conversion tests passed 4/4. The main build retains an existing
  SwiftUI `onChange` deprecation warning.
- These export builds were not installed. Installed applications and the Mac's
  permissions were preserved. Runtime source was compared with the latest
  working copies on both computers; public-only changes generalize setup
  defaults, paths and hardware-test opt-in behavior.
