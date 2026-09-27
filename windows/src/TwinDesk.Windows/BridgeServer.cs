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
    private readonly object cameraGate = new();
    private CameraPeer? camera;
    private bool cameraEnabled;
    private VideoMode cameraMode = VideoMode.Hd;
    private CameraPeer? microphone;
    private bool microphoneEnabled;
    private SpeakerPeer? speakers;
    public bool CameraConnected { get { lock (cameraGate) return cameraEnabled && camera?.Demanded == true; } }
    public bool MicrophoneConnected { get { lock (cameraGate) return microphoneEnabled && microphone?.Demanded == true; } }
    public event Action<bool>? CameraConnectionChanged;
    public event Action<bool>? MicrophoneConnectionChanged;
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
    public void Start() { listener.Start(4); acceptTask = Task.Run(AcceptLoop); }
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
                if (json.RootElement.GetProperty("version").GetInt32() != 1)
                    throw new InvalidDataException("Pairing not accepted.");
                if (json.RootElement.TryGetProperty("role", out var role))
                {
                    if (!Wire.TokenMatches(identity.MediaToken, json.RootElement.GetProperty("token").GetString()))
                        throw new InvalidDataException("Media pairing not accepted.");
                    var owner = Volatile.Read(ref peer);
                    if (owner is null || !owner.Active) throw new InvalidDataException("Connect the main Mac app first.");
                    if (role.GetString() == "media-session")
                    {
                        await Wire.WriteAsync(stream, PacketKind.Welcome, JsonSerializer.SerializeToUtf8Bytes(new {
                            version = 1, role = "media-session", cameraSession = owner.CameraSession
                        }), deadline.Token);
                        return;
                    }
                    var isMicrophone = role.GetString() == "microphone";
                    var isSpeakers = role.GetString() == "speakers";
                    if (!isMicrophone && !isSpeakers && role.GetString() != "camera") throw new InvalidDataException("Unknown connection role.");
                    if (!json.RootElement.TryGetProperty("cameraSession", out var session) ||
                        !Wire.TokenMatches(owner.CameraSession, session.GetString()))
                        throw new InvalidDataException("A current paired Mac session is required.");
                    handshakes.Release(); slotHeld = false;
                    if (isSpeakers) await ServeSpeakers(stream, owner, deadline.Token);
                    else await ServeCamera(stream, owner, deadline.Token, isMicrophone);
                    return;
                }
                if (!Wire.TokenMatches(identity.Token, json.RootElement.GetProperty("token").GetString()))
                    throw new InvalidDataException("Pairing not accepted.");
                candidate = new Peer(stream, stop.Token);
                if (Interlocked.CompareExchange(ref peer, candidate, null) is not null) throw new InvalidDataException("A Mac is already connected.");
                handshakes.Release(); slotHeld = false;
                await Wire.WriteAsync(stream, PacketKind.Welcome, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, sampleRate = 48000, channels = 2, bits = 16, supportsReturnToWindows = true, supportsCamera = true, supportsMicrophone = true, cameraSession = candidate.CameraSession }), deadline.Token);
                ConnectionChanged?.Invoke(true);
                Status?.Invoke("Mac connected over an encrypted link.");
                await candidate.Run(data => { lock (cameraGate) { if (speakers?.Active != true) Audio?.Invoke(data); } }, ReturnToWindowsRequested);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or AuthenticationException or SocketException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            if (!stop.IsCancellationRequested) Status?.Invoke(candidate is null ? "A connection could not complete pairing." : "Mac connection ended.");
        }
        finally
        {
            if (candidate is not null)
            {
                candidate.Close();
                if (Interlocked.CompareExchange(ref peer, null, candidate) == candidate)
                {
                    CloseCamera();
                    ConnectionChanged?.Invoke(false);
                }
            }
            if (slotHeld) handshakes.Release();
        }
    }
    public Task CommandAsync(string name, CancellationToken ct = default) =>
        Volatile.Read(ref peer)?.Command(name, ct) ?? Task.FromException(new IOException("Mac is not connected."));
    public bool SendInput(byte[] data) => Volatile.Read(ref peer)?.Enqueue(new Packet(PacketKind.Input, data)) == true;
    public string CreateMediaSetupCode()
    {
        var owner = Volatile.Read(ref peer);
        if (owner is null || !owner.Active) throw new IOException("Connect your Mac to TwinDesk first.");
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var pairingCode = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
            version = 1, host = endpoint.Address.ToString(), port = endpoint.Port,
            token = identity.MediaToken, fingerprint = identity.Fingerprint
        }));
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { pairingCode, session = owner.CameraSession }));
    }
    public void Disconnect() { Volatile.Read(ref peer)?.Close(); CloseCamera(); }
    public void SetCameraSharing(bool enabled)
    {
        lock (cameraGate)
        {
            cameraEnabled = enabled;
            camera?.Close(); // Discard queued frames; the Mac reconnects to get the new state.
        }
        CameraConnectionChanged?.Invoke(false);
    }
    public void SetCameraMode(VideoMode mode)
    {
        lock (cameraGate) { cameraMode = mode; camera?.Close(); }
        CameraConnectionChanged?.Invoke(false);
    }
    public void SetMicrophoneSharing(bool enabled)
    {
        lock (cameraGate) { microphoneEnabled = enabled; microphone?.Close(); }
        MicrophoneConnectionChanged?.Invoke(false);
    }
    // Bind a capture to this particular media connection. A late callback from a
    // cancelled capture can never leak a frame into a newly paired/reconnected session.
    public Action<byte[]> MediaSender(bool isMicrophone)
    {
        CameraPeer? session;
        long generation;
        lock (cameraGate) { session = isMicrophone ? microphone : camera; generation = session?.DemandGeneration ?? 0; }
        return data => {
            lock (cameraGate)
                if (session is not null && peer?.Active == true &&
                    (isMicrophone ? microphoneEnabled && microphone == session : cameraEnabled && camera == session)) session.Send(data, generation);
        };
    }
    private void CloseCamera()
    {
        lock (cameraGate) { camera?.Close(); microphone?.Close(); speakers?.Close(); }
        CameraConnectionChanged?.Invoke(false);
        MicrophoneConnectionChanged?.Invoke(false);
    }
    private async Task ServeSpeakers(SslStream stream, Peer owner, CancellationToken handshake)
    {
        SpeakerPeer session;
        lock (cameraGate)
        {
            if (peer != owner || !owner.Active || speakers is not null) throw new InvalidDataException("Speaker session is unavailable.");
            session = new SpeakerPeer(stream, owner.Token);
            speakers = session;
        }
        try
        {
            await Wire.WriteAsync(stream, PacketKind.Welcome, JsonSerializer.SerializeToUtf8Bytes(new {
                version = 1, role = "speakers", sampleRate = 48000, channels = 2, bits = 16
            }), handshake);
            await Wire.WriteAsync(stream, PacketKind.CameraStatus, JsonSerializer.SerializeToUtf8Bytes(new {
                enabled = true, message = "Mac speaker audio forwarding active."
            }), handshake);
            await session.Run(data => { lock (cameraGate) { if (speakers == session && peer == owner && owner.Active) Audio?.Invoke(data); } });
        }
        finally
        {
            session.Close();
            lock (cameraGate) { if (speakers == session) speakers = null; }
            session.Dispose();
        }
    }
    private async Task ServeCamera(SslStream stream, Peer owner, CancellationToken handshake, bool isMicrophone)
    {
        CameraPeer session;
        bool enabled;
        VideoMode mode;
        lock (cameraGate)
        {
            if (peer != owner || !owner.Active || (isMicrophone ? microphone : camera) is not null) throw new InvalidDataException("Media session is unavailable.");
            enabled = isMicrophone ? microphoneEnabled : cameraEnabled;
            mode = cameraMode;
            session = new CameraPeer(stream, owner.Token, isMicrophone);
            session.DemandChanged += demanded => {
                bool accepted;
                lock (cameraGate) accepted = demanded && peer == owner && owner.Active &&
                    (isMicrophone ? microphoneEnabled && microphone == session : cameraEnabled && camera == session);
                if (isMicrophone) MicrophoneConnectionChanged?.Invoke(accepted); else CameraConnectionChanged?.Invoke(accepted);
            };
            if (isMicrophone) microphone = session; else camera = session;
        }
        try
        {
            var welcome = isMicrophone
                ? JsonSerializer.SerializeToUtf8Bytes(new { version = 1, role = "microphone", sampleRate = 48000, channels = 2, bits = 16 })
                : JsonSerializer.SerializeToUtf8Bytes(new { version = 1, role = "camera", codec = "jpeg", width = mode.Width, height = mode.Height, fps = mode.Fps });
            await Wire.WriteAsync(stream, PacketKind.Welcome, welcome, handshake);
            var label = isMicrophone ? "microphone" : "camera";
            await Wire.WriteAsync(stream, PacketKind.CameraStatus, JsonSerializer.SerializeToUtf8Bytes(new { enabled, message = enabled ? $"PC {label} sharing enabled." : $"Enable {label} sharing in TwinDesk on Windows." }), handshake);
            await session.Run();
        }
        finally
        {
            session.Close();
            lock (cameraGate)
            {
                if (isMicrophone && microphone == session) microphone = null;
                if (!isMicrophone && camera == session) camera = null;
            }
            if (isMicrophone) MicrophoneConnectionChanged?.Invoke(false); else CameraConnectionChanged?.Invoke(false);
            session.Dispose();
        }
    }
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
        public string CameraSession { get; } = Guid.NewGuid().ToString("N");
        public bool Active => !lifetime.IsCancellationRequested;
        public CancellationToken Token => lifetime.Token;
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
