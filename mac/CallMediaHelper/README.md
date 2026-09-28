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

**Use PC camera automatically with microphone** is enabled by default. When a
Mac app opens TwinDesk Microphone, TwinDesk starts the PC camera and microphone.
It stops them when that app releases the input, normally within a second.
Speaker forwarding continues independently. The saved automatic preference
survives launch and sleep; manual camera sessions end on sleep.

This mode follows microphone use, not independent camera use. A camera-only
preview or a call using another microphone still needs **Start camera feed**.
Manual **Start camera feed** and **Stop camera feed** turn off automatic camera
mode, so Stop always takes effect during a call. Enable the automatic toggle
again to return to call-triggered capture. Apps that keep their microphone open
after a call also keep the camera active until they release it; manual Stop is
always available. Muting inside a calling app may leave its input open.

The camera policy observes Core Audio process input usage and excludes TwinDesk
Calls itself. It never treats received microphone packets or its own OBS feed
as demand. OBS's public device-running flag includes TwinDesk's sink stream, so
that flag cannot independently identify camera consumers. See the
[OBS source](https://github.com/obsproject/obs-studio/blob/32.2.2/plugins/mac-virtualcam/src/camera-extension/OBSCameraDeviceSource.swift)
and [Apple's device-running property](https://developer.apple.com/documentation/coremediaio/kcmiodevicepropertydeviceisrunningsomewhere).

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

### Native WhatsApp limitations (2026-09-27)

WhatsApp for Mac 26.36.74 skips Core Audio devices whose transport is Virtual
or Aggregate. TwinDesk Microphone now reports RemoteStreaming, describing its
network source. After installing this version and reopening WhatsApp, the
"no connected microphone" error no longer prevented a call from opening.
The user subsequently confirmed successful microphone audio in WhatsApp.
Changing the transport does not add a device,
change its UID, or grant a new capture permission.

Native WhatsApp video is not working in this setup. With the PC feed already
receiving live video, WhatsApp selected a 1280×720 NV12 capture format and its
AVFoundation capture graph failed with OSStatus -12780. OBS's installed camera
extension advertises a single 1920×1080 BGRA format. A format compatibility
problem is suspected, but has not been proved by testing another extension.
Reducing the incoming Windows video to 720p does not change the format that
OBS advertises to Mac apps. Do not claim native WhatsApp video support based
on the working FaceTime or browser tests. Camera and microphone permissions
were already allowed; repeated permission resets are not the remedy.

See the [OBS camera-device implementation](https://github.com/obsproject/obs-studio/blob/32.2.2/plugins/mac-virtualcam/src/camera-extension/OBSCameraDeviceSource.swift)
and [stream-format implementation](https://github.com/obsproject/obs-studio/blob/32.2.2/plugins/mac-virtualcam/src/camera-extension/OBSCameraStreamSource.swift).
The failed manual-feed test was ended and automatic microphone-triggered
camera mode restored.

FaceTime later repeatedly rebuilt its audio-device list and reported "Call
Failed". Reloading the audio driver cleared the selection loop; restoring
automatic camera mode was followed by a successful user-confirmed FaceTime
call. The RemoteStreaming transport was retained. A trial legacy
JackIsConnected handler did not expose that property through the public HAL
and was removed from source, so it is not credited as the fix. The installed
trial bundle contains that unused handler; it can be replaced during the next
normal driver update without changing the working setup now.

Automatic mode was checked with a separate microphone consumer: opening the
virtual input started live 720p video and non-silent PC microphone audio;
releasing it stopped the camera and left the OBS device idle. Policy tests
cover manual Stop during an active call, disallowed microphone forwarding, and
sleep. Audio-producer tests cover creation with a restrictive umask, page-rounded
shared-memory sizes, PCM conversion and reopening the existing region.
