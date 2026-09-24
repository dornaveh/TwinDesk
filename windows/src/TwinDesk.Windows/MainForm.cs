using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TwinDesk;

public sealed class MainForm : Form
{
    private readonly Settings settings;
    private readonly Identity identity;
    private readonly ComboBox network = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 290 };
    private readonly ComboBox speakers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 380 };
    private readonly CheckBox displays = new() { Text = "Switch both monitor inputs with keyboard and mouse", AutoSize = true };
    private readonly Label connection = new() { Text = "Ready for setup", AutoSize = true, ForeColor = Color.FromArgb(113, 211, 196) };
    private readonly Label active = new() { Text = "Controlling this PC", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold) };
    private readonly Label audioStatus = new() { Text = "Mac audio will play independently of the selected computer.", AutoSize = true };
    private readonly TextBox log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly DataGridView monitorGrid = new() { Dock = DockStyle.Fill, AutoGenerateColumns = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, Height = 100 };
    private readonly Button start = Button("Start connection");
    private readonly Button swap = Button("Switch to Mac");
    private readonly NotifyIcon tray;
    private readonly SemaphoreSlim transition = new(1);
    private readonly System.Windows.Forms.Timer audioTimer = new() { Interval = 1000 };
    private readonly bool startInTray;
    private BridgeServer? server;
    private InputForwarder? input;
    private AudioOutput? player;
    private bool updatingOutputs;
    private sealed record OutputChoice(string? Id, string Name) { public override string ToString() => Name; }
    private List<MonitorDescription> detected = [];
    private bool quitting, switching, scanning;
    private bool audioVerified;
    private long previousAudio;
    private CancellationTokenSource? operation;

    public MainForm(bool startInTray = false)
    {
        if (startInTray) StartupTrace.Write("form construction started");
        this.startInTray = startInTray;
        settings = Settings.Load(); identity = new Identity();
        Text = "TwinDesk"; Size = new Size(900, 830); MinimumSize = new Size(780, 740);
        StartPosition = FormStartPosition.CenterScreen;
        if (startInTray) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; }
        Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(20, 26, 36); ForeColor = Color.FromArgb(232, 238, 246);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(26), ColumnCount = 1, RowCount = 13 };
        Controls.Add(layout);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Add(layout, new Label { Text = "T W I N D E S K   /   P R E V I E W", AutoSize = true, ForeColor = Color.FromArgb(113, 211, 196) }, 30);
        Add(layout, active, 54); Add(layout, connection, 30);
        Add(layout, new Label { Text = "Double middle-click / Ctrl + Alt + F12: switch     ·     Ctrl + Alt + F11: PC", AutoSize = true }, 36);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var recover = Button("Restore PC displays");
        actions.Controls.AddRange([start, swap, recover]); swap.Enabled = false;
        Add(layout, actions, 52);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var ip in adapter.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)) network.Items.Add(ip.Address.ToString());
        network.Text = settings.BindAddress;
        var pairing = Button("Copy Mac pairing code");
        var networkRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        networkRow.Controls.AddRange([new Label { Text = "PC address", AutoSize = true, Margin = new Padding(0, 9, 12, 0) }, network, pairing]);
        Add(layout, networkRow, 48);
        var audioRow = new FlowLayoutPanel { Dock = DockStyle.Fill };
        MigrateAudioSelection();
        try { RefreshAudioChoices(new WindowsAudioEnvironment().Snapshot()); }
        catch { RefreshAudioChoices(new OutputSnapshot(null, [])); }
        audioRow.Controls.AddRange([new Label { Text = "Speakers", AutoSize = true, Margin = new Padding(0, 9, 24, 0) }, speakers]);
        Add(layout, audioRow, 43); Add(layout, audioStatus, 32);
        displays.Checked = settings.SwitchDisplays;
        Add(layout, displays, 34);
        monitorGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Monitor", HeaderText = "Monitor", ReadOnly = true });
        monitorGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current input", ReadOnly = true });
        foreach (var name in new[] { "PC", "Mac" }) monitorGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = name, HeaderText = name + " cable", DataSource = new List<InputChoice> { new(15, "DisplayPort"), new(17, "HDMI 1"), new(18, "HDMI 2") }, ValueMember = "Value", DisplayMember = "Name" });
        Add(layout, monitorGrid, 112);
        Add(layout, new Label { Text = "Connect each monitor to the Mac as well as the PC. Enable input switching after pairing.\nAudio stays connected when you switch. The PC must remain awake for audio and file access.", AutoSize = true }, 52);
        var tools = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var scan = Button("Refresh monitors"); var guide = Button("Setup guide"); var storage = Button("Set up Ethernet & drives");
        tools.Controls.AddRange([scan, guide, storage]); Add(layout, tools, 48);
        layout.Controls.Add(log, 0, 12); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Style(this);
        speakers.ForeColor = Color.Black; speakers.BackColor = Color.White;
        speakers.DrawMode = DrawMode.OwnerDrawFixed; speakers.ItemHeight = 24;
        speakers.DrawItem += (_, e) => {
            e.DrawBackground();
            var index = e.Index >= 0 ? e.Index : speakers.SelectedIndex;
            if (index >= 0) TextRenderer.DrawText(e.Graphics, speakers.Items[index]?.ToString() ?? "", e.Font ?? Font, e.Bounds, Color.Black, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            e.DrawFocusRectangle();
        };
        speakers.SelectedIndexChanged += (_, _) => {
            if (updatingOutputs || speakers.SelectedItem is not OutputChoice selected) return;
            settings.AudioEndpointId = selected.Id;
            settings.AudioDeviceName = selected.Name;
            settings.AudioDevice = selected.Id is null ? -1 : 0; // Legacy field; stable ID is authoritative.
            try { settings.Save(); } catch (Exception e) { Note("Could not save audio selection: " + e.Message); }
            audioVerified = false;
            player?.Select(selected.Id);
        };
        speakers.DropDown += async (_, _) => {
            try { RefreshAudioChoices(await Task.Run(() => new WindowsAudioEnvironment().Snapshot())); }
            catch (Exception e) { Note("Could not refresh audio outputs: " + e.Message); }
        };
        connection.ForeColor = Color.FromArgb(113, 211, 196);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TwinDesk", null, (_, _) => OpenWindow());
        menu.Items.Add("Switch to Mac", null, (_, _) => RequestSwitch(Computer.Mac));
        menu.Items.Add("Return to PC", null, (_, _) => RequestSwitch(Computer.PC));
        menu.Items.Add("Quit TwinDesk", null, async (_, _) => { await Stop(); quitting = true; Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "TwinDesk · PC", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => OpenWindow();
        start.Click += async (_, _) => { try { if (server is null) await Start(); else await Stop(); } catch (Exception e) { Note(e.Message); } };
        swap.Click += (_, _) => RequestSwitch(null);
        recover.Click += async (_, _) => { RequestSwitch(Computer.PC); if (server is null) await RestoreDisplays(); };
        scan.Click += async (_, _) => { try { await Scan(); } catch (Exception e) { Note(e.Message); } };
        pairing.Click += (_, _) => { try { Save(); Clipboard.SetText(identity.PairingCode(settings.BindAddress, settings.Port)); Note("Pairing code copied. Paste it into TwinDesk on your Mac; treat it like a password."); } catch (Exception e) { Note(e.Message); } };
        guide.Click += (_, _) => { var path = Path.Combine(AppContext.BaseDirectory, "SETUP.html"); if (File.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); else Note("See README.md in the project folder."); };
        storage.Click += async (_, _) => {
            if (server is not null) { Note("Stop the connection before setting up the direct Ethernet link."); return; }
            var script = Path.Combine(AppContext.BaseDirectory, "setup", "Configure-DirectLink.ps1");
            if (!File.Exists(script)) { Note("The setup file is included in the packaged Windows build."); return; }
            try {
                var info = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-File"); info.ArgumentList.Add(script);
                using var process = System.Diagnostics.Process.Start(info)!;
                storage.Enabled = false; await process.WaitForExitAsync();
                if (process.ExitCode == 0) { network.Text = "192.168.77.1"; Save(); Note("Direct Ethernet and drive sharing configured. Set the Mac to 192.168.77.2, subnet mask 255.255.255.252, with no router or DNS."); }
                else Note("Network setup did not complete. The setup guide explains how to review it.");
            } catch (Exception e) { Note(e.Message); }
            finally { storage.Enabled = true; }
        };
        audioTimer.Tick += (_, _) => {
            var bytes = player?.BytesSubmitted ?? 0;
            var playing = bytes > previousAudio;
            audioStatus.Text = player is { Available: false } ? player.Status : playing ? "Mac audio is playing through the selected speakers." : server?.Connected == true ? "Audio connected · waiting for sound from the Mac." : "Mac audio will play independently of the selected computer.";
            if (playing && !audioVerified) { StartupTrace.Write($"Mac audio submitted to PC output; bytes={bytes}; queued={player?.QueuedMilliseconds:F1} ms; dropped={player?.DroppedBytes}"); audioVerified = true; }
            previousAudio = bytes;
        };
        audioTimer.Start();
        if (startInTray) StartupTrace.Write("form constructed");
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        Shown += async (_, _) => {
            if (startInTray) StartupTrace.Write("shown");
            if (startInTray)
            {
                Hide();
                // Never gate reconnection on DDC/CI: a monitor may take a long time
                // to answer (or never answer) after Windows starts.
                LoadSavedMonitors();
                StartupTrace.Write("saved monitors loaded");
                try { await Start(); } catch (Exception e) { Note("Automatic connection failed: " + e.Message); }
                StartupTrace.Write($"start returned; server={server is not null}");
            }
            else try { await Scan(); } catch (Exception e) { Note(e.Message); }
            Note("Speaker output: " + (speakers.SelectedItem?.ToString() ?? "Select an output"));
        };
        FormClosing += (_, e) => { if (!quitting && server is not null && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2000, "TwinDesk is still running", "Use the tray menu to quit and return control to the PC.", ToolTipIcon.Info); } else { input?.Dispose(); Microsoft.Win32.SystemEvents.SessionSwitch -= SessionChanged; Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged; tray.Dispose(); audioTimer.Dispose(); identity.Dispose(); } };
    }
    private record InputChoice(uint Value, string Name);
    private void MigrateAudioSelection()
    {
        if (settings.AudioEndpointId is not null || settings.AudioDevice == -1) return;
        try
        {
            var matches = AudioPlayer.Devices().Where(d => d.Id >= 0 && d.Name == settings.AudioDeviceName).ToArray();
            // Old waveOut indices can be recycled; only a single matching saved
            // device name is enough evidence to migrate to a stable endpoint ID.
            settings.AudioEndpointId = matches.Length == 1 ? AudioPlayer.EndpointId(matches[0].Id) : "unavailable-legacy-output";
        }
        catch { settings.AudioEndpointId = "unavailable-legacy-output"; }
        // A missing explicit output remains explicit; never silently choose the default.
    }
    private void RefreshAudioChoices(OutputSnapshot snapshot)
    {
        updatingOutputs = true;
        try
        {
            var choices = new List<OutputChoice> { new(null, "Windows default output") };
            choices.AddRange(snapshot.Devices.Select(d => new OutputChoice(d.Id, d.Name)));
            if (settings.AudioEndpointId is { } id && choices.All(d => d.Id != id))
                choices.Add(new(id, settings.AudioDeviceName));
            var old = speakers.Items.Cast<OutputChoice>().ToArray();
            if (!old.SequenceEqual(choices))
            {
                speakers.BeginUpdate();
                try { speakers.Items.Clear(); speakers.Items.AddRange(choices.Cast<object>().ToArray()); }
                finally { speakers.EndUpdate(); }
            }
            speakers.SelectedItem = speakers.Items.Cast<OutputChoice>().First(d => d.Id == settings.AudioEndpointId);
        }
        finally { updatingOutputs = false; }
    }
    private void OpenWindow() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
    private static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 35, Padding = new Padding(12, 5, 12, 5), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 10, 0) };
    private static void Add(TableLayoutPanel layout, Control control, int height) { var row = layout.Controls.Count; layout.Controls.Add(control, 0, row); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height)); }
    private static void Style(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            c.ForeColor = parent.ForeColor;
            if (c is TextBox or ComboBox or System.Windows.Forms.Button) c.BackColor = Color.FromArgb(37, 47, 63);
            if (c is DataGridView grid)
            {
                grid.BackgroundColor = Color.FromArgb(28, 36, 49); grid.BorderStyle = BorderStyle.None; grid.EnableHeadersVisualStyles = false;
                grid.DefaultCellStyle.BackColor = Color.FromArgb(37, 47, 63); grid.DefaultCellStyle.ForeColor = parent.ForeColor;
                grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(28, 36, 49); grid.ColumnHeadersDefaultCellStyle.ForeColor = parent.ForeColor;
            }
            Style(c);
        }
    }
    private async Task Scan()
    {
        if (scanning) return;
        scanning = true;
        await ddc.WaitAsync();
        try
        {
            var found = await Task.Run(Monitors.Scan);
            monitorGrid.EndEdit();
            var choices = monitorGrid.Rows.Cast<DataGridViewRow>().Select((row, index) =>
                new MonitorRoute(detected[index].Id, Convert.ToUInt32(row.Cells["PC"].Value), Convert.ToUInt32(row.Cells["Mac"].Value)))
                .ToDictionary(route => route.Id);
            var present = found.Select(m => m.Id).ToHashSet();
            // Preserve temporarily missing monitors and the user's cable choices.
            // A refresh must not silently remove a route or change the live session.
            detected = found.Concat(detected.Where(m => !present.Contains(m.Id)).Select(m => m with { Input = null })).ToList();
            monitorGrid.Rows.Clear();
            foreach (var m in detected)
            {
                var route = choices.GetValueOrDefault(m.Id) ?? settings.Monitors.FirstOrDefault(x => x.Id == m.Id);
                var pc = route?.PcInput ?? m.Input ?? 15; if (pc is not (15 or 17 or 18)) pc = 15;
                var status = !present.Contains(m.Id) ? "Not detected" : m.Input switch { 15 => "DisplayPort", 17 => "HDMI 1", 18 => "HDMI 2", _ => "Not reported" };
                monitorGrid.Rows.Add(m.Name, status, pc, route?.MacInput ?? 18u);
            }
            Note($"Found {found.Count} monitor(s). Refresh only reads settings; it does not change inputs or disconnect the Mac.");
        }
        finally { ddc.Release(); scanning = false; }
    }
    private void LoadSavedMonitors()
    {
        monitorGrid.Rows.Clear();
        detected = settings.Monitors.Select((route, index) =>
            new MonitorDescription(route.Id, $"Display {index + 1}", null, "")).ToList();
        foreach (var m in detected)
        {
            var route = settings.Monitors.First(x => x.Id == m.Id);
            monitorGrid.Rows.Add(m.Name, "Not checked", route.PcInput, route.MacInput);
        }
    }
    private void Save()
    {
        if (!IPAddress.TryParse(network.Text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork || address.Equals(IPAddress.Any)) throw new ArgumentException("Enter this PC's IPv4 address.");
        monitorGrid.EndEdit(); settings.BindAddress = address.ToString(); settings.SwitchDisplays = displays.Checked;
        var audioChoice = (OutputChoice)speakers.SelectedItem!;
        settings.AudioEndpointId = audioChoice.Id;
        settings.AudioDevice = audioChoice.Id is null ? -1 : 0;
        settings.AudioDeviceName = audioChoice.Name;
        settings.Monitors = monitorGrid.Rows.Cast<DataGridViewRow>().Select((r, i) => new MonitorRoute(detected[i].Id, Convert.ToUInt32(r.Cells["PC"].Value), Convert.ToUInt32(r.Cells["Mac"].Value))).ToList();
        if (settings.SwitchDisplays && (settings.Monitors.Count == 0 || settings.Monitors.Any(x => x.PcInput == x.MacInput))) throw new ArgumentException("Each monitor needs different PC and Mac inputs.");
        settings.Save();
    }
    private async Task Start()
    {
        Save();
        try
        {
            var audio = new AudioOutput(settings.AudioEndpointId);
            player = audio;
            audio.StatusChanged += message => UI(() => { if (player == audio) { audioVerified = false; Note(message); } });
            audio.DevicesChanged += snapshot => UI(() => { if (player == audio) RefreshAudioChoices(snapshot); });
            audio.Start();
            server = new BridgeServer(settings.BindAddress, settings.Port, identity);
            server.Status += message => UI(() => Note(message));
            server.ReturnToWindowsRequested += () => UI(() => RequestSwitch(Computer.PC));
            server.Audio += bytes => player?.Push(bytes);
            server.ConnectionChanged += connected => UI(() => {
                StartupTrace.Write($"Mac connection changed: connected={connected}");
                connection.Text = connected ? "Mac connected · encrypted" : "Waiting for your Mac";
                swap.Enabled = connected; SetThreadExecutionState(connected ? 0x80000001u : 0x80000000u);
                if (!connected) { audioVerified = false; operation?.Cancel(); input?.SetRemote(false); ShowTarget(false); player?.Flush(); if (settings.SwitchDisplays) _ = RestoreDisplays(); }
            });
            input = new InputForwarder { Forward = bytes => server?.SendInput(bytes) == true,
                Shortcut = target => BeginInvoke(() => RequestSwitch(target)),
                Overflow = () => BeginInvoke(() => { Note("Input connection stalled; returning to PC."); RequestSwitch(Computer.PC); server?.Disconnect(); }) };
            server.Start(); start.Text = "Stop connection"; connection.Text = "Waiting for your Mac";
            network.Enabled = displays.Enabled = monitorGrid.Enabled = false;
            Note($"Listening on {settings.BindAddress}:{settings.Port}. Paste the pairing code into the Mac app.");
        }
        catch { await Stop(); throw; }
    }
    private void RequestSwitch(Computer? target)
    {
        var destination = target ?? (input?.Remote == true ? Computer.PC : Computer.Mac);
        // Returning input to Windows is immediate, even while a DDC call is pending.
        if (destination == Computer.PC) { input?.SetRemote(false); operation?.Cancel(); ShowTarget(false); }
        _ = Switch(destination);
    }
    private async Task Switch(Computer target)
    {
        if (server is null) return;
        if (switching && target == Computer.Mac) return;
        await transition.WaitAsync(); switching = true;
        operation?.Dispose(); operation = new CancellationTokenSource(); var ct = operation.Token;
        try
        {
            if (target == Computer.Mac)
            {
                if (!server.Connected) throw new IOException("Connect the Mac before switching.");
                // Windows owns display switching in both directions. Arming Mac
                // HDMI-side recovery also sends DDC before our DP-side return;
                // the qualified individual tests used Windows commands alone.
                await server.CommandAsync("activate", ct);
                ct.ThrowIfCancellationRequested();
                if (settings.SwitchDisplays)
                {
                    await ddc.WaitAsync(ct);
                    try
                    {
                        var errors = await Task.Run(() => Monitors.Select(settings.Monitors, Computer.Mac));
                        if (errors.Count != 0) throw new IOException(string.Join("; ", errors));
                    }
                    finally { ddc.Release(); }
                }
                ct.ThrowIfCancellationRequested();
                if (!server.Connected) throw new IOException("Mac disconnected during switching.");
                input?.SetRemote(true); ShowTarget(true); Note("Controlling the Mac. Audio remains connected.");
            }
            else
            {
                input?.SetRemote(false); ShowTarget(false);
                if (server.Connected) { try { await server.CommandAsync("deactivate"); } catch (Exception e) { Note(e.Message); } }
                if (settings.SwitchDisplays) await RestoreDisplays();
                Note("Keyboard and mouse returned to Windows. Audio remains connected.");
            }
        }
        catch (Exception e)
        {
            input?.SetRemote(false); ShowTarget(false);
            Note(e is OperationCanceledException ? "Switch cancelled; returning to PC." : e.Message);
            if (server?.Connected == true) { try { await server.CommandAsync("deactivate"); } catch { server.Disconnect(); } }
            if (settings.SwitchDisplays) await RestoreDisplays();
        }
        finally { switching = false; transition.Release(); }
    }
    private readonly SemaphoreSlim ddc = new(1);
    private async Task RestoreDisplays()
    {
        await ddc.WaitAsync();
        try
        {
            var errors = await Task.Run(() => Monitors.Select(settings.Monitors, Computer.PC));
            if (errors.Count > 0) Note("PC display recovery: " + string.Join("; ", errors) + ". Use the monitor's input button if needed.");
            await Task.Delay(1200);
            var detected = await Task.Run(Monitors.Scan);
            var unconfirmed = settings.Monitors.Where(route =>
                detected.All(monitor => monitor.Id != route.Id || monitor.Input != route.PcInput)).ToList();
            if (unconfirmed.Count > 0)
                Note($"Windows picture not confirmed on {unconfirmed.Count} monitor(s). Select DisplayPort with the monitor's input button if needed.");
        }
        catch (Exception e) { Note(e.Message); }
        finally { ddc.Release(); }
    }
    private void ShowTarget(bool mac) { active.Text = mac ? "Controlling the Mac" : "Controlling this PC"; swap.Text = mac ? "Switch to PC" : "Switch to Mac"; tray.Text = mac ? "TwinDesk · Mac" : "TwinDesk · PC"; }
    private async Task Stop()
    {
        input?.SetRemote(false); operation?.Cancel();
        if (server is not null)
        {
            await transition.WaitAsync();
            try { if (server.Connected) { try { await server.CommandAsync("deactivate"); } catch { } } await server.DisposeAsync(); server = null; }
            finally { transition.Release(); }
        }
        input?.Dispose(); input = null;
        var audio = player; player = null; if (audio is not null) await audio.DisposeAsync();
        if (settings.SwitchDisplays) await RestoreDisplays();
        ShowTarget(false); swap.Enabled = false; start.Text = "Start connection"; connection.Text = "Connection stopped";
        network.Enabled = speakers.Enabled = displays.Enabled = monitorGrid.Enabled = true; SetThreadExecutionState(0x80000000u);
    }
    private void UI(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }
    private void SessionChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect)
            UI(() => RequestSwitch(Computer.PC));
    }
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend) UI(() => { RequestSwitch(Computer.PC); server?.Disconnect(); });
    }
    private void Note(string message) { StartupTrace.Write(message); if (log.Lines.Length > 100) log.Lines = log.Lines[^70..]; log.AppendText($"{DateTime.Now:HH:mm}  {message}{Environment.NewLine}"); }
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
}
