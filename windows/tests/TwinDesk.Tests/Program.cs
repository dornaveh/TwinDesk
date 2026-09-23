using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using TwinDesk;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); passed++; }
async Task MustFail(Func<Task> action, string name) { try { await action(); } catch (Exception e) when (e is not OperationCanceledException and not TimeoutException) { Check(true, name); return; } throw new Exception("FAILED: " + name); }

var bytes = RandomNumberGenerator.GetBytes(4000);
using (var stream = new MemoryStream())
{
    await Wire.WriteAsync(stream, PacketKind.Audio, bytes, default); stream.Position = 0;
    var result = await Wire.ReadAsync(new FragmentedStream(stream), default);
    Check(result.Kind == PacketKind.Audio && result.Data.SequenceEqual(bytes), "Fragmented TCP frames round-trip without data loss");
}
foreach (var length in new[] { int.MinValue, -1, 0, 65537, int.MaxValue })
{
    var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, length);
    await MustFail(async () => await Wire.ReadAsync(new MemoryStream(header), default), "Reject packet size " + length);
}
await MustFail(async () => await Wire.ReadAsync(new MemoryStream(new byte[] { 0, 0, 0, 1, 255 }), default), "Reject unknown packet type");
await MustFail(async () => await Wire.ReadAsync(new MemoryStream(new byte[] { 0, 0, 0, 5, 1 }), default), "Reject truncated packet");
Check(Wire.ValidAudio(new byte[1920]) && !Wire.ValidAudio(new byte[3]) && !Wire.ValidAudio(new byte[19204]) && !Wire.ValidAudio([]), "Enforce PCM alignment and bounded duration");
Check(Wire.TokenMatches("correct", "correct") && !Wire.TokenMatches("correct", "wrong") && !Wire.TokenMatches("correct", null), "Pairing token comparison");

var monitorHandles = new List<(string Id, string Name, nint Handle)> { ("left", "Left", 0), ("right", "Right", 17) };
var monitorRoutes = new[] { new MonitorRoute("right", 17, 18), new MonitorRoute("left", 15, 18) };
var monitorWrites = new List<(nint Handle, uint Input)>();
string? WriteMonitor(nint handle, uint value) { monitorWrites.Add((handle, value)); return null; }
var monitorErrors = Monitors.Select(monitorRoutes, Computer.Mac, monitorHandles, WriteMonitor);
Check(monitorErrors.Count == 0 && monitorWrites.SequenceEqual(new[] { ((nint)17, 18u), ((nint)0, 18u) }),
    "Both Mac inputs are selected by identity, including an enumerated zero handle");
monitorWrites.Clear();
monitorErrors = Monitors.Select(monitorRoutes, Computer.PC, monitorHandles, WriteMonitor);
Check(monitorErrors.Count == 0 && monitorWrites.SequenceEqual(new[] { ((nint)17, 17u), ((nint)0, 15u) }),
    "PC return uses each monitor's configured input, including a zero handle");
monitorWrites.Clear();
monitorErrors = Monitors.Select([new MonitorRoute("missing", 15, 18)], Computer.Mac, monitorHandles, WriteMonitor);
Check(monitorWrites.Count == 0 && monitorErrors.Count == 1 && monitorErrors[0].Contains("missing"),
    "An absent monitor never sends a command to the valid zero handle");
monitorWrites.Clear();
monitorErrors = Monitors.Select(monitorRoutes, Computer.PC, monitorHandles, (handle, value) => {
    monitorWrites.Add((handle, value)); return handle == 17 ? "Display rejected the request" : null;
});
Check(monitorWrites.Count == 2 && monitorErrors.Count == 1 && monitorErrors[0].Contains("Right: input command failed (Display rejected the request)"),
    "Monitor command failure is reported without skipping the other monitor's recovery");

var samsungId = @"\\?\DISPLAY#SAM0F35#test-port|0";
var samsungRoute = new MonitorRoute(samsungId, 15, 18);
var mixedHandles = new List<(string Id, string Name, nint Handle)> { (samsungId, "Samsung", 0), ("other-model", "Other", 17) };
monitorWrites.Clear();
monitorErrors = Monitors.Select([samsungRoute, new MonitorRoute("other-model", 15, 18)], Computer.Mac, mixedHandles, WriteMonitor);
Check(monitorErrors.Count == 0 && monitorWrites.SequenceEqual(new[] { ((nint)0, 6u), ((nint)17, 18u) }),
    "Samsung U32J59x HDMI 2 uses its verified device code while other monitors keep standard codes");
monitorWrites.Clear();
monitorErrors = Monitors.Select([samsungRoute], Computer.PC, mixedHandles, WriteMonitor);
Check(monitorErrors.Count == 0 && monitorWrites.SequenceEqual(new[] { ((nint)0, 15u) }),
    "Samsung DisplayPort recovery keeps its original input code");
Check(Monitors.NormalizeInput(samsungId.ToLowerInvariant(), 6) == 18 && Monitors.NormalizeInput(samsungId, 15) == 15 &&
    Monitors.NormalizeInput("other-model", 6) == 6 && Monitors.NormalizeInput(@"\\?\DISPLAY#SAM0F350#test-port|0", 6) == 6,
    "Samsung readback appears as HDMI 2 in the UI without rewriting other model identities");

Settings.DataDirectory = Path.Combine(Environment.CurrentDirectory, "local-data", "tests-" + Guid.NewGuid().ToString("N"));
Check(KeyboardState.Normalize(0xA1, 54, true, 0) == (54u, false) &&
    KeyboardState.Normalize(0xA1, 0, false, 0) == (54u, false) &&
    KeyboardState.Normalize(0xA0, 42, true, 0) == (42u, false), "Both Shift keys normalize regardless of spurious extended flags or missing scans");
Check(KeyboardState.Normalize(0xA3, 29, true, 0) == (29u, true) &&
    KeyboardState.Normalize(0xA5, 56, true, 0) == (56u, true) &&
    KeyboardState.Normalize(0x6F, 53, true, 0) == (53u, true), "Right Ctrl, right Alt and keypad divide keep extended mappings");
Check(KeyboardState.Normalize(0xA3, 0, false, 0xE01D) == (29u, true) &&
    KeyboardState.Normalize(0x41, 0, false, 30) == (30u, false), "Remapped keys with no scan use the Windows fallback mapping");
Check(KeyboardState.IsOwnInput(0x10, KeyboardState.OwnInputTag) && !KeyboardState.IsOwnInput(0x10, 0) &&
    !KeyboardState.IsOwnInput(0, KeyboardState.OwnInputTag), "Only TwinDesk's tagged injected releases bypass keyboard tracking");
foreach (var modifiers in new[] { (0xA2u, 0xA4u), (0xA3u, 0xA5u), (0x11u, 0x12u) })
{
    var keys = new KeyboardState();
    keys.Process(modifiers.Item1, true); keys.Process(modifiers.Item2, true);
    var down = keys.Process(0x7A, true); var repeat = keys.Process(0x7A, true);
    keys.Process(modifiers.Item1, false); keys.Process(modifiers.Item2, false);
    var lateRepeat = keys.Process(0x7A, true); var up = keys.Process(0x7A, false);
    Check(down.Swallow && down.Trigger && down.Target == Computer.PC && repeat.Swallow && !repeat.Trigger &&
        lateRepeat.Swallow && !lateRepeat.Trigger && up.Swallow && !up.Trigger && !keys.Process(0x7A, true).Swallow,
        $"Return shortcut captures down, repeats and release with modifier pair {modifiers}");
}
using var identity = new Identity();
using (var second = new Identity()) Check(second.Token == identity.Token && second.Fingerprint == identity.Fingerprint, "DPAPI-protected identity persists for this Windows account");
var stored = File.ReadAllBytes(Settings.PathFor("identity.protected"));
Check(!System.Text.Encoding.UTF8.GetString(stored).Contains(identity.Token), "Saved identity does not contain the pairing token in plaintext");
using (var pairing = JsonDocument.Parse(Convert.FromBase64String(identity.PairingCode("127.0.0.1", 48150))))
    Check(pairing.RootElement.GetProperty("fingerprint").GetString() == identity.Fingerprint, "Pairing pins the generated TLS certificate");

using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
await using var host = new BridgeServer("127.0.0.1", port, identity);
var audio = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
var returnRequests = 0;
host.ReturnToWindowsRequested += () => Interlocked.Increment(ref returnRequests);
host.Audio += value => audio.TrySetResult(value);
host.Status += message => Console.WriteLine("  " + message);
host.Start();
async Task<SslStream> Connect(string token, bool expectWelcome)
{
    var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
    var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == identity.Fingerprint);
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "TwinDesk", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
    await Wire.WriteAsync(ssl, PacketKind.Hello, JsonSerializer.SerializeToUtf8Bytes(new { version = 1, token }), default);
    if (expectWelcome)
    {
        using var timeout = new CancellationTokenSource(3000);
        var welcome = await Wire.ReadAsync(ssl, timeout.Token);
        using var welcomeJson = JsonDocument.Parse(welcome.Data);
        Check(welcome.Kind == PacketKind.Welcome && welcomeJson.RootElement.GetProperty("supportsReturnToWindows").GetBoolean(), "Authenticated peer receives welcome with return-menu capability");
    }
    return ssl;
}
using (var bad = await Connect("wrong", false))
    await MustFail(async () => { using var t = new CancellationTokenSource(3000); await Wire.ReadAsync(bad, t.Token); }, "Wrong token cannot open a session");
using var peer = await Connect(identity.Token, true);
Check(host.Connected, "Host tracks paired connection");
using (var duplicate = await Connect(identity.Token, false))
    await MustFail(async () => { using var t = new CancellationTokenSource(3000); await Wire.ReadAsync(duplicate, t.Token); }, "Second authenticated connection cannot evict active Mac");
Check(host.Connected, "Original connection survives rejected duplicate");
await Wire.WriteAsync(peer, PacketKind.Audio, new byte[1920], default);
Check((await audio.Task.WaitAsync(TimeSpan.FromSeconds(3))).Length == 1920, "System audio reaches independent receiver");

Packet packet;
foreach (var name in new[] { "returnToWindows", "execute", "activate" })
{
    var id = Guid.NewGuid().ToString();
    await Wire.WriteAsync(peer, PacketKind.Command, JsonSerializer.SerializeToUtf8Bytes(new { id, name }), default);
    using (var t = new CancellationTokenSource(3000)) { do { packet = await Wire.ReadAsync(peer, t.Token); } while (packet.Kind == PacketKind.Heartbeat); }
    using var reply = JsonDocument.Parse(packet.Data);
    Check(packet.Kind == PacketKind.Reply && reply.RootElement.GetProperty("id").GetString() == id &&
        reply.RootElement.GetProperty("ok").GetBoolean() == (name == "returnToWindows"), "Mac command allowlist acknowledgement: " + name);
}
Check(Volatile.Read(ref returnRequests) == 1 && host.Connected, "Only the allowed Mac command requests return; rejected commands keep the connection");
var command = host.CommandAsync("activate");
using (var t = new CancellationTokenSource(3000)) { do { packet = await Wire.ReadAsync(peer, t.Token); } while (packet.Kind == PacketKind.Heartbeat); }
using (var doc = JsonDocument.Parse(packet.Data))
{
    Check(packet.Kind == PacketKind.Command && doc.RootElement.GetProperty("name").GetString() == "activate", "Activation requires Mac acknowledgement");
    await Wire.WriteAsync(peer, PacketKind.Reply, JsonSerializer.SerializeToUtf8Bytes(new { id = doc.RootElement.GetProperty("id").GetString(), ok = true }), default);
}
await command.WaitAsync(TimeSpan.FromSeconds(3)); Check(true, "Matching acknowledgement completes activation");
Check(host.SendInput(JsonSerializer.SerializeToUtf8Bytes(new { kind = "key", scan = 30, extended = false, down = true })), "Input queued on authenticated connection");
using (var t = new CancellationTokenSource(3000)) { do { packet = await Wire.ReadAsync(peer, t.Token); } while (packet.Kind == PacketKind.Heartbeat); }
Check(packet.Kind == PacketKind.Input, "Input delivered in order after activation");
var pending = host.CommandAsync("deactivate");
host.Disconnect();
await MustFail(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)), "Disconnect fails pending operations promptly");
for (int i = 0; i < 30 && host.Connected; i++) await Task.Delay(20);
Check(!host.Connected && !host.SendInput([]), "Disconnected session cannot receive input");
using var fresh = await Connect(identity.Token, true);
await Wire.WriteAsync(fresh, PacketKind.Command, JsonSerializer.SerializeToUtf8Bytes(new { name = "returnToWindows" }), default);
await MustFail(async () => { using var t = new CancellationTokenSource(3000); while (true) await Wire.ReadAsync(fresh, t.Token); }, "Missing command ID terminates session without dispatch");
Check(Volatile.Read(ref returnRequests) == 1, "Malformed command never requests return");
for (int i = 0; i < 30 && host.Connected; i++) await Task.Delay(20);
using var audioPeer = await Connect(identity.Token, true);
await Wire.WriteAsync(audioPeer, PacketKind.Audio, new byte[3], default);
await MustFail(async () => { using var t = new CancellationTokenSource(3000); while (true) await Wire.ReadAsync(audioPeer, t.Token); }, "Malformed audio terminates the session");
var pcm = new LivePcmSource();
var ramp = Enumerable.Range(0, LivePcmSource.Capacity + 16).Select(i => (byte)(i % 251)).ToArray();
pcm.Push(ramp.AsSpan(0, 4000));
var partial = new byte[3992]; pcm.Read(partial, 0, partial.Length);
Check(partial.SequenceEqual(ramp.Take(3992)), "Live audio preserves PCM before ring wrap");
pcm.Push(ramp.AsSpan(4000));
var wrapped = new byte[ramp.Length - 3992]; pcm.Read(wrapped, 0, wrapped.Length);
Check(wrapped.SequenceEqual(ramp.Skip(3992)), "Live audio preserves frame order across ring wrap");
pcm.Push(ramp);
var newest = new byte[LivePcmSource.Capacity]; pcm.Read(newest, 0, newest.Length);
Check(newest.SequenceEqual(ramp.Skip(16)) && pcm.DroppedBytes == 16, "Oversized burst keeps freshest 40ms of complete frames");
pcm.Push(new byte[LivePcmSource.Capacity]); pcm.Push([1, 2, 3, 4]);
pcm.Read(newest, 0, newest.Length);
Check(newest.TakeLast(4).SequenceEqual(new byte[] {1, 2, 3, 4}) && pcm.DroppedBytes == 20, "Queue overflow discards oldest frames rather than recent sound");
var readBeforeSilence = pcm.BytesRead;
Array.Fill(newest, (byte)255); pcm.Read(newest, 0, newest.Length);
Check(newest.All(b => b == 0) && pcm.BytesRead == readBeforeSilence, "Underrun supplies silence without counting it as Mac audio");
pcm.Push([1, 2, 3, 4]); pcm.Clear(); pcm.Read(newest, 0, newest.Length);
Check(newest.All(b => b == 0) && pcm.QueuedBytes == 0, "Flush prevents old queued sound after reconnect");
await MustFail(() => { pcm.Push([1, 2, 3]); return Task.CompletedTask; }, "Reject partial stereo frames in playback queue");
if (args.Contains("--hardware-audio"))
{
    var output = AudioPlayer.Devices().FirstOrDefault(x => x.Name.Contains("Speakers") && x.Name.Contains("Realtek")) ?? new AudioDevice(-1, "Windows default output");
    using var playback = new AudioPlayer(output.Id);
    Console.WriteLine("  Playback backend: " + playback.Backend);
    playback.Push(new byte[1920]); playback.Push(new byte[1920]);
    await Task.Delay(250);
    Check(playback.BytesSubmitted == 3840 && playback.Failure == null, "Windows speaker output consumes silent PCM without playback error");
    Check(playback.Backend.StartsWith("WASAPI"), "Selected output opens through shared WASAPI rather than compatibility fallback");
    Console.WriteLine($"  Engine period={playback.EnginePeriodMilliseconds} ms; device queued={playback.DeviceQueuedMilliseconds:F2} ms; application queued={playback.QueuedMilliseconds:F2} ms");
    playback.Flush(); playback.Push(new byte[1920]); await Task.Delay(100);
    Check(playback.BytesSubmitted == 5760 && playback.Failure == null, "WASAPI flush restarts audio successfully");
}
else Console.WriteLine("SKIP 3 real playback checks (pass --hardware-audio to opt in)");
Console.WriteLine($"\n{passed} checks passed. No monitor inputs or keyboard hooks were changed by these tests.");

sealed class FragmentedStream(Stream inner) : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] b, int o, int c) => inner.Read(b, o, Math.Min(c, 3));
    public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => inner.ReadAsync(b[..Math.Min(b.Length, 3)], ct);
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
