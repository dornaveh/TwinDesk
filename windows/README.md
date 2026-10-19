# Windows host

Build with the .NET 10 SDK from the repository root:

```powershell
powershell -NoProfile -File windows/tools/Build-Windows.ps1
```

Run `windows/dist/TwinDesk-Windows/TwinDesk.exe`. The .NET 10 Desktop Runtime
is required. Choose the PC's reachable IPv4 address, start the connection and
copy the pairing code to the Mac app. Permit TCP 48150 inbound only on the
trusted interface/from the intended Mac. The app binds a single address;
it does not automatically open the firewall.

## Daily use and startup

The tray menu provides switching and opening the settings window. Ctrl+Alt+F12
toggles, Ctrl+Alt+F10 selects Mac, and Ctrl+Alt+F11 returns local input to PC.
Monitor routing must match actual cables. Refresh reads the monitor list while
connected without changing input or ending the connection.

Double-clicking the physical middle mouse button also toggles computers. A
single middle click waits for the Windows double-click recognition window;
middle dragging starts when movement exceeds the click tolerance. Mouse buttons
and pending clicks are released or discarded when changing targets. Injected
mouse events do not trigger the shortcut. Mac Back/Forward buttons map to
Command-[ and Command-] in apps supporting those navigation shortcuts.

After building and testing, optionally run:

```powershell
powershell -NoProfile -File windows/tools/Install-Windows-Startup.ps1
```

This registers a per-user scheduled task `TwinDesk-<username>` at sign-in,
with a 15-second delay. It launches the built app in the tray and selects
`windows/local-data/installed-<username>` for its settings and protected identity.
Keep the build directory in place or reinstall the startup registration after
moving it. The script preserves existing destination data and only migrates
AppData settings when the destination file is absent. It removes the old
TwinDesk Run entry if present. Do not run it from a throwaway checkout if you
already have a working installation elsewhere.

Without that registration, settings normally live in `%LOCALAPPDATA%\TwinDesk`.
The `--data <directory>` option or a local `data-directory.txt` beside the app
can select a different runtime directory. Do not commit or share these files.

## Camera and microphone for Mac calls

Open **Camera & mic** in the window or tray menu. Select the webcam and microphone
and allow each device independently. These permissions and device selections
are remembered, but capture stays idle until the authenticated Mac requests it.
The Mac's automatic camera mode follows actual use of TwinDesk Microphone, so
opening that input starts the camera and microphone and releasing it stops both.
Manual camera Start/Stop remains available in the Mac menu for camera-only use.
Disconnecting the receiver also stops capture.

The default camera profile requests native 1280×720 at 30 fps; 1920×1080 at
15 fps is also available. Unsupported resolutions fail visibly rather than
being upscaled on Windows. The tested Logitech USB camera delivered about
15 fps with either profile, including in an independent capture check.
The Mac helper scales 720p frames to the OBS virtual camera's 1080p output;
this does not add detail.

Use **Copy Mac media setup code** with the separate
[Mac Calls helper](../mac/CallMediaHelper/README.md). Its media credential cannot
control the PC; it survives reconnects and is protected with Windows DPAPI.
Keep the code private. Video, microphone, and speaker audio use separate bounded
connections so video traffic does not queue behind keyboard events. Nothing is
saved as a recording.

The Calls helper takes over Mac speaker capture to exclude the forwarded
microphone from the return audio. Follow its setup instructions before enabling
the microphone; do not run the old Mac audio tap and the helper microphone together.

## Optional direct Ethernet and C:/E: file sharing

This is optional and independent of KVM. The supplied helper is deliberately
limited to a dedicated `192.168.77.0/30` link and Windows C: and E: drives. For
other addresses or shares, configure Windows networking and SMB manually.
Never run the helper against your normal internet/LAN adapter.

Preview the helper's intended changes first:

```powershell
powershell -NoProfile -File windows/tools/Configure-DirectLink.ps1 -InterfaceAlias Ethernet -WhatIf
```

For actual changes, run the same command without `-WhatIf` in an Administrator
PowerShell. `-Account` defaults to the current Windows account; supply the
intended individual account explicitly if elevating as a different user.

The script assigns the named wired adapter `192.168.77.1/30`, with no gateway
or DNS, and creates authenticated encrypted `TwinDesk-C` and `TwinDesk-E`
shares with full share access for that account. It adds firewall allowances
for TCP 445/48150 from `192.168.77.2` on that adapter only. It checks for some
conflicting existing settings; review your existing routes/firewall policy too.
It does not alter NTFS permissions or disable existing broader firewall rules.

Set Mac Ethernet manually to `192.168.77.2`, mask `255.255.255.252`, no router
or DNS. Keep Wi-Fi for internet access. Finder can connect to
`smb://192.168.77.1/TwinDesk-C` and `smb://192.168.77.1/TwinDesk-E` using your
Windows account password, not the Windows Hello PIN. Those shares allow file
creation/modification/deletion wherever that account's file permissions allow;
protected, locked, encrypted and cloud-only files retain their restrictions.

`Remove-FileSharing.ps1 -WhatIf` previews removing the two shares and two named
firewall rules. Applying it keeps the files and Ethernet configuration.

## Remembered microphone selection

Camera and microphone choices are restored independently. TwinDesk remembers the
microphone's endpoint, name, and physical device container when Windows exposes
it. It first matches the exact endpoint, then the same hardware; drivers without
hardware identity can use a unique saved name. Ambiguous devices require a user
selection, and TwinDesk never substitutes the first or default microphone.

If the saved device is absent or capture fails, the choice and sharing preference
remain saved. Device enumeration retries every three seconds, including while
the camera is active. Forwarding resumes only when the selected microphone is
available and an authenticated Mac calling app requests it. Turning sharing off
explicitly disables restoration.

## Audio

Select **Windows default output** to have Mac audio follow the current Windows
playback device, including changes between speakers and headphones. The output
selector also works while connected. Explicit device selections are remembered
by endpoint identity, so similarly named devices are not interchangeable.

If an output disconnects or its format changes, TwinDesk reopens playback with
bounded retry delays. An explicitly selected device must return before playback
resumes; TwinDesk does not silently switch to another device. If no output is
available, incoming audio is discarded until one becomes available. The status
shows recovery without repeating an error for every audio packet. Keyboard,
mouse, monitor control, and the encrypted connection stay active.

Shared WASAPI allows Windows and Mac audio together; compatible output formats
use IAudioClient3's supported minimum period. A 40 ms maximum application queue
discards old frames after a burst, without intentionally waiting to fill it.
Recovery clears queued audio instead of replaying stale sound. Brief gaps while
a device opens are expected; Bluetooth adds its own delay.
## Sign in to the Mac after a restart

One-time setup on the signed-in Mac:

1. Enable **Remote Login** for your account in System Settings → General → Sharing.
2. Enable **Screen Sharing**. Open its information button, select **Only these
   users**, and add your Mac account. Leave **VNC viewers may control screen with
   password** off; TwinDesk authenticates with your Mac account.
3. Verify the Mac's SSH host key once using Windows OpenSSH. TwinDesk uses the
   current Windows user's `known_hosts` file; unknown, changed, or revoked keys
   are rejected before sending a password.

Choose **Sign in to Mac** in the Windows window, or **Sign in to Mac after
restart** in the tray menu. Enter the Mac address, local account name, and Mac
password. Only the address and account name are saved. The password stays in
memory for this attempt and is not written to settings, logs, process arguments,
or environment variables.

On Apple silicon with macOS 26 or later, TwinDesk first unlocks FileVault over
SSH and waits for macOS to start. If the account is already signed in, it starts
the Mac companion. Otherwise a temporary Mac sign-in view opens inside Windows.
Click your account and sign in using the PC's keyboard and mouse. This view uses
Apple account authentication inside a host-verified SSH tunnel; screen traffic
and input stay inside that tunnel. It has no clipboard integration. Account names
and passwords must each fit within 63 UTF-8 bytes for Apple's legacy account
authentication protocol; longer values are rejected without truncation.

The view closes once the normal TwinDesk connection returns. You can then switch
computers normally. Closing the view early keeps control on Windows. The flow
does not disable FileVault, enable automatic login, switch monitor inputs, or
sign out another account. Mac login startup must be configured for subsequent
automatic reconnection.

Qualification: 61 protocol checks and 31 unlock/host-identity/UI checks passed,
including cancellation, fragmented screen updates, authentication refusal, and
bounded image/message sizes. Windows x64 publish passed. The real Mac's SSH host
identity and Screen Sharing account-authentication availability were verified
without a password. Read-only commands also succeeded against the real Mac's
login window using the production channel-open setting and the existing agent
SSH key. Real-password Screen Sharing sign-in through Windows was then completed
on the tested Mac, followed by TwinDesk reconnection, keyboard/mouse switching
to Mac and back, and audio reaching Windows. That restart required a channel-open
fix before sign-in succeeded; FileVault unlock through the final build still
needs a fresh qualification. These results do not establish monitor switching
reliability or compatibility with other macOS releases.
