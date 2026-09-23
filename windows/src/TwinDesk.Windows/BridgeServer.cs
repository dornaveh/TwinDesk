using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Channels;

namespace TwinDesk;

public sealed class BridgeServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly Identity identity;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim handshakes = new(4);
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private int clientId;
    private Task? acceptTask;
    private Peer? peer;
    public bool Connected => Volatile.Read(ref peer) is not null;
    public event Action<bool>? ConnectionChanged;
    public event Action<byte[]>? Audio;
    public event Action<string>? Status;
    public event Action? ReturnToWindowsRequested;

    public BridgeServer(string address, int port, Identity identity)
    {
        var ip = IPAddress.Parse(address);
        if (ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any)) throw new ArgumentException("Select one IPv4 address for this PC.");
        listener = new TcpListener(ip, port); this.identity = identity;
    }
    public void Start() { listener.Start(4); acceptTask = AcceptLoop(); }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!await handshakes.WaitAsync(0, stop.Token)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref clientId);
                var task = Serve(client);
                clients[id] = task;
                _ = task.ContinueWith(_ => clients.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        Peer? candidate = null;
        var slotHeld = true;
        try
        {
            using (client)
            using (var stream = new SslStream(client.GetStream(), false))
            {
                client.NoDelay = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(8));
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = identity.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false, CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                }, deadline.Token);
                var hello = await Wire.ReadAsync(stream, deadline.Token);
                if (hello.Kind != PacketKind.Hello) throw new InvalidDataException("Pairing required.");
                using var json = JsonDocument.Parse(hello.Data);
                if (json.RootElement.GetProperty("version").GetInt32() != 1 ||
                    !Wire.TokenMatches(identity.Token, json.RootElement.GetProperty("token").GetString()))
                    throw new InvalidDataException("Pairing not accepted.");
                candidate = new Peer(stream, stop.Token);
                if (Interlocked.CompareExchange(ref peer, candidate, null) is not null) throw new InvalidDataException("A Mac is already connected.");
                handshakes.Release(); slotHeld = false;
                await Wire.WriteAsync(stream, PacketKind.Welcome, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sampleRate = 48000, channels = 2, bits = 16, supportsReturnToWindows = true }), deadline.Token);
                ConnectionChanged?.Invoke(true);
                Status?.Invoke("Mac connected over an encrypted link.");
                await candidate.Run(Audio, ReturnToWindowsRequested);
            }
        }
        catch (Exception e) when (e is IOException or AuthenticationException or SocketException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (!stop.IsCancellationRequested) Status?.Invoke(candidate is null ? "A connection could not complete pairing." : "Mac connection ended.");
        }
        finally
        {
            if (candidate is not null)
            {
                candidate.Close();
                if (Interlocked.CompareExchange(ref peer, null, candidate) == candidate) ConnectionChanged?.Invoke(false);
            }
            if (slotHeld) handshakes.Release();
        }
    }
    public Task CommandAsync(string name, CancellationToken ct = default) =>
        Volatile.Read(ref peer)?.Command(name, ct) ?? Task.FromException(new IOException("Mac is not connected."));
    public bool SendInput(byte[] data) => Volatile.Read(ref peer)?.Enqueue(new Packet(PacketKind.Input, data)) == true;
    public void Disconnect() => Volatile.Read(ref peer)?.Close();
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); Disconnect();
        if (acceptTask is not null) await acceptTask;
        await Task.WhenAll(clients.Values);
        stop.Dispose(); handshakes.Dispose();
    }

    private sealed class Peer
    {
        private readonly SslStream stream;
        private readonly CancellationTokenSource lifetime;
        private readonly Channel<Packet> output = Channel.CreateBounded<Packet>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private readonly ConcurrentDictionary<string, TaskCompletionSource> pending = new();
        public Peer(SslStream stream, CancellationToken stop) { this.stream = stream; lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop); }
        public bool Enqueue(Packet packet) => !lifetime.IsCancellationRequested && output.Writer.TryWrite(packet);
        public async Task Command(string name, CancellationToken ct)
        {
            var id = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = completion;
            try
            {
                if (!Enqueue(new Packet(PacketKind.Command, JsonSerializer.SerializeToUtf8Bytes(new { id, name })))) throw new IOException("Connection queue is full.");
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
            }
            finally { pending.TryRemove(id, out _); }
        }
        public async Task Run(Action<byte[]>? audio, Action? returnToWindows)
        {
            var write = WriteLoop(); var heartbeat = Heartbeat();
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(8));
                    var packet = await Wire.ReadAsync(stream, idle.Token);
                    switch (packet.Kind)
                    {
                        case PacketKind.Audio:
                            if (!Wire.ValidAudio(packet.Data)) throw new InvalidDataException("Invalid audio frame.");
                            audio?.Invoke(packet.Data); break;
                        case PacketKind.Heartbeat: break;
                        case PacketKind.Command:
                            using (var doc = JsonDocument.Parse(packet.Data))
                            {
                                var root = doc.RootElement;
                                if (!root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                                    string.IsNullOrWhiteSpace(idValue.GetString()) || idValue.GetString()!.Length > 64)
                                    throw new InvalidDataException("Invalid command identifier.");
                                var id = idValue.GetString();
                                var ok = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                                    name.GetString() == "returnToWindows" && returnToWindows is not null;
                                if (!Enqueue(new Packet(PacketKind.Reply, JsonSerializer.SerializeToUtf8Bytes(new {
                                    id, ok, error = ok ? null : "Unsupported command."
                                })))) throw new IOException("Connection queue is full.");
                                // Acceptance only: UI posts its normal switch without blocking this receive loop.
                                if (ok) returnToWindows!();
                            }
                            break;
                        case PacketKind.Reply:
                            using (var doc = JsonDocument.Parse(packet.Data))
                            {
                                var root = doc.RootElement;
                                if (pending.TryRemove(root.GetProperty("id").GetString()!, out var completion))
                                {
                                    if (root.GetProperty("ok").GetBoolean()) completion.TrySetResult();
                                    else completion.TrySetException(new IOException(root.TryGetProperty("error", out var error) ? error.GetString() : "Mac could not complete the command."));
                                }
                            }
                            break;
                        default: throw new InvalidDataException("Unexpected packet.");
                    }
                }
            }
            finally { Close(); try { await Task.WhenAll(write, heartbeat); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { } }
        }
        private async Task WriteLoop()
        {
            try { await foreach (var p in output.Reader.ReadAllAsync(lifetime.Token)) await Wire.WriteAsync(stream, p.Kind, p.Data, lifetime.Token); }
            finally { Close(); }
        }
        private async Task Heartbeat()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                if (!Enqueue(new Packet(PacketKind.Heartbeat, []))) { Close(); break; }
        }
        public void Close()
        {
            lifetime.Cancel(); output.Writer.TryComplete();
            foreach (var task in pending.Values) task.TrySetException(new IOException("Mac disconnected."));
        }
    }
}
