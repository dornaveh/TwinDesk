using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace TwinDesk;

public record OutputEndpoint(string Id, string Name);
public record OutputSnapshot(string? DefaultId, IReadOnlyList<OutputEndpoint> Devices);
public interface IAudioOutputBackend : IDisposable
{
    string Backend { get; }
    string? Failure { get; }
    long BytesSubmitted { get; }
    long DroppedBytes { get; }
    double QueuedMilliseconds { get; }
    void Push(byte[] data);
}
public interface IAudioOutputEnvironment
{
    OutputSnapshot Snapshot();
    IAudioOutputBackend Open(string endpointId);
}

public sealed class WindowsAudioEnvironment : IAudioOutputEnvironment
{
    public OutputSnapshot Snapshot()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<OutputEndpoint>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            using (device) devices.Add(new(device.ID, device.FriendlyName));
        string? current = null;
        try { using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); current = device.ID; }
        catch (System.Runtime.InteropServices.COMException) { /* No default output is a normal unplug state. */ }
        return new(current, devices);
    }
    public IAudioOutputBackend Open(string endpointId) => new AudioPlayer(endpointId);
}

// Only Tick/Dispose (the worker) open or close native devices. Push never waits
// for reconnection, touches COM, or queues an error per incoming audio packet.
public sealed class AudioRecovery : IDisposable
{
    private readonly object gate = new();
    private readonly IAudioOutputEnvironment environment;
    private IAudioOutputBackend? backend;
    private string? selected, target, failure;
    private long revision, appliedRevision = -1, submitted, dropped;
    private TimeSpan retryAt, openedAt;
    private int failures;
    private bool disposed;
    public event Action<string>? StatusChanged;
    public string Status { get; private set; } = "Opening audio output…";
    public bool Available { get { lock (gate) return backend is not null && revision == appliedRevision && failure is null && backend.Failure is null; } }
    public long BytesSubmitted { get { lock (gate) return submitted + (backend?.BytesSubmitted ?? 0); } }
    public long DroppedBytes { get { lock (gate) return dropped + (backend?.DroppedBytes ?? 0); } }
    public double QueuedMilliseconds { get { lock (gate) return backend?.QueuedMilliseconds ?? 0; } }
    public AudioRecovery(IAudioOutputEnvironment environment, string? selection) { this.environment = environment; selected = selection; }
    public void Select(string? endpointId) { lock (gate) { if (disposed || selected == endpointId) return; selected = endpointId; revision++; } }
    public void Flush() { lock (gate) { if (!disposed) revision++; } }
    public void Push(byte[] data)
    {
        if (!Wire.ValidAudio(data)) return;
        lock (gate)
        {
            if (disposed || backend is null || revision != appliedRevision || failure is not null) { dropped += data.Length; return; }
            // Backend.Push only appends to a bounded memory buffer.
            try { backend.Push(data); }
            catch (Exception e) { failure = e.Message; dropped += data.Length; }
        }
    }
    private IAudioOutputBackend? Detach()
    {
        var old = backend; backend = null;
        if (old is not null) { submitted += old.BytesSubmitted; dropped += old.DroppedBytes + (long)(old.QueuedMilliseconds * 192); }
        return old;
    }
    private TimeSpan Backoff() => TimeSpan.FromSeconds(1 << Math.Min(failures++, 3));
    private static void Close(IAudioOutputBackend? old) { try { old?.Dispose(); } catch { /* Removed device must not prevent recovery. */ } }
    private void Report(string text)
    {
        lock (gate) { if (disposed || Status == text) return; Status = text; }
        if (StatusChanged is { } handlers)
            foreach (Action<string> handler in handlers.GetInvocationList())
                try { handler(text); } catch { /* A closing UI subscriber cannot invalidate the playback backend. */ }
    }
    // Worker-only; the injected snapshot and monotonic time make loss/retry paths deterministic.
    public void Tick(OutputSnapshot snapshot, TimeSpan now)
    {
        IAudioOutputBackend? old = null;
        string? openId, notice = null;
        long openingRevision;
        lock (gate)
        {
            if (disposed) return;
            var wanted = selected ?? snapshot.DefaultId;
            openId = snapshot.Devices.Any(d => d.Id == wanted) ? wanted : null;
            if (appliedRevision != revision || target != openId)
            {
                old = Detach(); target = openId; appliedRevision = revision;
                retryAt = now; failures = 0; failure = null;
            }
            var error = failure ?? backend?.Failure;
            if (error is not null)
            {
                old ??= Detach(); failure = null; retryAt = now + Backoff();
                notice = "Audio output unavailable. Reconnecting automatically: " + error;
            }
            if (backend is not null && now - openedAt >= TimeSpan.FromSeconds(10)) failures = 0;
            if (openId is null) notice = selected is null ? "Waiting for a Windows default audio output." : "Selected audio output is unavailable. Waiting for it to reconnect.";
            if (backend is not null || now < retryAt) openId = null;
            openingRevision = revision;
        }
        Close(old);
        if (notice is not null) Report(notice);
        if (openId is null) return;
        IAudioOutputBackend? opened = null;
        try
        {
            opened = environment.Open(openId);
            bool accepted;
            lock (gate)
            {
                accepted = !disposed && revision == openingRevision;
                if (accepted) { backend = opened; openedAt = now; }
            }
            if (!accepted) { Close(opened); return; }
            Report("Audio output: " + snapshot.Devices.First(d => d.Id == openId).Name + " · " + opened.Backend);
        }
        catch (Exception e)
        {
            Close(opened);
            lock (gate) { if (disposed || revision != openingRevision) return; retryAt = now + Backoff(); }
            Report("Audio output unavailable. Reconnecting automatically: " + e.Message);
        }
    }
    public void SnapshotFailed(string message)
    {
        // Drop new packets until the next successful inventory read; worker closes the device.
        IAudioOutputBackend? old;
        lock (gate) { if (disposed) return; old = Detach(); }
        Close(old);
        Report("Cannot read Windows audio outputs. Retrying automatically: " + message);
    }
    public void Dispose()
    {
        IAudioOutputBackend? old;
        lock (gate) { if (disposed) return; disposed = true; old = Detach(); }
        Close(old);
    }
}

public sealed class AudioOutput : IAsyncDisposable
{
    private readonly IAudioOutputEnvironment environment = new WindowsAudioEnvironment();
    private readonly AudioRecovery recovery;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object lifecycle = new();
    private Task? worker, disposal;
    private bool closing;
    public event Action<string>? StatusChanged;
    public event Action<OutputSnapshot>? DevicesChanged;
    public bool Available => recovery.Available;
    public string Status => recovery.Status;
    public long BytesSubmitted => recovery.BytesSubmitted;
    public long DroppedBytes => recovery.DroppedBytes;
    public double QueuedMilliseconds => recovery.QueuedMilliseconds;
    public AudioOutput(string? selected)
    {
        recovery = new(environment, selected);
        recovery.StatusChanged += text => StatusChanged?.Invoke(text);
    }
    public void Start() { lock (lifecycle) { if (!closing) worker ??= Task.Run(Run); } }
    private void Wake() { lock (lifecycle) { if (closing) return; try { wake.Release(); } catch (SemaphoreFullException) { } } }
    public void Select(string? endpointId) { recovery.Select(endpointId); Wake(); }
    public void Flush() { recovery.Flush(); Wake(); }
    public void Push(byte[] data) { if (!Volatile.Read(ref closing)) recovery.Push(data); }
    private async Task Run()
    {
        var clock = Stopwatch.StartNew();
        OutputSnapshot? previous = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var snapshot = environment.Snapshot();
                    recovery.Tick(snapshot, clock.Elapsed);
                    if (previous is null || previous.DefaultId != snapshot.DefaultId || !previous.Devices.SequenceEqual(snapshot.Devices))
                    {
                        previous = snapshot;
                        if (DevicesChanged is { } handlers)
                            foreach (Action<OutputSnapshot> handler in handlers.GetInvocationList())
                                try { handler(snapshot); } catch { /* UI may have closed while a refresh was queued. */ }
                    }
                }
                catch (Exception e) { recovery.SnapshotFailed(e.Message); }
                await wake.WaitAsync(TimeSpan.FromSeconds(1), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { recovery.Dispose(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (disposal is null) { closing = true; stop.Cancel(); disposal = Finish(worker ?? Task.CompletedTask); }
            return new(disposal);
        }
    }
    private async Task Finish(Task run)
    {
        try { await run; }
        finally { recovery.Dispose(); stop.Dispose(); wake.Dispose(); }
    }
}
