using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TwinDesk;

public sealed class MicrophoneCapture : IAsyncDisposable
{
    private readonly MMDevice endpoint;
    private readonly WasapiRecorder recorder;
    private readonly Action<byte[]> send;
    private readonly Action<string> failed;
    private int stopped;
    public static IReadOnlyList<MicrophoneDevice> Devices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (device) result.Add(Describe(device));
        return result;
    }
    public static MicrophoneDevice? Describe(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(id);
            return Describe(device);
        }
        catch { return null; } // A legacy endpoint may have been removed entirely.
    }
    private static MicrophoneDevice Describe(MMDevice device)
    {
        var container = "";
        try
        {
            // PKEY_Device_ContainerId, defined by the Windows SDK.
            var key = new PropertyKey(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
            if (device.Properties.TryGetValue<Guid>(key, out var value) && value != Guid.Empty) container = value.ToString("D");
        }
        catch { /* Some drivers do not expose physical device identity. */ }
        return new(device.ID, device.FriendlyName, container);
    }
    // Construct only after explicit opt-in and a ready authenticated microphone connection.
    public MicrophoneCapture(string id, Action<byte[]> send, Action<string> failed)
    {
        this.send = send; this.failed = failed;
        using var enumerator = new MMDeviceEnumerator();
        endpoint = enumerator.GetDevice(id);
        try
        {
            // Shared WASAPI performs conversion from the selected device's native format.
            recorder = new WasapiRecorderBuilder().WithDevice(endpoint).WithSharedMode().WithEventSync()
                .WithFormat(new WaveFormat(48000, 16, 2)).WithBufferLength(20).WithMmcssThreadPriority("Audio").Build();
            recorder.DataAvailable += DataAvailable;
            recorder.RecordingStopped += (_, e) => {
                if (Interlocked.Exchange(ref stopped, 1) == 0)
                    failed(e.Exception is null ? "Microphone stopped. Enable sharing again to retry." : "Microphone stopped: " + e.Exception.Message);
            };
        }
        catch { endpoint.Dispose(); throw; }
    }
    public void Start() { if (Volatile.Read(ref stopped) == 0) recorder.StartRecording(); }
    private void DataAvailable(ReadOnlySpan<byte> bytes, AudioClientBufferFlags flags, long position, long qpc)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        // No buffer borrowed from WASAPI escapes the callback, and no local monitor is opened.
        if (bytes.Length % 4 != 0) { Cancel(); failed("Microphone returned an invalid audio frame."); return; }
        for (var offset = 0; offset < bytes.Length && Volatile.Read(ref stopped) == 0; offset += 1920)
        {
            var chunk = bytes.Slice(offset, Math.Min(1920, bytes.Length - offset)).ToArray();
            if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(chunk);
            send(chunk);
        }
    }
    public void Cancel() { Interlocked.Exchange(ref stopped, 1); recorder.StopRecording(); }
    public async ValueTask DisposeAsync()
    {
        Cancel();
        await recorder.DisposeAsync();
        endpoint.Dispose();
    }
}
