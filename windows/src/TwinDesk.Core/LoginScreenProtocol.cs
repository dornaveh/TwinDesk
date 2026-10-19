using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace TwinDesk;

// Minimal RFB client for Apple's login screen. The caller supplies an SSH-tunnel
// stream; this component never opens an unprotected network connection itself.
public sealed class LoginScreenProtocol : IAsyncDisposable
{
    public const int MaximumPixels = 32 * 1024 * 1024;
    private readonly Stream stream;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private Task? writer;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public byte[] Pixels { get; private set; } = [];
    public event Action? Frame;

    public LoginScreenProtocol(Stream stream) => this.stream = stream;

    public async Task InitializeAsync(string username, string password, CancellationToken ct)
    {
        var version = Encoding.ASCII.GetString(await Read(12, ct));
        if (!version.StartsWith("RFB 003.", StringComparison.Ordinal) || version[^1] != '\n' ||
            !int.TryParse(version.AsSpan(8, 3), out var minor) || minor < 8)
            throw new InvalidDataException("The Mac did not offer a compatible login screen.");
        await stream.WriteAsync("RFB 003.008\n"u8.ToArray(), ct);
        var count = (await Read(1, ct))[0];
        if (count == 0) { await DiscardText(ct); throw new InvalidOperationException("Screen Sharing refused the connection. Check that your Mac account is allowed."); }
        var methods = await Read(count, ct);
        if (!methods.Contains((byte)30)) throw new InvalidOperationException("Mac account sign-in is unavailable in Screen Sharing. Enable Screen Sharing for your Mac account; a shared VNC password is not used.");
        await stream.WriteAsync(new byte[] { 30 }, ct);
        var dh = await Read(4, ct);
        var length = BinaryPrimitives.ReadUInt16BigEndian(dh.AsSpan(2));
        if (length is < 16 or > 512) throw new InvalidDataException("Unsupported Mac login authentication parameters.");
        var prime = await Read(length, ct); var serverPublic = await Read(length, ct);
        var response = CreateAppleResponse(BinaryPrimitives.ReadUInt16BigEndian(dh), prime, serverPublic, username, password);
        try { await stream.WriteAsync(response, ct); }
        finally { CryptographicOperations.ZeroMemory(response); password = string.Empty; }
        if (BinaryPrimitives.ReadUInt32BigEndian(await Read(4, ct)) != 0)
        {
            await DiscardText(ct);
            throw new InvalidOperationException("The Mac refused Screen Sharing sign-in. Check the account password and Screen Sharing access for that account.");
        }
        await stream.WriteAsync(new byte[] { 1 }, ct); // Share the physical console.
        var init = await Read(24, ct);
        Resize(BinaryPrimitives.ReadUInt16BigEndian(init), BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2)));
        await Discard(BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20)), 1024 * 1024, ct);
        var format = new byte[20]; format[4] = 32; format[5] = 24; format[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(format.AsSpan(12), 255);
        format[14] = 16; format[15] = 8; // Little-endian BGRX.
        await stream.WriteAsync(format, ct);
        var encodings = new byte[20]; encodings[0] = 2; encodings[3] = 4;
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(12), -223);
        BinaryPrimitives.WriteInt32BigEndian(encodings.AsSpan(16), -224);
        await stream.WriteAsync(encodings, ct);
        writer = WriteLoop(); RequestFrame(false);
    }

    // Apple's ARD account authentication: fixed 64-byte UTF-8 slots, encrypted
    // with the negotiated DH secret. Legacy ARD cryptography is contained inside
    // the authenticated SSH tunnel; it is never used as transport protection.
    public static byte[] CreateAppleResponse(ushort generator, byte[] modulus, byte[] remotePublic, string username, string password)
    {
        if (modulus.Length is < 16 or > 512 || modulus.Length != remotePublic.Length) throw new InvalidDataException("Invalid DH length.");
        var p = new BigInteger(modulus, true, true); var remote = new BigInteger(remotePublic, true, true);
        if (p.IsEven || p.GetBitLength() < 128 || generator < 2 || generator >= p - 1 || remote <= 1 || remote >= p - 1) throw new InvalidDataException("Invalid DH values.");
        var user = Encoding.UTF8.GetBytes(username); var secretText = Encoding.UTF8.GetBytes(password);
        var entropy = RandomNumberGenerator.GetBytes(32); var clear = RandomNumberGenerator.GetBytes(128);
        byte[] shared = [], aesKey = [];
        try
        {
            if (user.Length is 0 or > 63 || secretText.Length is 0 or > 63) throw new InvalidOperationException("Mac Screen Sharing requires the account name and password to each fit within 63 UTF-8 bytes.");
            var exponent = new BigInteger(entropy, true, true) % (p - 3) + 2;
            var localPublic = BigInteger.ModPow(generator, exponent, p);
            shared = FixedBytes(BigInteger.ModPow(remote, exponent, p), modulus.Length);
            aesKey = MD5.HashData(shared); // Required by Apple's ARD wire format.
            user.CopyTo(clear, 0); clear[user.Length] = 0;
            secretText.CopyTo(clear, 64); clear[64 + secretText.Length] = 0;
            using var aes = Aes.Create(); aes.Key = aesKey;
            var encrypted = aes.EncryptEcb(clear, PaddingMode.None);
            var result = new byte[128 + modulus.Length]; encrypted.CopyTo(result, 0);
            FixedBytes(localPublic, modulus.Length).CopyTo(result, 128);
            CryptographicOperations.ZeroMemory(encrypted);
            return result;
        }
        finally
        {
            foreach (var bytes in new[] { user, secretText, entropy, clear, shared, aesKey }) CryptographicOperations.ZeroMemory(bytes);
        }
    }
    private static byte[] FixedBytes(BigInteger value, int length)
    {
        var raw = value.ToByteArray(true, true); var result = new byte[length];
        if (raw.Length > length) throw new InvalidDataException("Invalid DH value size.");
        raw.CopyTo(result, length - raw.Length); CryptographicOperations.ZeroMemory(raw); return result;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var token = combined.Token;
        while (true)
        {
            switch ((await Read(1, token))[0])
            {
                case 0:
                    var header = await Read(3, token); var rectangles = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
                    var full = false;
                    for (var n = 0; n < rectangles; n++)
                    {
                        if (n >= 8192) throw new InvalidDataException("Too many login-screen rectangles.");
                        var rect = await Read(12, token);
                        var x = BinaryPrimitives.ReadUInt16BigEndian(rect); var y = BinaryPrimitives.ReadUInt16BigEndian(rect.AsSpan(2));
                        var w = BinaryPrimitives.ReadUInt16BigEndian(rect.AsSpan(4)); var h = BinaryPrimitives.ReadUInt16BigEndian(rect.AsSpan(6));
                        var encoding = BinaryPrimitives.ReadInt32BigEndian(rect.AsSpan(8));
                        if (encoding == -224) break;
                        if (encoding == -223) { Resize(w, h); full = true; continue; }
                        ValidateRectangle(x, y, w, h);
                        if (encoding == 0)
                            for (var row = 0; row < h; row++) await stream.ReadExactlyAsync(Pixels.AsMemory(((y + row) * Width + x) * 4, w * 4), token);
                        else if (encoding == 1)
                        {
                            var source = await Read(4, token); var sx = BinaryPrimitives.ReadUInt16BigEndian(source); var sy = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(2));
                            ValidateRectangle(sx, sy, w, h);
                            for (var i = 0; i < h; i++) { var row = y > sy ? h - 1 - i : i; Buffer.BlockCopy(Pixels, ((sy + row) * Width + sx) * 4, Pixels, ((y + row) * Width + x) * 4, w * 4); }
                        }
                        else throw new InvalidDataException("The Mac sent an unsupported login-screen encoding.");
                    }
                    Frame?.Invoke(); await Task.Delay(80, token); RequestFrame(!full); break;
                case 1:
                    var map = await Read(5, token); await Discard((uint)BinaryPrimitives.ReadUInt16BigEndian(map.AsSpan(3)) * 6, 65536 * 6, token); break;
                case 2: break;
                case 3:
                    var clipboard = await Read(7, token); await Discard(BinaryPrimitives.ReadUInt32BigEndian(clipboard.AsSpan(3)), 1024 * 1024, token); break;
                default: throw new InvalidDataException("The Mac sent an unsupported login-screen message.");
            }
        }
    }
    private void Resize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) throw new InvalidDataException("Mac login-screen dimensions exceed the supported limit.");
        CryptographicOperations.ZeroMemory(Pixels); Width = width; Height = height; Pixels = new byte[checked(width * height * 4)];
    }
    private void ValidateRectangle(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || width < 0 || height < 0 || (long)x + width > Width || (long)y + height > Height) throw new InvalidDataException("Invalid login-screen rectangle.");
    }
    public void Key(uint symbol, bool down)
    {
        var packet = new byte[8]; packet[0] = 4; packet[1] = down ? (byte)1 : (byte)0; BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), symbol); Enqueue(packet);
    }
    public void Pointer(int x, int y, byte buttons)
    {
        var packet = new byte[6]; packet[0] = 5; packet[1] = buttons;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)Math.Clamp(x, 0, Width - 1));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)Math.Clamp(y, 0, Height - 1)); Enqueue(packet);
    }
    private void RequestFrame(bool incremental)
    {
        var packet = new byte[10]; packet[0] = 3; packet[1] = incremental ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6), (ushort)Width); BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), (ushort)Height); Enqueue(packet);
    }
    private void Enqueue(byte[] packet)
    {
        if (stop.IsCancellationRequested || !outgoing.Writer.TryWrite(packet)) { stop.Cancel(); throw new IOException("Login-screen input stalled. Reconnect before continuing."); }
    }
    private async Task WriteLoop()
    {
        try { await foreach (var packet in outgoing.Reader.ReadAllAsync(stop.Token)) await stream.WriteAsync(packet, stop.Token); }
        catch { stop.Cancel(); }
    }
    private async Task<byte[]> Read(int count, CancellationToken ct) { var bytes = new byte[count]; await stream.ReadExactlyAsync(bytes, ct); return bytes; }
    private async Task DiscardText(CancellationToken ct) => await Discard(BinaryPrimitives.ReadUInt32BigEndian(await Read(4, ct)), 65536, ct);
    private async Task Discard(uint count, int limit, CancellationToken ct)
    {
        if (count > limit) throw new InvalidDataException("Mac login-screen message exceeds the supported limit.");
        var scratch = new byte[Math.Min(count, 4096)];
        while (count > 0) { var n = (int)Math.Min(count, (uint)scratch.Length); await stream.ReadExactlyAsync(scratch.AsMemory(0, n), ct); count -= (uint)n; }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); outgoing.Writer.TryComplete(); stream.Dispose();
        if (writer is not null) await writer;
        CryptographicOperations.ZeroMemory(Pixels); stop.Dispose();
    }
}
