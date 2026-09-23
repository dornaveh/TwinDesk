# TwinDesk

Use a Windows PC's keyboard, mouse, speakers or headphones with an Apple
Silicon Mac. TwinDesk can also switch the inputs on two monitors connected
to both computers, so the picture follows the keyboard and mouse.

**Experimental, source-only preview.** Input sharing and audio forwarding have
been used successfully on real hardware. Monitor input control remains
hardware-dependent and intermittent on the tested dual Samsung U32J59x setup.
Keep access to the monitors' physical input controls and a spare Mac keyboard
or mouse during setup. This is not a replacement for hardware input at boot,
FileVault unlock, recovery, or protected Windows desktops.

## What it does

- Shares keyboard and mouse over an authenticated, encrypted network connection.
- Sends Mac system audio continuously to a selected Windows audio output,
  independently of which computer you control.
- Requests monitor input changes through DDC/CI. The Windows host owns normal
  switching in both directions; monitor support must be tested individually.
- Reconnects after interruptions. Optional login startup runs Windows in the
  tray and provides a Mac menu-bar companion.
- Offers optional Windows C: and E: drive sharing through ordinary SMB/Finder.
  File sharing is separate from the KVM connection.

## Requirements and cables

- Windows 11 and the .NET 10 SDK to build; the .NET 10 Desktop Runtime to run.
- Apple Silicon Mac, macOS 14.2 or later for Core Audio process taps, and Apple's
  Command Line Tools. See the [Mac instructions](mac/README.md) for build details.
- A common network. A direct Ethernet connection is recommended for consistent
  audio; Wi-Fi also requires reachability and appropriate firewall settings.
- One video cable from each computer to each shared monitor. TwinDesk sends
  input-selection commands, not display video. Choose cables and monitor inputs
  that support your required resolution and refresh rate.

## Build and connect

1. On Windows, run `powershell -NoProfile -File windows/tools/Build-Windows.ps1`.
   Open `windows/dist/TwinDesk-Windows/TwinDesk.exe`.
2. Build and install the companion using [mac/README.md](mac/README.md). Grant
   Accessibility to the installed app for input control and audio-capture
   permission when macOS asks. Install before granting permissions: replacing
   an ad-hoc signed build can require granting them again.
3. In Windows TwinDesk, select the PC's network address reachable from the Mac.
   A fresh install starts on loopback until you choose the appropriate address.
   Start the connection, copy the pairing code, and paste it into the Mac app.
   Treat that code as a password; do not publish it or include it in screenshots.
4. First leave monitor switching off and test input with a monitor manually set
   to the Mac. Then refresh the monitor list and set the PC and Mac cable inputs.
   For the tested Samsung model, DisplayPort is the PC input and HDMI 2 is the
   Mac input; configure your own wiring instead of assuming those defaults.
5. Enable monitor switching while disconnected, then reconnect. Test one
   direction at a time and confirm both screens. Refresh remains available
   during a connection; refreshing does not switch inputs or disconnect the Mac.
6. Enable Mac audio forwarding and select your Windows output. The PC must stay
   powered on and awake for input and audio forwarding.

No network, shares, startup tasks, or system permissions are changed just by
cloning or building this repository.

## Daily controls

| Action | Windows shortcut |
| --- | --- |
| Switch computers | Ctrl + Alt + F12 |
| Return keyboard and mouse to Windows | Ctrl + Alt + F11 |
| Select the Mac | Ctrl + Alt + F10 |

The Mac menu also has **Switch back to Windows**. Windows key maps to Command,
Alt to Option, and Ctrl to Control. Keyboard forwarding uses physical key
positions interpreted by the Mac's selected layout. Some keyboards require
their Fn key to produce F10–F12.

## Audio and muting

The Mac uses audio-only Core Audio process taps, not ScreenCaptureKit. The
stream is stereo 48 kHz/16-bit PCM over the paired TLS connection. Windows uses
shared WASAPI with the driver's low-latency mode when supported, allowing PC
and Mac sound together. It falls back when the selected output format differs
or the device cannot use the low-latency path.

Mac local playback is suppressed while the audio tap is active. **For silence
even when forwarding stops, mute Mac mini Speakers in macOS Sound settings**;
also turn off the startup sound if desired. Those OS preferences are separate
from TwinDesk and can be changed by the user or operating system.

Protected-video playback worked in one live setup after replacing the old
screen-capture audio method, but behavior is not guaranteed across apps,
content, macOS versions or permissions. Bluetooth headphones can add substantial
delay after Windows playback; the network and software buffer sizes are not a
guarantee of end-to-end latency.

With **Windows default output** selected, TwinDesk follows changes to the Windows
playback device automatically. You can also change TwinDesk's speaker selection
while connected. If an explicitly selected device disconnects, TwinDesk waits
for that device and retries playback; it does not redirect sound to another
output. Recovery discards old audio and leaves the connection, keyboard/mouse,
and monitor inputs alone. A device change can cause a brief audio gap.

## Startup, storage and troubleshooting

- [Windows setup, optional Ethernet/SMB sharing and startup](windows/README.md)
- [Mac build, permissions, audio and startup](mac/README.md)

Monitor input reads can fail even while the display picture is fine. “Not
reported” means a control query failed; it does not prove the screen is absent.
If one monitor stays on the other computer, use its own input button. Software
DDC success is not proof that the displayed picture changed.

## Repository layout and privacy

`windows/` contains the Windows app, shared wire-format code, build/install
scripts and dependency notices. `mac/` contains the Mac app, its audio
components, build/install scripts and the bundled Apple Silicon display helper.

The PC creates a local certificate and random pairing token. The Mac pins the
certificate; pairing state is protected with Windows DPAPI and Mac Keychain.
There is no hosted relay or account service. TwinDesk does not record audio or
log typed text. Runtime identity files, pairing codes, local logs, private
deployment helpers and machine-specific commissioning records are excluded
from this repository.

Dependencies retain their [third-party notices](THIRD-PARTY-NOTICES.md).
No project-wide open-source license has been selected for TwinDesk itself.
