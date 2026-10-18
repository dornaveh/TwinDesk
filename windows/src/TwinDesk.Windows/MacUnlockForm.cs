namespace TwinDesk;

internal sealed class MacUnlockForm : Form
{
    private readonly Settings settings;
    private readonly Func<bool> connected;
    private readonly TextBox host = new() { Width = 480 };
    private readonly TextBox account = new() { Width = 480 };
    private readonly TextBox password = new() { Width = 480, UseSystemPasswordChar = true, MaxLength = 1024 };
    private readonly Button unlock = new() { Text = "Unlock Mac", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
    private readonly Label status = new() { AutoSize = true, MaximumSize = new Size(485, 0), Margin = new Padding(0, 12, 0, 0) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private CancellationTokenSource? operation;
    private bool waiting;
    private DateTime deadline;
    private MacStartupState waitingState;

    internal MacUnlockForm(Settings settings, Func<bool> connected)
    {
        this.settings = settings; this.connected = connected;
        Text = "TwinDesk · Unlock Mac"; ClientSize = new Size(550, 600);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(20, 26, 36); ForeColor = Color.FromArgb(232, 238, 246);
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(25), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Unlock your Mac after a restart", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
        layout.Controls.Add(new Label { Text = "Enter your Mac account password. It is used for this attempt only and is never saved.", MaximumSize = new Size(485, 0), AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
        AddField(layout, "Mac address", host); AddField(layout, "Mac account name", account); AddField(layout, "Mac password", password);
        host.Text = settings.MacHost; account.Text = settings.MacUsername;
        var buttons = new FlowLayoutPanel { AutoSize = true, Width = 485, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(14, 6, 14, 6) };
        close.Click += (_, _) => Close(); buttons.Controls.AddRange([unlock, close]); layout.Controls.Add(buttons);
        layout.Controls.Add(status);
        layout.Controls.Add(new Label { Text = "Disk unlock and desktop sign-in are separate on macOS. If the Mac still shows its sign-in screen after unlocking, sign in there before TwinDesk can receive keyboard and mouse input.", MaximumSize = new Size(485, 0), AutoSize = true, Margin = new Padding(0, 14, 0, 0) });
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
        Shown += (_, _) => { status.Text = connected() ? "Mac connected. Unlock is not needed." : "Ready to unlock the Mac over your direct connection."; password.Focus(); timer.Start(); };
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
        if (connected()) { password.Clear(); status.Text = "Mac connected. Unlock is not needed."; return; }
        var address = host.Text.Trim(); var username = account.Text.Trim();
        if (Uri.CheckHostName(address) == UriHostNameType.Unknown || address.Contains(':')) { status.Text = "Enter the Mac's IPv4 address or hostname."; return; }
        if (username.Length == 0 || username.Any(char.IsWhiteSpace) || username.Contains('\0')) { status.Text = "Enter the Mac's account name, such as the name shown before @ in Terminal."; return; }
        if (password.TextLength == 0) { status.Text = "Enter your Mac password."; password.Focus(); return; }
        var secret = password.Text; password.Clear();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(40)); operation = cancel;
        unlock.Enabled = host.Enabled = account.Enabled = false; waiting = false;
        status.Text = "Connecting securely to your Mac…";
        try
        {
            var result = await new MacStartupClient().UnlockAsync(address, username, secret, cancel.Token);
            if (IsDisposed) return;
            settings.MacHost = address; settings.MacUsername = username; settings.Save();
            status.Text = result.State switch {
                MacStartupState.DiskUnlocked => "Startup disk unlocked. Waiting for the Mac to finish starting…",
                MacStartupState.SignInRequired => "The disk is already unlocked. Sign in to the Mac desktop to start TwinDesk.",
                MacStartupState.DesktopReady => "You are signed in. TwinDesk is starting; waiting for its connection…",
                _ => "Another Mac account is signed in. Sign in to your TwinDesk account on the Mac."
            };
            waiting = result.State is MacStartupState.DiskUnlocked or MacStartupState.DesktopReady;
            waitingState = result.State;
            deadline = DateTime.UtcNow.AddSeconds(30);
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "Unlock was cancelled or timed out. Check the Mac before trying again."; }
        catch (Exception e) { if (!IsDisposed) status.Text = e is InvalidOperationException ? e.Message : "Could not reach the Mac. Check that it is powered on and connected, then try again."; }
        finally { secret = string.Empty; operation = null; if (!IsDisposed) unlock.Enabled = host.Enabled = account.Enabled = true; }
    }
}
