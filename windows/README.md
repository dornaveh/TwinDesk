# Windows host

Build with the .NET 10 SDK from the repository root:

```powershell
powershell -NoProfile -File windows/tools/Build-Windows.ps1
```

Run `windows/dist/TwinDesk-Windows/TwinDesk.exe`. The .NET 10 Desktop Runtime
is required. Choose the PC's reachable IPv4 address, start the connection and
copy the pairing code to the Mac app. Permit TCP 48150 inbound only on the
trusted interface/from the intended Mac. The app binds a single address;
it does not automatically open the firewall.

## Daily use and startup

The tray menu provides switching and opening the settings window. Ctrl+Alt+F12
toggles, Ctrl+Alt+F10 selects Mac, and Ctrl+Alt+F11 returns local input to PC.
Monitor routing must match actual cables. Refresh reads the monitor list while
connected without changing input or ending the connection.

After building and testing, optionally run:

```powershell
powershell -NoProfile -File windows/tools/Install-Windows-Startup.ps1
```

This registers a per-user scheduled task `TwinDesk-<username>` at sign-in,
with a 15-second delay. It launches the built app in the tray and selects
`windows/local-data/installed-<username>` for its settings and protected identity.
Keep the build directory in place or reinstall the startup registration after
moving it. The script preserves existing destination data and only migrates
AppData settings when the destination file is absent. It removes the old
TwinDesk Run entry if present. Do not run it from a throwaway checkout if you
already have a working installation elsewhere.

Without that registration, settings normally live in `%LOCALAPPDATA%\TwinDesk`.
The `--data <directory>` option or a local `data-directory.txt` beside the app
can select a different runtime directory. Do not commit or share these files.

## Optional direct Ethernet and C:/E: file sharing

This is optional and independent of KVM. The supplied helper is deliberately
limited to a dedicated `192.168.77.0/30` link and Windows C: and E: drives. For
other addresses or shares, configure Windows networking and SMB manually.
Never run the helper against your normal internet/LAN adapter.

Preview the helper's intended changes first:

```powershell
powershell -NoProfile -File windows/tools/Configure-DirectLink.ps1 -InterfaceAlias Ethernet -WhatIf
```

For actual changes, run the same command without `-WhatIf` in an Administrator
PowerShell. `-Account` defaults to the current Windows account; supply the
intended individual account explicitly if elevating as a different user.

The script assigns the named wired adapter `192.168.77.1/30`, with no gateway
or DNS, and creates authenticated encrypted `TwinDesk-C` and `TwinDesk-E`
shares with full share access for that account. It adds firewall allowances
for TCP 445/48150 from `192.168.77.2` on that adapter only. It checks for some
conflicting existing settings; review your existing routes/firewall policy too.
It does not alter NTFS permissions or disable existing broader firewall rules.

Set Mac Ethernet manually to `192.168.77.2`, mask `255.255.255.252`, no router
or DNS. Keep Wi-Fi for internet access. Finder can connect to
`smb://192.168.77.1/TwinDesk-C` and `smb://192.168.77.1/TwinDesk-E` using your
Windows account password, not the Windows Hello PIN. Those shares allow file
creation/modification/deletion wherever that account's file permissions allow;
protected, locked, encrypted and cloud-only files retain their restrictions.

`Remove-FileSharing.ps1 -WhatIf` previews removing the two shares and two named
firewall rules. Applying it keeps the files and Ethernet configuration.

## Audio

Choose an output before starting the connection. Shared WASAPI allows Windows
and Mac audio together; compatible output formats use IAudioClient3's supported
minimum period. A 40 ms maximum application queue discards old frames after a
burst, without intentionally waiting to fill it. Current audio-device changes
require stopping and restarting the connection. Bluetooth delay is separate.
