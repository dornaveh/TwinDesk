using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TwinDesk;

public sealed class Settings
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 48150;
    public int AudioDevice { get; set; } = -1;
    public string AudioDeviceName { get; set; } = "Windows default output";
    public bool SwitchDisplays { get; set; }
    public List<MonitorRoute> Monitors { get; set; } = [];
    public static string DataDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TwinDesk");
    public static string PathFor(string name) => Path.Combine(DataDirectory, name);
    public static Settings Load() => File.Exists(PathFor("settings.json")) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathFor("settings.json"))) ?? new() : new();
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(PathFor("settings.json.tmp"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(PathFor("settings.json.tmp"), PathFor("settings.json"), true);
    }
}

public sealed class Identity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Token { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData));
    private record Secret(string Pfx, string Token);
    public Identity()
    {
        Directory.CreateDirectory(Settings.DataDirectory);
        var file = Settings.PathFor("identity.protected");
        if (File.Exists(file))
        {
            var secret = JsonSerializer.Deserialize<Secret>(Protect(File.ReadAllBytes(file), false))!;
            Certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(secret.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            Token = secret.Token;
        }
        else
        {
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest("CN=TwinDesk", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var pfx = generated.Export(X509ContentType.Pfx);
            // Windows Schannel requires a user key container for server TLS.
            // No PersistKeySet: the imported working key is removed on disposal.
            Certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            File.WriteAllBytes(file, Protect(JsonSerializer.SerializeToUtf8Bytes(new Secret(Convert.ToBase64String(pfx), Token)), true));
        }
    }
    public string PairingCode(string host, int port) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { version = 1, host, port, token = Token, fingerprint = Fingerprint }));
    public void Dispose() => Certificate.Dispose();

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    private static byte[] Protect(byte[] value, bool encrypt)
    {
        var input = new Blob { Length = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            Blob output;
            var ok = encrypt ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output) : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
