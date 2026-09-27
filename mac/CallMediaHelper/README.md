# TwinDesk Calls for Mac

Receive a Windows webcam and its microphone in Mac calling apps.
This helper runs alongside the existing TwinDesk keyboard/mouse app. It does not
read that app's Keychain item, inject input, or capture the screen.

## Requirements

- Apple Silicon Mac, macOS 14.2 or later, paired Windows TwinDesk with call-media support.
- [OBS Studio](https://obsproject.com/) 30 or newer. Enable OBS Virtual Camera in
  System Settings → General → Login Items & Extensions → Camera Extensions.
  Close OBS after enabling it; TwinDesk feeds its separately installed extension.
- [BlackHole 2ch](https://existential.audio/blackhole/). Install the signed driver;
  macOS may need an audio-service restart or restart before it appears.

## Build and install

Run `./Build.command`. Copy `dist/TwinDesk-Calls.app` to `/Applications`.
The helper is signed locally by default; changed builds may require renewed
system-audio approval. Set `TWINDESK_SIGNING_IDENTITY` for an available stable identity.

During setup, transfer speaker-audio ownership from the regular TwinDesk app to
Calls: disable the regular app's old global audio capture while leaving its
keyboard/mouse connection running. Calls then forwards speaker audio automatically.
For the legacy app, a setup operator can persist `sendAudio=false` in
`local.twindesk.mac` and relaunch the exact trusted app binary, preserving its
Keychain/Accessibility identity. Coordinate the relaunch to avoid unwanted
monitor recovery. Do not run both audio capture paths together.

Open Windows TwinDesk's call-media controls, select the webcam and its microphone,
and enable sharing. Copy its call-media setup code into TwinDesk Calls and start.
Approve the helper's System Audio Recording request. No screen capture is used.
The setup code is a scoped media credential, not permission to control the PC.
It is stored under `~/Library/Application Support/TwinDesk Calls/` with directory
permissions 0700 and file permissions 0600. Never publish or log it.

For automatic login, add `/Applications/TwinDesk-Calls.app` as a login item.
It accepts `--startup` to hide its initial window. The helper runs without a menu-bar or Dock icon. Choose **Start camera feed** / **Stop camera feed** in the main **TwinDesk** menu; **Camera & microphone settings…** opens its setup window. The camera always starts off after launch or sleep. Microphone permission is remembered. Capture starts while another Mac process actively uses BlackHole 2ch as an input, or while the user has manually started the camera feed; the latter keeps camera and microphone transport paired for calling apps that do not open a virtual microphone until a call begins.
speaker forwarding is automatic while the helper runs. Keep the regular TwinDesk
app running as well.

Forwarded speaker audio follows the Mac output device's volume and mute controls.
The helper applies the device's reported volume gain to the captured audio before
sending it to Windows; Windows playback volume still applies separately.

In the Mac calling app choose:

- **Camera:** OBS Virtual Camera
- **Microphone:** BlackHole 2ch
- **Speakers:** normal Mac system output, not BlackHole

FaceTime currently accepts the OBS video but can silence audio from virtual input
devices even when BlackHole is receiving valid samples. The same failure occurs
when BlackHole is wrapped in a Core Audio aggregate device. Similar failures are
[reported for BlackHole](https://github.com/ExistentialAudio/BlackHole/issues/526)
and [documented by another virtual-mic vendor](https://support.voicemod.net/hc/en-us/articles/22169835107090--macOS-How-to-Use-Voicemod-with-FaceTime-or-any-Communication-Apps-Without-Built-in-Audio-Settings).
This is an app/macOS voice-processing limitation,
not a lost TwinDesk network stream. For FaceTime, use an iPhone through Continuity
[Continuity Camera](https://support.apple.com/guide/iphone/use-iphone-as-a-webcam-iph5b912c30c/ios)
or a directly connected microphone. Other calling apps still need an
end-to-end microphone check before relying on them.

Calls sends microphone audio only to BlackHole and captures only the normal
speaker output while excluding its own process. This avoids returning microphone
audio to the PC speakers. The PC must remain awake and connected. The main menu uses private local command/status files containing only media controls and status, never pairing credentials. Commands expire after ten seconds, are consumed once, and only named camera/microphone controls or helper quit are accepted. Quitting TwinDesk stops the helper.

Camera frames
and microphone/speaker PCM are streamed in memory, not saved as recordings.

Separate authenticated TLS connections carry camera, microphone, and speaker
streams; the helper obtains a fresh media session after reconnecting. Queues are
bounded. The helper accepts 1080p or 720p JPEG up to 30fps. OBS exposes a fixed 1080p virtual device, so 720p input is scaled to that output; this does not add detail. The tested webcam delivered roughly 15fps even at the requested 720p/30 setting.
A calling service may independently reduce outgoing resolution.

## Validation

The Mac helper builds; synthetic 1080p frames were submitted to the enabled OBS
sink, and BlackHole output opened at 48kHz stereo. Microphone demand detection passed a separate-process input open/close test. Windows defaults camera/microphone demand to off; manual camera Start sends explicit camera and microphone demand messages. Stop drops queued camera media and closes the camera connection. The installed single-menu Start command received live 720p frames from Windows, and FaceTime displayed that video. During a live microphone test, TwinDesk received PC mic audio and rendered a full BlackHole buffer with non-silent levels, while FaceTime's own audio diagnostics remained at silence. Main control and speaker audio reconnected after the coordinated update. Other calling apps still need user verification before claiming end-to-end microphone compatibility.
