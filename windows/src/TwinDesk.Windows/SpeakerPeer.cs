using System.Net.Security;

namespace TwinDesk;

// Receives the helper's speaker mix. It cannot accept commands or inject input.
internal sealed class SpeakerPeer : IDisposable
{
    private readonly SslStream stream;
    private readonly CancellationTokenSource lifetime;
    public bool Active => !lifetime.IsCancellationRequested;
    public SpeakerPeer(SslStream stream, CancellationToken owner)
    {
        this.stream = stream;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(owner);
    }
    public async Task Run(Action<byte[]> audio)
    {
        var writer = Heartbeat();
        try
        {
            while (Active)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(8));
                var packet = await Wire.ReadAsync(stream, idle.Token);
                if (packet.Kind == PacketKind.Audio && Wire.ValidAudio(packet.Data)) audio(packet.Data);
                else if (packet.Kind != PacketKind.Heartbeat || packet.Data.Length != 0)
                    throw new InvalidDataException("Unexpected speaker packet.");
            }
        }
        finally
        {
            Close();
            try { await writer; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
    }
    private async Task Heartbeat()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await Wire.WriteAsync(stream, PacketKind.Heartbeat, [], timeout.Token);
            }
        }
        finally { Close(); }
    }
    public void Close() => lifetime.Cancel();
    public void Dispose() { Close(); lifetime.Dispose(); }
}
