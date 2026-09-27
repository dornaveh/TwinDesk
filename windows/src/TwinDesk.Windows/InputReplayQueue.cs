using System.Threading.Channels;

namespace TwinDesk;

// SendInput can synchronously invoke a low-level hook. Keep replay off the
// hook's message thread, preserve order, and never wait for it from that thread.
internal sealed class InputReplayQueue : IDisposable
{
    private readonly Channel<Action> pending = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    internal Task Completion { get; }
    public InputReplayQueue()
    {
        Completion = Task.Run(async () => {
            await foreach (var replay in pending.Reader.ReadAllAsync().ConfigureAwait(false)) replay();
        });
    }
    public void Enqueue(Action replay) => pending.Writer.TryWrite(replay);
    public void Dispose() => pending.Writer.TryComplete();
}
