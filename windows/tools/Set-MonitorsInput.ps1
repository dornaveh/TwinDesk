param(
  [ValidateSet(5, 15, 6)][int]$InputCode = 15,
  [ValidateSet(5, 15, 6)][int]$CurrentInput = 6,
  [Parameter(Mandatory)][ValidatePattern('^UID[0-9]+$')][string]$TargetUid,
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class MonitorInputWriter {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct Physical {
    public IntPtr Handle;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Description;
  }
  [StructLayout(LayoutKind.Sequential)]
  public struct Rect { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct Info {
    public int Size; public Rect Monitor, Work; public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Device;
  }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct Device {
    public int Size;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string Name;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Description;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Id;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Key;
  }
  public delegate bool Callback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
  [DllImport("user32.dll")]
  static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, Callback callback, IntPtr data);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  static extern bool GetMonitorInfo(IntPtr monitor, ref Info info);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  static extern bool EnumDisplayDevices(string device, uint index, ref Device info, uint flags);
  [DllImport("dxva2.dll", SetLastError=true)]
  static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
  [DllImport("dxva2.dll", SetLastError=true)]
  static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] Physical[] physical);
  [DllImport("dxva2.dll")]
  static extern bool DestroyPhysicalMonitors(uint count, Physical[] physical);
  [DllImport("dxva2.dll", SetLastError=true)]
  static extern bool SetVCPFeature(IntPtr handle, byte code, uint value);
  [DllImport("dxva2.dll", SetLastError=true)]
  static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint maximum);

  public static string[] Write(uint value, uint currentInput, string targetUid, bool force) {
    var result = new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m,h,r,d) => {
      var info = new Info { Size = Marshal.SizeOf<Info>() };
      if (!GetMonitorInfo(m, ref info)) return true;
      var device = new Device { Size = Marshal.SizeOf<Device>() };
      if (!EnumDisplayDevices(info.Device, 0, ref device, 1)) return true;
      if (!device.Id.Contains(targetUid)) return true;
      uint count;
      if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m, out count) || count == 0) return true;
      var physical = new Physical[count];
      if (!GetPhysicalMonitorsFromHMONITOR(m, count, physical)) return true;
      try {
        foreach (var p in physical) {
          uint current, maximum;
          if (!force && (!GetVCPFeatureAndVCPFeatureReply(p.Handle, 0x60, IntPtr.Zero, out current, out maximum) || current != currentInput)) {
            result.Add(device.Id + ": skipped (not readable at requested current input)");
            continue;
          }
          var ok = SetVCPFeature(p.Handle, 0x60, value);
          result.Add(device.Id + ": " + (ok ? "command sent" : "error " + Marshal.GetLastWin32Error()));
        }
      } finally { DestroyPhysicalMonitors(count, physical); }
      return true;
    }, IntPtr.Zero);
    return result.ToArray();
  }
}
'@

[MonitorInputWriter]::Write([uint32]$InputCode, [uint32]$CurrentInput, $TargetUid, [bool]$Force)
