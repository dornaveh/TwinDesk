#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

OUT="$PWD/dist/TwinDeskMicrophone.driver"
mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources"
xcrun clang -std=c11 -O2 -arch arm64 -mmacosx-version-min=14.2 -fPIC -bundle \
  TwinDeskAudioDriver.c -o "$OUT/Contents/MacOS/TwinDeskAudioDriver" \
  -framework CoreAudio -framework CoreFoundation -framework Foundation
cp Info.plist "$OUT/Contents/Info.plist"
cp LEMURCAM-LICENSE.md "$OUT/Contents/Resources/LemurCam-LICENSE.txt"

SIGNING_IDENTITY="${TWINDESK_SIGNING_IDENTITY:-}"
if [[ -z "$SIGNING_IDENTITY" ]]; then
  SIGNING_IDENTITY="$(security find-identity -v -p codesigning | awk '/Apple Development/ {print $2; exit}')"
fi
if [[ -z "$SIGNING_IDENTITY" ]]; then
  echo "No code-signing identity is available for the audio driver." >&2
  exit 1
fi
codesign --force --sign "$SIGNING_IDENTITY" --timestamp=none "$OUT"
codesign --verify --deep --strict "$OUT"
echo "Built $OUT (not installed)."
