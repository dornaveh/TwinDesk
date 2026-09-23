using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TwinDesk;

public record MonitorDescription(string Id, string Name, uint? Input, string Capabilities);
public record MonitorRoute(string Id, uint PcInput, uint MacInput);

public static class Monitors
{
    // SAM0F35 (Samsung U32J59x) advertises HDMI 2 as 18 but accepts 6.
    // Keep saved routes and UI values standard; translate only at the device edge.
    private static bool UsesSamsungHdmi2Code(string id) =>
        id.StartsWith(@"\\?\DISPLAY#SAM0F35#", StringComparison.OrdinalIgnoreCase);
    private static uint NativeInput(string id, uint value) =>
        UsesSamsungHdmi2Code(id) && value == 18 ? 6u : value;
    internal static uint NormalizeInput(string id, uint value) =>
        UsesSamsungHdmi2Code(id) && value == 6 ? 18u : value;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Physical { public nint Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Info { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device { public int Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key; }
    private delegate bool Callback(nint monitor, nint hdc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint hdc, nint clip, Callback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint h, ref Info info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint n, ref Device info, uint flags);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out] Physical[] physical);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, Physical[] physical);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetVCPFeatureAndVCPFeatureReply(nint h, byte code, nint type, out uint current, out uint max);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool SetVCPFeature(nint h, byte code, uint value);

    // Keep all physical handles open for the whole batch: changing an input can
    // immediately change Windows' logical display enumeration.
    private static void WithHandles(Action<List<(string Id, string Name, nint Handle)>> action)
    {
        var items = new List<(string, string, nint)>();
        var arrays = new List<Physical[]>();
        try
        {
            EnumDisplayMonitors(0, 0, (h, _, _, _) =>
            {
                var info = new Info { Size = Marshal.SizeOf<Info>() };
                if (!GetMonitorInfo(h, ref info)) return true;
                var device = new Device { Size = Marshal.SizeOf<Device>() };
                var id = EnumDisplayDevices(info.Device, 0, ref device, 1) ? device.Id : info.Device;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(h, out var count) || count == 0) return true;
                var physical = new Physical[count];
                if (!GetPhysicalMonitorsFromHMONITOR(h, count, physical)) return true;
                arrays.Add(physical);
                for (var i = 0; i < physical.Length; i++) items.Add(($"{id}|{i}", info.Device, physical[i].Handle));
                return true;
            }, 0);
            action(items);
        }
        finally { foreach (var a in arrays) DestroyPhysicalMonitors((uint)a.Length, a); }
    }

    public static List<MonitorDescription> Scan()
    {
        var result = new List<MonitorDescription>();
        WithHandles(items =>
        {
            foreach (var m in items)
            {
                uint? input = GetVCPFeatureAndVCPFeatureReply(m.Handle, 0x60, 0, out var v, out _) ? NormalizeInput(m.Id, v) : null;
                // Capability-string requests are unnecessary for input switching and
                // can stall on monitors with unreliable DDC/CI implementations.
                result.Add(new MonitorDescription(m.Id, m.Name, input, ""));
            }
        });
        return result;
    }

    public static List<string> Select(IReadOnlyList<MonitorRoute> routes, Computer target)
    {
        var errors = new List<string>();
        foreach (var route in routes)
        {
            // A successful input switch can change the display topology and
            // invalidate handles acquired for the other monitor. Enumerate again
            // before each command rather than reusing a stale batch.
            WithHandles(items => errors.AddRange(Select([route], target, items, (handle, value) =>
                SetVCPFeature(handle, 0x60, value) ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message)));
        }
        return errors;
    }

    internal static List<string> Select(IReadOnlyList<MonitorRoute> routes, Computer target,
        List<(string Id, string Name, nint Handle)> items, Func<nint, uint, string?> selectInput)
    {
        var errors = new List<string>();
        foreach (var route in routes)
        {
            // Physical-monitor handles are opaque: this PC's driver returns zero
            // for an enumerated monitor. Test the identity match, not its handle.
            var index = items.FindIndex(x => x.Id == route.Id);
            if (index < 0) { errors.Add($"Monitor unavailable: {route.Id}"); continue; }
            var m = items[index];
            var value = target == Computer.PC ? route.PcInput : route.MacInput;
            var error = selectInput(m.Handle, NativeInput(m.Id, value));
            if (error is not null) errors.Add($"{m.Name}: input command failed ({error})");
        }
        return errors;
    }
}
