using Renci.SshNet;
using Renci.SshNet.Common;

namespace TwinDesk;

internal sealed record MacStartupResult(MacStartupState State);

internal sealed class MacStartupClient
{
    internal static MacHostKeys KnownKeys(string host)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
        return MacHostKeys.Parse(File.Exists(file) ? File.ReadLines(file) : [], host);
    }

    internal async Task<MacStartupResult> UnlockAsync(string host, string username, string password, CancellationToken ct)
    {
        var keys = KnownKeys(host);
        if (keys.Count == 0) throw new InvalidOperationException("This Mac's identity has not been verified on this PC. Connect once with Windows OpenSSH and verify its host key before unlocking in TwinDesk.");
        using var authentication = new KeyboardInteractiveAuthenticationMethod(username);
        var confirmed = false;
        var answered = false;
        var rejectedHost = false;
        authentication.AuthenticationPrompt += (_, e) => {
            if (MacStartup.ConfirmsDiskUnlock(e.Instruction)) confirmed = true;
            foreach (var prompt in e.Prompts)
            {
                // Apple uses one hidden password prompt. Do not automatically
                // retry a rejected password or answer unrelated challenges.
                if (answered || prompt.IsEchoed || !prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Mac requested an additional sign-in step. Complete sign-in on the Mac.");
                answered = true;
                prompt.Response = password;
            }
        };
        var info = new ConnectionInfo(host, 22, username, authentication) { Timeout = TimeSpan.FromSeconds(15), RetryAttempts = 0 };
        info.AuthenticationBanner += (_, e) => { if (MacStartup.ConfirmsDiskUnlock(e.BannerMessage)) confirmed = true; };
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) => { e.CanTrust = keys.Trusts(e.HostKey); rejectedHost = !e.CanTrust; };
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            using var status = client.CreateCommand("/usr/bin/stat -f %Su /dev/console");
            status.CommandTimeout = TimeSpan.FromSeconds(8);
            await status.ExecuteAsync(ct).ConfigureAwait(false);
            if (status.ExitStatus != 0) throw new InvalidOperationException("The Mac authenticated, but its sign-in status could not be checked.");
            var state = MacStartup.ConsoleState(status.Result, username);
            if (state == MacStartupState.DesktopReady)
            {
                using var start = client.CreateCommand("/usr/bin/pgrep -f '^/Applications/TwinDesk.app/Contents/MacOS/TwinDesk$' >/dev/null || /usr/bin/open -gj /Applications/TwinDesk.app --args --startup");
                start.CommandTimeout = TimeSpan.FromSeconds(8);
                await start.ExecuteAsync(ct).ConfigureAwait(false);
                if (start.ExitStatus != 0) throw new InvalidOperationException("The Mac is signed in, but TwinDesk could not be opened. Check that it is installed in Applications.");
            }
            return new(state);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (confirmed && !rejectedHost)
        {
            // FileVault confirms unlock during PAM authentication, then tears
            // down SSH before opening a session. A bare disconnect is NOT success.
            return new(MacStartupState.DiskUnlocked);
        }
        catch (Exception) when (rejectedHost) { throw new InvalidOperationException("The Mac's identity does not match the trusted host key. No password was sent. Verify the Mac's identity before trying again."); }
        catch (SshAuthenticationException) { throw new InvalidOperationException("The Mac did not accept the password, or its account requires another sign-in step. Check the Mac account name and password."); }
        catch (SshConnectionException) { throw new InvalidOperationException("The Mac connection closed without confirming unlock. Try again once it is reachable; unlock was not confirmed."); }
        finally { password = string.Empty; }
    }
}
