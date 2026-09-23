using System.Runtime.InteropServices;

namespace TwinDesk;

internal sealed class LegacyAudioPlayer : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)] private struct Format { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Alignment, Bits, ExtraSize; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public nint Data; public uint Length, Recorded; public nuint User; public uint Flags, Loops; public nint Next; public nuint Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Caps { public ushort Manufacturer, Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; public uint Formats; public ushort Channels, Reserved; public uint Support; }
    private record Buffer(nint Header, nint Data, int Length);
    private readonly object gate = new();
    private readonly List<Buffer> buffers = [];
    private nint device;
    private int queued;
    private readonly System.Threading.Timer cleanup;
    public long BytesPlayed { get; private set; }
    public LegacyAudioPlayer(int id)
    {
        var format = new Format { Tag = 1, Channels = 2, Rate = 48000, BytesPerSecond = 192000, Alignment = 4, Bits = 16 };
        Check(waveOutOpen(out device, unchecked((uint)id), ref format, 0, 0, 0));
        cleanup = new System.Threading.Timer(_ => { lock (gate) Reap(); }, null, 20, 20);
    }
    public static List<AudioDevice> Devices()
    {
        var result = new List<AudioDevice> { new(-1, "Windows default output") };
        for (uint i = 0; i < waveOutGetNumDevs(); i++) if (waveOutGetDevCaps((nuint)i, out var caps, (uint)Marshal.SizeOf<Caps>()) == 0) result.Add(new((int)i, caps.Name));
        return result;
    }
    public void Push(byte[] data)
    {
        lock (gate)
        {
            if (device == 0) return;
            Reap();
            // Never accumulate delayed sound after a network stall.
            if (queued + data.Length > 19200) { waveOutReset(device); Reap(); }
            var sample = Marshal.AllocHGlobal(data.Length);
            var header = Marshal.AllocHGlobal(Marshal.SizeOf<Header>());
            var prepared = false;
            try
            {
                Marshal.Copy(data, 0, sample, data.Length);
                Marshal.StructureToPtr(new Header { Data = sample, Length = (uint)data.Length }, header, false);
                Check(waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<Header>())); prepared = true;
                Check(waveOutWrite(device, header, (uint)Marshal.SizeOf<Header>()));
                buffers.Add(new Buffer(header, sample, data.Length)); queued += data.Length;
            }
            catch { if (prepared) waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<Header>()); Marshal.FreeHGlobal(header); Marshal.FreeHGlobal(sample); throw; }
        }
    }
    public void Flush() { lock (gate) { if (device != 0) { waveOutReset(device); Reap(); } } }
    private void Reap()
    {
        for (int i = buffers.Count - 1; i >= 0; i--)
        {
            var b = buffers[i];
            if ((Marshal.PtrToStructure<Header>(b.Header).Flags & 1) == 0) continue;
            if (waveOutUnprepareHeader(device, b.Header, (uint)Marshal.SizeOf<Header>()) != 0) continue;
            Marshal.FreeHGlobal(b.Header); Marshal.FreeHGlobal(b.Data);
            buffers.RemoveAt(i); queued -= b.Length; BytesPlayed += b.Length;
        }
    }
    public void Dispose()
    {
        cleanup.Dispose();
        lock (gate) { if (device == 0) return; waveOutReset(device); Reap(); waveOutClose(device); device = 0; }
    }
    private static void Check(uint code) { if (code != 0) throw new IOException($"Windows audio output failed (code {code}). Select another speaker output."); }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint device, uint id, ref Format format, nint callback, nuint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveOutGetDevCaps(nuint id, out Caps caps, uint size);
}
