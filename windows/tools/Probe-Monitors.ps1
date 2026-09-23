$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class MonitorProbe {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct Physical { public IntPtr Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string Description; }
  public delegate bool Callback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
  [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, Callback callback, IntPtr data);
  [DllImport("dxva2.dll", SetLastError=true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
  [DllImport("dxva2.dll", SetLastError=true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] Physical[] physical);
  [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, Physical[] physical);
  [DllImport("dxva2.dll", SetLastError=true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, IntPtr type, out uint current, out uint maximum);
  [DllImport("dxva2.dll", SetLastError=true)] static extern bool GetCapabilitiesStringLength(IntPtr handle, out uint length);
  [DllImport("dxva2.dll", SetLastError=true, CharSet=CharSet.Ansi)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr handle, StringBuilder value, uint length);
  public static string[] Read() {
    var result = new List<string>();
    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m,h,r,d) => {
      uint count;
      if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m,out count)) { result.Add("Enumeration failed: " + Marshal.GetLastWin32Error()); return true; }
      var physical = new Physical[count];
      if (!GetPhysicalMonitorsFromHMONITOR(m,count,physical)) return true;
      try {
        foreach(var p in physical) {
          uint current, maximum, length;
          bool ok = GetVCPFeatureAndVCPFeatureReply(p.Handle,0x60,IntPtr.Zero,out current,out maximum);
          result.Add(p.Description + ": input=" + (ok ? current.ToString() : "unreadable, error=" + Marshal.GetLastWin32Error()));
          if(GetCapabilitiesStringLength(p.Handle,out length) && length > 0 && length < 65536) {
            var sb = new StringBuilder((int)length);
            if(CapabilitiesRequestAndCapabilitiesReply(p.Handle,sb,length)) result.Add(sb.ToString());
          }
        }
      } finally { DestroyPhysicalMonitors(count,physical); }
      return true;
    },IntPtr.Zero);
    return result.ToArray();
  }
}
'@
[MonitorProbe]::Read()
