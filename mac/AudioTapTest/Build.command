#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
out="$PWD/dist/TwinDesk Audio Test.app"
mkdir -p "$out/Contents/MacOS"
xcrun swiftc -swift-version 5 -parse-as-library -O -target arm64-apple-macosx14.2 AudioTapTest/*.swift Shared/*.swift TwinDesk/Connection.swift -o "$out/Contents/MacOS/TwinDeskAudioTest" -framework SwiftUI -framework CoreAudio -framework AppKit -framework Network -framework Security -framework CryptoKit
cp AudioTapTest/Info.plist "$out/Contents/Info.plist"
codesign --force --sign - "$out"
codesign --verify --deep --strict "$out"
echo "$out"
