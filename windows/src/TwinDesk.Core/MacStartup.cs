using System.Security.Cryptography;
using System.Text;

namespace TwinDesk;

public enum MacStartupState { DiskUnlocked, SignInRequired, DesktopReady, OtherUser }

public static class MacStartup
{
    public static bool ConfirmsDiskUnlock(string? text) => text?.Split('\n')
        .Any(line => line.Trim().Equals("System successfully unlocked.", StringComparison.Ordinal)) == true;

    public static MacStartupState ConsoleState(string owner, string username)
    {
        owner = owner.Trim();
        if (owner.Length == 0 || owner is "root" or "loginwindow" or "_mbsetupuser") return MacStartupState.SignInRequired;
        return owner.Equals(username, StringComparison.Ordinal) ? MacStartupState.DesktopReady : MacStartupState.OtherUser;
    }
}

// Reuse the host identity already approved through Windows OpenSSH. Never accept
// a changed or unknown host just because it answers at the saved IP address.
public sealed class MacHostKeys
{
    private readonly List<byte[]> trusted = [];
    private readonly List<byte[]> revoked = [];
    public int Count => trusted.Count;

    public static MacHostKeys Parse(IEnumerable<string> lines, string host, int port = 22)
    {
        var result = new MacHostKeys();
        var lookup = port == 22 ? host : $"[{host}]:{port}";
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0].StartsWith('#')) continue;
            var offset = parts[0].StartsWith('@') ? 1 : 0;
            if (parts.Length < offset + 3 || (offset == 1 && parts[0] != "@revoked")) continue;
            if (!parts[offset].Split(',').Any(name => Matches(name, lookup))) continue;
            try
            {
                var key = Convert.FromBase64String(parts[offset + 2]);
                if (key.Length is < 16 or > 65536) continue;
                (offset == 1 ? result.revoked : result.trusted).Add(key);
            }
            catch (FormatException) { }
        }
        return result;
    }

    public bool Trusts(byte[] key) => !revoked.Any(saved => CryptographicOperations.FixedTimeEquals(saved, key))
        && trusted.Any(saved => CryptographicOperations.FixedTimeEquals(saved, key));

    private static bool Matches(string pattern, string host)
    {
        if (!pattern.StartsWith('|')) return pattern.Equals(host, StringComparison.OrdinalIgnoreCase);
        var fields = pattern.Split('|');
        if (fields.Length != 4 || fields[1] != "1") return false;
        try
        {
            var salt = Convert.FromBase64String(fields[2]);
            var expected = Convert.FromBase64String(fields[3]);
            return CryptographicOperations.FixedTimeEquals(HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(host)), expected);
        }
        catch (FormatException) { return false; }
    }
}
