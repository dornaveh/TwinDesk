using System.Net.Sockets;
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
        using var connection = new MacSshConnection(host, username, password);
        if (!await connection.ConnectAsync(ct)) return new(MacStartupState.DiskUnlocked);
        var state = await connection.DesktopStateAsync(username, ct);
        if (state == MacStartupState.DesktopReady) await connection.StartTwinDeskAsync(ct);
        return new(state);
    }
    internal async Task<MacLoginConnection?> SignInAsync(string host, string username, string password, IProgress<string> progress, CancellationToken ct)
    {
        MacSshConnection? ssh = null;
        var deadline = DateTime.UtcNow.AddSeconds(100);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested(); ssh = new MacSshConnection(host, username, password);
                try
                {
                    if (await ssh.ConnectAsync(ct)) break;
                    progress.Report("Startup disk unlocked. Waiting for macOS to finish starting…");
                }
                catch (MacNotReadyException) when (DateTime.UtcNow < deadline) { progress.Report("Waiting for the Mac to finish starting…"); }
                ssh.Dispose(); ssh = null;
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("The Mac did not finish starting in time. Wait a moment and try again.");
                await Task.Delay(2500, ct);
            }
            var state = await ssh.DesktopStateAsync(username, ct);
            if (state == MacStartupState.DesktopReady) { await ssh.StartTwinDeskAsync(ct); ssh.Dispose(); ssh = null; return null; }
            if (state == MacStartupState.OtherUser) throw new InvalidOperationException("Another account owns the Mac desktop. Sign out of that account before using this sign-in flow.");
            progress.Report("Connecting to the Mac sign-in screen…");
            if (await ssh.CommandAsync("/usr/bin/nc -z -G 2 127.0.0.1 5900", ct) != 0)
                throw new InvalidOperationException("One-time setup is needed on the Mac: System Settings → General → Sharing → Screen Sharing. Turn it on and allow your account. Then try again. Leave the shared VNC-password option off.");
            var result = await MacLoginConnection.OpenAsync(ssh, username, password, ct);
            ssh = null; return result; // Ownership transferred to login connection.
        }
        finally { ssh?.Dispose(); password = string.Empty; }
    }
}

internal sealed class MacNotReadyException : IOException;

internal sealed class MacSshConnection : IDisposable
{
    private readonly KeyboardInteractiveAuthenticationMethod authentication;
    internal SshClient Client { get; }
    private string password;
    private bool confirmed, answered, rejectedHost;
    internal MacSshConnection(string host, string username, string password)
    {
        var keys = MacStartupClient.KnownKeys(host);
        if (keys.Count == 0) throw new InvalidOperationException("This Mac's identity has not been verified on this PC. Connect once with Windows OpenSSH and verify its host key before signing in with TwinDesk.");
        this.password = password; authentication = new KeyboardInteractiveAuthenticationMethod(username);
        authentication.AuthenticationPrompt += (_, e) => {
            if (MacStartup.ConfirmsDiskUnlock(e.Instruction)) confirmed = true;
            foreach (var prompt in e.Prompts)
            {
                if (answered || prompt.IsEchoed || !prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The Mac requested an additional authentication step. The password was not retried.");
                answered = true; prompt.Response = this.password;
            }
        };
        var info = new ConnectionInfo(host, 22, username, authentication) { Timeout = TimeSpan.FromSeconds(12), RetryAttempts = 0 };
        info.AuthenticationBanner += (_, e) => { if (MacStartup.ConfirmsDiskUnlock(e.BannerMessage)) confirmed = true; };
        Client = new SshClient(info);
        Client.HostKeyReceived += (_, e) => { e.CanTrust = keys.Trusts(e.HostKey); rejectedHost = !e.CanTrust; };
    }
    internal async Task<bool> ConnectAsync(CancellationToken ct)
    {
        try { await Client.ConnectAsync(ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (confirmed && !rejectedHost) { return false; }
        catch (Exception) when (rejectedHost) { throw new InvalidOperationException("The Mac's identity does not match its trusted host key. No password was sent. Verify the Mac's identity before trying again."); }
        catch (SshAuthenticationException) { throw new InvalidOperationException("The Mac did not accept the password, or its account requires another authentication step. Check the Mac account name and password."); }
        catch (Exception e) when (!answered && e is SocketException or SshConnectionException or SshOperationTimeoutException) { throw new MacNotReadyException(); }
        catch (SshConnectionException) { throw new InvalidOperationException("The connection closed without confirming unlock. The password was not retried."); }
        finally { password = string.Empty; }
    }
    internal async Task<MacStartupState> DesktopStateAsync(string username, CancellationToken ct)
    {
        using var command = Client.CreateCommand("/usr/bin/stat -f %Su /dev/console"); command.CommandTimeout = TimeSpan.FromSeconds(8);
        await command.ExecuteAsync(ct).ConfigureAwait(false);
        if (command.ExitStatus != 0) throw new InvalidOperationException("The Mac's desktop sign-in status could not be checked.");
        return MacStartup.ConsoleState(command.Result, username);
    }
    internal async Task<int> CommandAsync(string text, CancellationToken ct)
    {
        using var command = Client.CreateCommand(text); command.CommandTimeout = TimeSpan.FromSeconds(8);
        await command.ExecuteAsync(ct).ConfigureAwait(false); return command.ExitStatus ?? -1;
    }
    internal async Task StartTwinDeskAsync(CancellationToken ct)
    {
        if (await CommandAsync("/usr/bin/pgrep -f '^/Applications/TwinDesk.app/Contents/MacOS/TwinDesk$' >/dev/null || /usr/bin/open -gj /Applications/TwinDesk.app --args --startup", ct) != 0)
            throw new InvalidOperationException("The Mac is signed in, but TwinDesk could not be opened. Check its installation in Applications.");
    }
    public void Dispose() { password = string.Empty; Client.Dispose(); authentication.Dispose(); }
}

internal sealed class MacLoginConnection : IAsyncDisposable
{
    private readonly MacSshConnection ssh;
    private readonly ForwardedPortLocal tunnel;
    private readonly TcpClient tcp;
    internal LoginScreenProtocol Screen { get; }
    internal string Username { get; }
    private MacLoginConnection(MacSshConnection ssh, ForwardedPortLocal tunnel, TcpClient tcp, LoginScreenProtocol screen, string username)
    { this.ssh = ssh; this.tunnel = tunnel; this.tcp = tcp; Screen = screen; Username = username; }
    internal static async Task<MacLoginConnection> OpenAsync(MacSshConnection ssh, string username, string password, CancellationToken ct)
    {
        var tunnel = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", 5900);
        var tcp = new TcpClient(); LoginScreenProtocol? screen = null;
        try
        {
            ssh.Client.AddForwardedPort(tunnel); await Task.Run(tunnel.Start, ct);
            await tcp.ConnectAsync("127.0.0.1", (int)tunnel.BoundPort, ct);
            screen = new LoginScreenProtocol(tcp.GetStream());
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct); handshake.CancelAfter(TimeSpan.FromSeconds(20));
            await screen.InitializeAsync(username, password, handshake.Token);
            return new(ssh, tunnel, tcp, screen, username);
        }
        catch { if (screen is not null) await screen.DisposeAsync(); tcp.Dispose(); tunnel.Dispose(); throw; }
        finally { password = string.Empty; }
    }
    internal Task<MacStartupState> DesktopStateAsync(CancellationToken ct) => ssh.DesktopStateAsync(Username, ct);
    internal Task StartTwinDeskAsync(CancellationToken ct) => ssh.StartTwinDeskAsync(ct);
    public async ValueTask DisposeAsync()
    {
        await Screen.DisposeAsync(); tcp.Dispose();
        await Task.Run(() => { tunnel.Dispose(); ssh.Dispose(); });
    }
}
