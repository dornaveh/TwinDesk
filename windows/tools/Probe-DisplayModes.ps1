$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class DisplayModeProbe {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  public struct DevMode {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string DeviceName;
    public ushort SpecVersion, DriverVersion, Size, DriverExtra;
    public uint Fields;
    public int PositionX, PositionY;
    public uint DisplayOrientation, DisplayFixedOutput;
    public short Color, Duplex, YResolution, TTOption, Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string FormName;
    public ushort LogPixels;
    public uint BitsPerPel, Width, Height, DisplayFlags, Frequency;
    public uint ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2;
    public uint PanningWidth, PanningHeight;
  }
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="EnumDisplaySettingsW")]
  static extern bool EnumDisplaySettings(string name, int mode, ref DevMode value);
  public static string[] Read() {
    var result = new System.Collections.Generic.List<string>();
    for (int i = 1; i <= 8; i++) {
      var name = @"\\.\DISPLAY" + i;
      var mode = new DevMode { Size = (ushort)Marshal.SizeOf<DevMode>() };
      if (EnumDisplaySettings(name, -1, ref mode))
        result.Add(name + ": " + mode.Width + "x" + mode.Height + " @ " + mode.Frequency + " Hz, position " + mode.PositionX + "," + mode.PositionY);
    }
    return result.ToArray();
  }
}
'@

[DisplayModeProbe]::Read()
