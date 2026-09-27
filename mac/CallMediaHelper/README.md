# TwinDesk Calls for Mac

TwinDesk Calls makes a webcam and microphone connected to the Windows PC
available to Mac calling apps. It runs behind the existing TwinDesk menu and
does not inject keyboard or mouse input or capture screen images.

## Requirements

- Apple Silicon Mac, macOS 14.2 or later, and a paired Windows TwinDesk with
  call-media support.
- [OBS Studio](https://obsproject.com/) 30 or newer. Enable OBS Virtual Camera
  in System Settings → General → Login Items & Extensions → Camera Extensions.
  OBS itself can then remain closed; TwinDesk feeds its camera extension.
- TwinDesk Microphone, built from `mac/AudioDriver` and installed once with its
  installer.

## Build and install

From the repository root, run:

```sh
bash "mac/Build TwinDesk.command"
bash "mac/AudioDriver/Install.command"
zsh "mac/Install-Login-Startup.command"
```

The audio-driver installer asks for a Mac administrator password because Core
Audio drivers live under `/Library/Audio/Plug-Ins/HAL`. A locally installed
Apple Development signing identity is required to build that driver and is
automatically used for both apps when available. App builds fall back to ad-hoc
signing when no identity exists, which may make macOS ask for permissions again
after an update.

Open Windows TwinDesk's **Camera & mic** controls, select the webcam and its
microphone, and enable sharing. Copy its media setup code into **Camera &
microphone settings…** in the Mac TwinDesk menu. The code is a media-only
credential and cannot control the PC. It is stored under
`~/Library/Application Support/TwinDesk Calls/` with private file permissions;
never publish or log it.

Approve System Audio Recording for TwinDesk Calls when macOS asks. This lets it
forward Mac speaker audio to Windows with an audio-only Core Audio process tap.
It does not grant screen-image capture, webcam access, or access to the Mac's
physical microphone. TwinDesk Microphone is a local input-only device and does
not require a separate privacy permission.

## Use

In a Mac calling app select:

- **Camera:** OBS Virtual Camera
- **Microphone:** TwinDesk Microphone
- **Speakers:** the normal Mac output

Use **Start camera feed** and **Stop camera feed** in the main TwinDesk menu.
The camera starts off after launch and sleep. The PC microphone activates when
an app opens TwinDesk Microphone; starting the camera also keeps microphone
transport ready for apps that open their microphone only after a call begins.
Speaker forwarding runs automatically while the helper is connected.

The helper deliberately excludes its own microphone playback from the speaker
stream, preventing the caller's audio from being sent back to the PC as
feedback. The Mac output device's volume and mute controls are applied before
the speaker stream is sent; Windows playback volume still applies separately.

Camera frames and microphone/speaker PCM stay in memory and are not saved.
Separate authenticated TLS connections carry each stream. The microphone uses
an in-memory shared ring between TwinDesk Calls and Core Audio; it contains only
transient PCM and no pairing credentials. Queues are bounded, and stopping the
camera drops queued frames and closes its media demand.

The helper accepts 1080p or 720p JPEG at up to 30 fps. OBS exposes a fixed 1080p
virtual camera, so 720p input is scaled and does not gain detail. The tested
webcam delivered about 15 fps even when 720p/30 was requested. A calling service
may further reduce outgoing resolution.

## Verified setup

The installed single-menu controls received live 720p video from Windows in OBS
Virtual Camera. TwinDesk Microphone delivered the PC webcam's microphone to a
live FaceTime call at 48 kHz stereo. Keyboard/mouse control and automatic
speaker forwarding remained connected. Other calling apps should be checked
once before relying on them.
