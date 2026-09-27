using System.Net.Security;
using System.Threading.Channels;
using System.Text.Json;

namespace TwinDesk;

// A dedicated writer ensures camera congestion never queues behind keyboard/audio traffic.
internal sealed class CameraPeer : IDisposable
{
    private readonly SslStream stream;
    private readonly CancellationTokenSource lifetime;
    private readonly Channel<(byte[] Data, long Generation)> frames;
    private readonly bool microphone;
    private readonly object demandGate = new();
    private bool demanded;
    private long demandGeneration;
    public bool Demanded { get { lock (demandGate) return demanded && Active; } }
    public long DemandGeneration { get { lock (demandGate) return demandGeneration; } }
    public event Action<bool>? DemandChanged;
    public bool Active => !lifetime.IsCancellationRequested;
    public CameraPeer(SslStream stream, CancellationToken owner, bool microphone = false)
    {
        this.stream = stream;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
        this.microphone = microphone;
        frames = Channel.CreateBounded<(byte[] Data, long Generation)>(new BoundedChannelOptions(microphone ? 10 : 1) {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }
    public bool Send(byte[] data, long generation)
    {
        lock (demandGate)
            return demanded && demandGeneration == generation && Active &&
                (microphone ? data.Length is > 0 and <= 1920 && data.Length % 4 == 0 : data.Length is >= 4 and <= Wire.MaxCameraJpeg) && frames.Writer.TryWrite((data, generation));
    }
    public async Task Run()
    {
        var writer = WriteLoop();
        try
        {
            while (Active)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(8));
                var packet = await Wire.ReadAsync(stream, idle.Token);
                if (packet.Kind == PacketKind.CameraStatus && packet.Data.Length <= 512)
                {
                    using var status = JsonDocument.Parse(packet.Data);
                    var next = status.RootElement.GetProperty("demand").GetBoolean();
                    SetDemand(next);
                    continue;
                }
                if (packet.Kind != PacketKind.Heartbeat || packet.Data.Length != 0)
                    throw new InvalidDataException("Unexpected camera packet.");
            }
        }
        finally
        {
            Close();
            try { await writer; } catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private void SetDemand(bool next)
    {
        lock (demandGate)
        {
            if (demanded == next) return;
            demanded = next; demandGeneration++;
            while (frames.Reader.TryRead(out _)) { }
        }
        DemandChanged?.Invoke(next);
    }
    private async Task WriteLoop()
    {
        try
        {
            while (Active)
            {
                (byte[] Data, long Generation)? frame = null;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
                {
                    idle.CancelAfter(TimeSpan.FromSeconds(2));
                    try { frame = await frames.Reader.ReadAsync(idle.Token); }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                if (frame is { } queued && (!Demanded || queued.Generation != DemandGeneration)) continue;
                if (frame is null) await Wire.WriteAsync(stream, PacketKind.Heartbeat, [], deadline.Token);
                else if (microphone) await Wire.WriteAsync(stream, PacketKind.Audio, frame.Value.Data, deadline.Token);
                else await Wire.WriteCameraAsync(stream, frame.Value.Data, deadline.Token);
            }
        }
        finally { Close(); }
    }
    public void Close()
    {
        lifetime.Cancel();
        frames.Writer.TryComplete();
        while (frames.Reader.TryRead(out _)) { }
    }
    public void Dispose() { Close(); lifetime.Dispose(); }
}
