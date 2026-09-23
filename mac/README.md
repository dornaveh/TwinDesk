# TwinDesk for Mac

The Mac companion receives keyboard and mouse input from Windows and forwards Mac system audio to the PC. Video travels through the monitor cables, not the network. Start with the [project setup guide](../README.md).

## Requirements and build

- Apple Silicon Mac, macOS 14.2 or later.
- Apple Command Line Tools, including Swift and a macOS SDK with Core Audio process tap APIs.
- The Windows companion, reachable over IPv4. A wired network is recommended.

From the repository root:

```sh
bash "mac/Build TwinDesk.command"
```

The script builds `mac/dist/TwinDesk.app` and the bundled m1ddc display helper. It opens Finder at the result; set `TWINDESK_NO_REVEAL=1` to skip that. If Command Line Tools are missing, it requests Apple's installer and exits; finish installation before rebuilding.

The default signature is ad hoc. Set `TWINDESK_SIGNING_IDENTITY` to your own installed code-signing identity for consistent signing across builds. This does not automatically notarize the app or guarantee that permissions survive every update. Never distribute signing keys or pairing codes.

## Install, permissions, and pairing

1. Copy the built app to `/Applications/TwinDesk.app` before granting permissions. Quit the previous app before replacing it.
2. Open the installed app and allow Accessibility access when requested, so it can inject keyboard and mouse events. Newer macOS versions may group this under Device Control and Data Access.
3. Allow local-network access if macOS asks. Paste the pairing code from your Windows companion and connect. Pairing pins the PC certificate and saves the pairing secret in the Mac Keychain.
4. Enable **Play Mac system audio through the PC** if desired, and approve system-audio recording access. Depending on macOS, this permission appears under Screen & System Audio Recording. The current implementation uses a Core Audio process tap and does not capture screen images.

Ad hoc rebuilds can require granting permissions again. If input is unavailable after an update, check the permission for the installed copy, then quit and reopen the app. Do not run several copies from different folders.

## Operation

Use the Windows shortcuts documented in the root README. The Mac menu-bar dropdown also offers **Switch back to Windows** when connected to a compatible Windows companion. Monitor switching is requested through Windows during normal operation; Mac display-helper support remains experimental and connection-dependent.

The Windows key becomes Command; Alt becomes Option. Both Shift keys should work. Input switching and audio forwarding are independent.

The file-access buttons open the paired PC's configured SMB shares in Finder. Finder uses Windows credentials separately from TwinDesk pairing. Shares must first be configured on Windows; disconnecting TwinDesk does not unmount them.

## Audio and protected video

Audio capture currently requires a 48 kHz stereo Float32 output format, then sends stereo 16-bit PCM to Windows in chunks of up to 10 ms. Unsupported output formats produce an error. Output-device changes may require stopping and restarting forwarding. Network, Windows output buffering, and Bluetooth all contribute additional latency.

The tap suppresses local Mac playback while capture is running. Stopping the tap removes that suppression; it does not change an independent macOS mute setting. If you want the built-in speaker always silent, configure that separately in macOS Sound settings.

Protected playback such as Apple TV can behave differently with capture or display connections. Compatibility is not guaranteed. If video is black, stop audio forwarding and disconnect TwinDesk before testing playback again; if video remains black, investigate the display/HDCP path separately. Audio-only capture avoids screen capture but is not a way to bypass content protection.

## Optional login startup

After building and quitting any running installed TwinDesk app:

```sh
zsh "mac/Install-Login-Startup.command"
```

This copies the build into `/Applications`, backs up an existing installation under `mac/previous-install`, and registers `~/Library/LaunchAgents/local.twindesk.login.plist` for the current user. It opens TwinDesk at graphical sign-in, not before login. The app can reconnect using saved pairing. Installing a changed ad hoc build may require granting permissions again.

To remove automatic startup, unload the agent and remove its plist:

```sh
launchctl bootout "gui/$(id -u)/local.twindesk.login"
rm "$HOME/Library/LaunchAgents/local.twindesk.login.plist"
```

The vendored display helper's license, source checksums, and revision are in `third_party/m1ddc/`. Do not infer monitor-control reliability from successful compilation or clear video; DDC support varies with monitor, adapter, and active input.
