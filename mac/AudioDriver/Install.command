#!/bin/zsh
set -euo pipefail

driver_dir="$(cd "$(dirname "$0")" && pwd)"
source_driver="$driver_dir/dist/TwinDeskMicrophone.driver"
installed_driver="/Library/Audio/Plug-Ins/HAL/TwinDeskMicrophone.driver"
backup_dir="$(cd "$driver_dir/.." && pwd)/previous-install"

test -d "$source_driver" || { echo "Build TwinDesk Microphone first." >&2; exit 1; }
codesign --verify --deep --strict "$source_driver"
mkdir -p "$backup_dir"

stamp="$(date +%Y%m%d-%H%M%S)"
script="$(mktemp /tmp/twindesk-install-driver.XXXXXX)"
trap 'rm -f "$script"' EXIT
cat > "$script" <<SCRIPT
set -e
mkdir -p '/Library/Audio/Plug-Ins/HAL'
if test -d '$installed_driver'; then
  mv '$installed_driver' '$backup_dir/TwinDeskMicrophone-$stamp.driver'
fi
ditto '$source_driver' '$installed_driver'
chown -R root:wheel '$installed_driver'
chmod -R go-w '$installed_driver'
killall coreaudiod 2>/dev/null || true
SCRIPT
chmod 700 "$script"
/usr/bin/osascript -e "do shell script \"$script\" with administrator privileges"
codesign --verify --deep --strict "$installed_driver"
echo "Installed TwinDesk Microphone. Reopen calling apps so it appears in their microphone list."
