#!/bin/zsh
set -euo pipefail

project_root="$(cd "$(dirname "$0")" && pwd)"
source_app="$project_root/dist/TwinDesk.app"
installed_app="/Applications/TwinDesk.app"
backup_dir="$project_root/previous-install"
agent="$HOME/Library/LaunchAgents/local.twindesk.login.plist"

test -d "$source_app" || { echo "Build TwinDesk on this Mac first." >&2; exit 1; }
if /usr/bin/pgrep -f '^/Applications/TwinDesk.app/Contents/MacOS/TwinDesk' >/dev/null; then
  echo "The old TwinDesk app is still running; close it before installing." >&2
  exit 1
fi

codesign --verify --deep --strict "$source_app"
mkdir -p "$backup_dir" "$HOME/Library/LaunchAgents"
if test -d "$installed_app"; then
  mv "$installed_app" "$backup_dir/TwinDesk-$(date +%Y%m%d-%H%M%S).app"
fi
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
