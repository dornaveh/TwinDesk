#!/bin/zsh
set -euo pipefail

driver_dir="$(cd "$(dirname "$0")" && pwd)"
source_driver="$driver_dir/dist/TwinDeskMicrophone.driver"
installed_driver="/Library/Audio/Plug-Ins/HAL/TwinDeskMicrophone.driver"
backup_dir="$(cd "$driver_dir/.." && pwd)/previous-install"

test -d "$source_driver" || { echo "Build TwinDesk Microphone first." >&2; exit 1; }
codesign --verify --deep --strict "$source_driver"
mkdir -p "$backup_dir"

# An administrator AppleScript is not entitled to read the user's protected
# Documents folder. Stage and verify the bundle before requesting privileges.
stage_dir="$(mktemp -d /private/tmp/twindesk-driver-install.XXXXXX)"
trap 'rm -rf "$stage_dir"' EXIT
ditto "$source_driver" "$stage_dir/new.driver"
codesign --verify --deep --strict "$stage_dir/new.driver"
installer_uid="$(id -u)"
installer_gid="$(id -g)"
script="$stage_dir/install.sh"
cat > "$script" <<SCRIPT
#!/bin/bash
set -euo pipefail
installed='$installed_driver'
staged='$stage_dir'
mkdir -p /Library/Audio/Plug-Ins/HAL
codesign --verify --deep --strict "\$staged/new.driver"
# Copy completely before replacing the active bundle. Keep a rollback copy
# until installation and signature verification have both succeeded.
ditto "\$staged/new.driver" "\$staged/ready.driver"
chown -R root:wheel "\$staged/ready.driver"
chmod -R go-w "\$staged/ready.driver"
if test -d "\$installed"; then
    ditto "\$installed" "\$staged/old.driver"
fi
rollback() {
    rm -rf "\$installed"
    if test -d "\$staged/old.driver"; then
        ditto "\$staged/old.driver" "\$installed"
        chown -R $installer_uid:$installer_gid "\$staged/old.driver"
    fi
    rm -rf "\$staged/ready.driver"
}
trap rollback ERR
rm -rf "\$installed"
mv "\$staged/ready.driver" "\$installed"
codesign --verify --deep --strict "\$installed"
trap - ERR
if test -d "\$staged/old.driver"; then
    chown -R $installer_uid:$installer_gid "\$staged/old.driver"
fi
killall coreaudiod 2>/dev/null || true
SCRIPT
chmod 700 "$script"
/usr/bin/osascript -e "do shell script \"$script\" with administrator privileges"
if test -d "$stage_dir/old.driver"; then
    stamp="$(date +%Y%m%d-%H%M%S)"
    ditto "$stage_dir/old.driver" "$backup_dir/TwinDeskMicrophone-$stamp.driver"
fi
codesign --verify --deep --strict "$installed_driver"
echo "Installed TwinDesk Microphone. Reopen calling apps so it appears in their microphone list."
