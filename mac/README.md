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

The script builds `mac/dist/TwinDesk.app`, the bundled m1ddc display helper,
`mac/CallMediaHelper/dist/TwinDesk-Calls.app`, and the TwinDesk Microphone audio
driver. Install both apps and install the driver once using
`mac/AudioDriver/Install.command`. The background Calls helper handles system
audio and optional camera/microphone forwarding. See its
[setup instructions](CallMediaHelper/README.md) for media dependencies. The
script opens Finder at the result; set `TWINDESK_NO_REVEAL=1` to skip that. If
Command Line Tools are missing, it requests Apple's installer and exits; finish
installation before rebuilding.

The build uses an installed Apple Development signing identity when one is
available; set `TWINDESK_SIGNING_IDENTITY` to choose one explicitly. The audio
driver requires such an identity. The two apps fall back to ad-hoc signing when
no identity is found, which can make macOS request permissions again after an
update. Local signing does not notarize the apps. Never distribute signing keys
or pairing codes.

## Install, permissions, and pairing

1. Install TwinDesk Microphone with `mac/AudioDriver/Install.command`, then copy
   the built apps to `/Applications/TwinDesk.app` and
   `/Applications/TwinDesk-Calls.app` before granting permissions. Quit previous
   copies before replacing them. Have local Mac input available during setup or
   permission changes.
2. Open the installed app and allow Accessibility access when requested, so it can inject keyboard and mouse events. Newer macOS versions may group this under Device Control and Data Access.
3. Allow local-network access if macOS asks. Paste the pairing code from your Windows companion and connect. Pairing pins the PC certificate and saves the pairing secret in the Mac Keychain.
4. Open **Camera & microphone settings…** from the TwinDesk menu, pair the helper using the separate media setup code from Windows **Camera & mic**, and approve its system-audio recording access. Audio forwards automatically; camera and microphone permissions are independent and optional. Depending on macOS, the audio permission appears under Screen & System Audio Recording. The helper uses a Core Audio process tap and does not capture screen images.

Ad hoc rebuilds can require granting permissions again. If input is unavailable after an update, check the permission for the installed copy, then quit and reopen the app. Do not run several copies from different folders.

## Operation

Use the Windows shortcuts documented in the root README. The Mac menu-bar dropdown also offers **Switch back to Windows** when connected to a compatible Windows companion. Monitor switching is requested through Windows during normal operation; Mac display-helper support remains experimental and connection-dependent.

**Use PC camera automatically with microphone** starts the PC camera when a
calling app opens TwinDesk Microphone and stops it when the input is released.
It is enabled by default and requires **Use PC microphone automatically**.
Calling apps select OBS Virtual Camera and TwinDesk Microphone. **Start camera
feed / Stop camera feed** remain available for camera-only previews; either
manual command turns off automatic camera mode until you enable it again.
OBS and the TwinDesk audio driver are optional for speaker-only use.

The Windows key becomes Command; Alt becomes Option. Both Shift keys should work. Input switching and audio forwarding are independent. The helper has no separate menu icon and is launched by the main app.

The file-access buttons open the paired PC's configured SMB shares in Finder. Finder uses Windows credentials separately from TwinDesk pairing. Shares must first be configured on Windows; disconnecting TwinDesk does not unmount them.

## Audio and protected video

Audio capture currently requires a 48 kHz stereo Float32 output format, then sends stereo 16-bit PCM to Windows in chunks of up to 10 ms. Unsupported output formats produce an error. Output-device changes may require stopping and restarting forwarding. Network, Windows output buffering, and Bluetooth all contribute additional latency.

The tap suppresses local Mac playback while capture is running. Stopping the tap removes that suppression; it does not change an independent macOS mute setting. If you want the built-in speaker always silent, configure that separately in macOS Sound settings.

Protected playback such as Apple TV can behave differently with capture or display connections. Compatibility is not guaranteed. If video is black, quit TwinDesk (which also stops its helper) before testing playback again; if video remains black, investigate the display/HDCP path separately. Audio-only capture avoids screen capture but is not a way to bypass content protection.

## Optional login startup

After building and quitting any running installed TwinDesk app:

```sh
zsh "mac/Install-Login-Startup.command"
```

This verifies and copies both app builds into `/Applications`, verifies the
already installed audio driver, backs up existing app installations under
`mac/previous-install`, and registers
`~/Library/LaunchAgents/local.twindesk.login.plist` for the current user. Both
apps must be closed before installation. It opens TwinDesk at graphical sign-in,
not before login; TwinDesk launches the installed helper. Both can reconnect
using their saved pairing. Installing a changed ad hoc build may require granting
permissions again.

To remove automatic startup, unload the agent and remove its plist:

```sh
launchctl bootout "gui/$(id -u)/local.twindesk.login"
rm "$HOME/Library/LaunchAgents/local.twindesk.login.plist"
```

The vendored display helper's license, source checksums, and revision are in `third_party/m1ddc/`. Do not infer monitor-control reliability from successful compilation or clear video; DDC support varies with monitor, adapter, and active input.
