namespace TwinDesk;

internal sealed class MacUnlockForm : Form
{
    private readonly Settings settings;
    private readonly Func<bool> connected;
    private readonly Action<string>? trace;
    private readonly TextBox host = new() { Width = 480 };
    private readonly TextBox account = new() { Width = 480 };
    private readonly TextBox password = new() { Width = 480, UseSystemPasswordChar = true, MaxLength = 1024 };
    private readonly Button unlock = new() { Text = "Sign in to Mac", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(485, 0), Margin = new Padding(0, 12, 0, 0) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private CancellationTokenSource? operation;
    private bool waiting;
    private DateTime deadline;
    private MacStartupState waitingState;

    internal MacUnlockForm(Settings settings, Func<bool> connected, Action<string>? trace = null)
    {
        this.settings = settings; this.connected = connected; this.trace = trace;
        Text = "TwinDesk · Sign in to Mac"; ClientSize = new Size(550, 600);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(20, 26, 36); ForeColor = Color.FromArgb(232, 238, 246);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(25), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Sign in to your Mac after a restart", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
        layout.Controls.Add(new Label { Text = "Enter your Mac account password. It is used for this attempt only and is never saved.", MaximumSize = new Size(485, 0), AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
        AddField(layout, "Mac address", host); AddField(layout, "Mac account name", account); AddField(layout, "Mac password", password);
        host.Text = settings.MacHost; account.Text = settings.MacUsername;
        var buttons = new FlowLayoutPanel { AutoSize = true, Width = 485, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
        close.Click += (_, _) => Close(); buttons.Controls.AddRange([unlock, close]); layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        layout.Controls.Add(new Label { Text = "TwinDesk unlocks the startup disk, then opens your Mac's sign-in screen here on the PC. Use this PC's keyboard and mouse to sign in. One-time Mac setup: enable Screen Sharing for your account in System Settings → General → Sharing.", MaximumSize = new Size(485, 0), AutoSize = true, Margin = new Padding(0, 14, 0, 0) });
        unlock.Click += async (_, _) => await Unlock(); AcceptButton = unlock; CancelButton = close;
        timer.Tick += (_, _) => {
            if (connected()) { waiting = false; status.Text = "Mac connected. TwinDesk is ready to switch computers."; }
            else if (waiting && DateTime.UtcNow >= deadline) {
                waiting = false;
                status.Text = waitingState == MacStartupState.DesktopReady
                    ? "The Mac is signed in, but TwinDesk has not connected yet. Check its connection on the Mac."
                    : "The startup disk is unlocked. If the Mac shows its sign-in screen, sign in there; TwinDesk will reconnect afterwards.";
            }
        };
        Shown += (_, _) => { status.Text = connected() ? "Mac connected. Sign-in is not needed." : "Ready to sign in to your Mac over the direct connection."; password.Focus(); timer.Start(); };
        FormClosing += (_, _) => { operation?.Cancel(); password.Clear(); timer.Stop(); };
        FormClosed += (_, _) => timer.Dispose();
    }

    private static void AddField(Control layout, string label, TextBox field)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 7, 0, 3) });
        field.Margin = new Padding(0); layout.Controls.Add(field);
    }

    private async Task Unlock()
    {
        if (operation is not null) return;
        if (connected()) { password.Clear(); status.Text = "Mac connected. Sign-in is not needed."; return; }
        var address = host.Text.Trim(); var username = account.Text.Trim();
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown || address.Contains(':')) { status.Text = "Enter the Mac's IPv4 address or hostname."; return; }
        if (username.Length == 0 || username.Any(char.IsWhiteSpace) || username.Contains('\0')) { status.Text = "Enter the Mac's account name, such as the name shown before @ in Terminal."; return; }
        if (password.TextLength == 0) { status.Text = "Enter your Mac password."; password.Focus(); return; }
        var secret = password.Text; password.Clear();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(130)); operation = cancel;
        unlock.Enabled = host.Enabled = account.Enabled = false; waiting = false;
        status.Text = "Connecting securely to your Mac…";
        var stage = "Connecting securely to your Mac";
        try
        {
            var progress = new Progress<string>(text => { stage = text; trace?.Invoke("Mac sign-in stage: " + text); if (!IsDisposed) status.Text = text; });
            await using var login = await Task.Run(() => new MacStartupClient().SignInAsync(address, username, secret, progress, cancel.Token));
            secret = string.Empty;
            if (IsDisposed) return;
            settings.MacHost = address; settings.MacUsername = username; settings.Save();
            if (login is not null)
            {
                // The interactive view has its own lifetime. Do not expire it
                // just because the initial unlock's boot timeout has elapsed.
                cancel.CancelAfter(Timeout.InfiniteTimeSpan);
                using var view = new MacLoginForm(login, connected);
                view.ShowDialog(this);
                await view.FinishAsync();
                if (IsDisposed) return;
                status.Text = connected() ? "Mac connected. You can now switch computers." : "The Mac sign-in view was closed. You can try again when ready.";
                waiting = false;
            }
            else { status.Text = "The Mac is signed in. Waiting for TwinDesk to reconnect…"; waiting = true; }
            waitingState = MacStartupState.DesktopReady;
            deadline = DateTime.UtcNow.AddSeconds(30);
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Sign-in was cancelled or timed out. Wait a moment before trying again."; }
        catch (Exception e)
        {
            // Exception messages can contain server-supplied text. Record only
            // exception types and code locations, never credentials or messages.
            var types = new List<string>();
            for (Exception? cause = e; cause is not null && types.Count < 6; cause = cause.InnerException) types.Add(cause.GetType().Name);
            trace?.Invoke($"Mac sign-in failed at {stage}: {string.Join(" / ", types)}; {e.StackTrace}");
            if (!IsDisposed) status.Text = e switch {
                InvalidOperationException => e.Message,
                InvalidDataException => "The Mac replied with an unsupported sign-in screen format. TwinDesk recorded the failing step for diagnosis.",
                EndOfStreamException => "The Mac closed the sign-in screen connection. TwinDesk recorded the failing step for diagnosis.",
                _ => $"Mac sign-in failed while {stage.TrimEnd('…').ToLowerInvariant()}. TwinDesk recorded the failing step for diagnosis."
            };
        }
        finally { secret = string.Empty; operation = null; if (!IsDisposed) unlock.Enabled = host.Enabled = account.Enabled = true; }
    }
}
