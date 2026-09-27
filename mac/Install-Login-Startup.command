#!/bin/zsh
set -euo pipefail

project_root="$(cd "$(dirname "$0")" && pwd)"
source_app="$project_root/dist/TwinDesk.app"
installed_app="/Applications/TwinDesk.app"
source_helper="$project_root/CallMediaHelper/dist/TwinDesk-Calls.app"
installed_helper="/Applications/TwinDesk-Calls.app"
installed_driver="/Library/Audio/Plug-Ins/HAL/TwinDeskMicrophone.driver"
backup_dir="$project_root/previous-install"
agent="$HOME/Library/LaunchAgents/local.twindesk.login.plist"

test -d "$source_app" || { echo "Build TwinDesk on this Mac first." >&2; exit 1; }
test -d "$source_helper" || { echo "Build TwinDesk Calls first." >&2; exit 1; }
test -d "$installed_driver" || { echo "Install TwinDesk Microphone first by running mac/AudioDriver/Install.command." >&2; exit 1; }
if /usr/bin/pgrep -f '^/Applications/TwinDesk(-Calls)?.app/Contents/MacOS/TwinDesk' >/dev/null; then
  echo "The old TwinDesk app is still running; close it before installing." >&2
  exit 1
fi

codesign --verify --deep --strict "$source_app"
codesign --verify --deep --strict "$source_helper"
codesign --verify --deep --strict "$installed_driver"
mkdir -p "$backup_dir" "$HOME/Library/LaunchAgents"
if test -d "$installed_app"; then
  mv "$installed_app" "$backup_dir/TwinDesk-$(date +%Y%m%d-%H%M%S).app"
fi
if test -d "$installed_helper"; then
  mv "$installed_helper" "$backup_dir/TwinDesk-Calls-$(date +%Y%m%d-%H%M%S).app"
fi
ditto "$source_helper" "$installed_helper"
codesign --verify --deep --strict "$installed_helper"
ditto "$source_app" "$installed_app"
codesign --verify --deep --strict "$installed_app"

cat > "$agent" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>local.twindesk.login</string>
  <key>ProgramArguments</key><array>
    <string>/usr/bin/open</string>
    <string>-a</string>
    <string>/Applications/TwinDesk.app</string>
    <string>--args</string>
    <string>--startup</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>LimitLoadToSessionType</key><string>Aqua</string>
</dict></plist>
PLIST
plutil -lint "$agent"
launchctl bootout "gui/$(id -u)/local.twindesk.login" >/dev/null 2>&1 || true
launchctl bootstrap "gui/$(id -u)" "$agent"
echo "TwinDesk will open in the Mac menu bar at sign-in."
