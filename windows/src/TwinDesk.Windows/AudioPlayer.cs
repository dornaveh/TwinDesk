using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TwinDesk;

public record AudioDevice(int Id, string Name) { public override string ToString() => Name; }

// Shared mode preserves simultaneous Windows audio. NAudio supplies the Windows
// interop; queue freshness and the network PCM format remain TwinDesk's policy.
public sealed class AudioPlayer : IDisposable
{
    private readonly object gate = new();
    private readonly LivePcmSource source = new();
    private WasapiPlayer? output;
    private MMDevice? endpoint;
    private LegacyAudioPlayer? fallback;
    private Exception? playbackError;
    private bool disposed;
    public string Backend { get; private set; } = "";
    public bool LowLatencyActive => output?.LowLatencyActive == true;
    public double EnginePeriodMilliseconds => output?.LatencyMilliseconds ?? 0;
    public long BytesSubmitted => fallback?.BytesPlayed ?? source.BytesRead;
    public long DroppedBytes => source.DroppedBytes;
    public double QueuedMilliseconds => source.QueuedBytes / 192.0;
    public double DeviceQueuedMilliseconds { get { lock (gate) return output?.CurrentLatency.TotalMilliseconds ?? 0; } }
    public string? Failure => Volatile.Read(ref playbackError)?.Message;

    public AudioPlayer(int id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            endpoint = id == -1 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(EndpointId(id));
            output = new WasapiPlayerBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync()
                .WithLatency(20).WithLowLatency(false).WithMmcssThreadPriority("Audio").Build();
            output.Init(source);
            output.PlaybackStopped += (_, e) => { if (e.Exception is not null) Volatile.Write(ref playbackError, e.Exception); };
            Backend = $"WASAPI shared; low latency={output.LowLatencyActive}; engine period={output.LatencyMilliseconds} ms";
            if (!output.LowLatencyActive) Backend += $"; {output.LowLatencyUnavailableReason}";
            output.Play();
        }
        catch (Exception e) when (e is COMException or NotSupportedException or InvalidOperationException or IOException)
        {
            output?.Dispose(); output = null; endpoint?.Dispose(); endpoint = null;
            fallback = new LegacyAudioPlayer(id);
            Backend = $"Compatibility playback; WASAPI unavailable: {e.Message}";
        }
    }
    public static List<AudioDevice> Devices() => LegacyAudioPlayer.Devices();
    public void Push(byte[] data)
    {
        if (!Wire.ValidAudio(data)) throw new InvalidDataException("Invalid audio frame.");
        lock (gate)
        {
            if (disposed) return;
            if (Failure is { } error) throw new IOException($"Audio output stopped: {error}. Select another speaker output.");
            if (fallback is not null) fallback.Push(data); else source.Push(data);
        }
    }
    public void Flush()
    {
        lock (gate)
        {
            if (disposed) return;
            if (fallback is not null) { fallback.Flush(); return; }
            output!.Stop(); source.Clear(); output.Play();
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            output?.Dispose(); output = null;
            endpoint?.Dispose(); endpoint = null;
            fallback?.Dispose(); fallback = null;
            source.Clear();
        }
    }
    // Preserve saved waveOut IDs and duplicate monitor names: resolve the exact
    // underlying endpoint instead of guessing from friendly names or list order.
    internal static string EndpointId(int id)
    {
        var sizePointer = Marshal.AllocHGlobal(4);
        nint text = 0;
        try
        {
            if (waveOutMessage((nint)id, 0x0812, sizePointer, 0) != 0) throw new IOException("Cannot identify the selected audio output.");
            var size = Marshal.ReadInt32(sizePointer);
            if (size < 2 || size > 65536) throw new IOException("Invalid audio endpoint identifier size.");
            text = Marshal.AllocHGlobal(size);
            if (waveOutMessage((nint)id, 0x0811, text, (nint)size) != 0) throw new IOException("Cannot identify the selected audio output.");
            return Marshal.PtrToStringUni(text) ?? throw new IOException("Missing audio endpoint identifier.");
        }
        finally { Marshal.FreeHGlobal(sizePointer); if (text != 0) Marshal.FreeHGlobal(text); }
    }
    [DllImport("winmm.dll")] private static extern uint waveOutMessage(nint id, uint message, nint first, nint second);
}

// Never wait to fill a buffer. Keep at most 40 ms of *unsubmitted* stereo PCM;
// after a burst discard the oldest complete frames, not the newly arrived sound.
internal sealed class LivePcmSource : IWaveProvider
{
    internal const int Capacity = 7680;
    private readonly object gate = new();
    private readonly byte[] ring = new byte[Capacity];
    private int head, count;
    private long read, dropped;
    public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
    public long BytesRead { get { lock (gate) return read; } }
    public long DroppedBytes { get { lock (gate) return dropped; } }
    public int QueuedBytes { get { lock (gate) return count; } }
    public void Push(ReadOnlySpan<byte> data)
    {
        if (data.Length % 4 != 0) throw new InvalidDataException("Partial stereo PCM frame.");
        lock (gate)
        {
            if (data.Length > Capacity) { dropped += data.Length - Capacity; data = data[^Capacity..]; }
            var discard = Math.Max(0, count + data.Length - Capacity);
            head = (head + discard) % Capacity; count -= discard; dropped += discard;
            var tail = (head + count) % Capacity;
            var first = Math.Min(data.Length, Capacity - tail);
            data[..first].CopyTo(ring.AsSpan(tail)); data[first..].CopyTo(ring);
            count += data.Length;
        }
    }
    public int Read(byte[] buffer, int offset, int requested) => Read(buffer.AsSpan(offset, requested));
    public int Read(Span<byte> destination)
    {
        var requested = destination.Length;
        if (requested % 4 != 0) throw new InvalidDataException("Partial stereo PCM read.");
        lock (gate)
        {
            var take = Math.Min(requested, count);
            var first = Math.Min(take, Capacity - head);
            ring.AsSpan(head, first).CopyTo(destination);
            ring.AsSpan(0, take - first).CopyTo(destination[first..]);
            destination[take..].Clear();
            head = (head + take) % Capacity; count -= take; read += take;
            return requested; // Silence keeps the device running during a gap.
        }
    }
    public void Clear() { lock (gate) { dropped += count; head = count = 0; } }
}
