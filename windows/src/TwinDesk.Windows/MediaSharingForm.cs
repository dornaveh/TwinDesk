namespace TwinDesk;

// Device permission is remembered; capture still requires current Mac demand.
public sealed class MediaSharingForm : Form
{
    private readonly Settings settings;
    private readonly ComboBox cameras = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 535 };
    private readonly ComboBox modes = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 535 };
    private readonly ComboBox microphones = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 535 };
    private readonly CheckBox shareCamera = new() { Text = "Allow Mac apps to use this camera", AutoSize = true };
    private readonly CheckBox shareMic = new() { Text = "Allow Mac apps to use this microphone", AutoSize = true };
    private readonly Label cameraStatus = new() { Text = "Camera is off.", AutoSize = true, MaximumSize = new Size(550, 0) };
    private readonly Label micStatus = new() { Text = "Microphone is off.", AutoSize = true, MaximumSize = new Size(550, 0) };
    private readonly SemaphoreSlim changes = new(1);
    private BridgeServer? server;
    private WebcamCapture? camera;
    private MicrophoneCapture? microphone;
    private bool enumerating;
    private bool restoring;
    private bool restoringDevices;
    private readonly System.Windows.Forms.Timer deviceRestoreTimer = new() { Interval = 3000 };
    private int cameraGeneration, micGeneration;

    public MediaSharingForm(Settings settings)
    {
        this.settings = settings;
        Text = "TwinDesk · Camera & microphone";
        ClientSize = new Size(620, 550); MinimumSize = Size;
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Use your PC camera and microphone in calls on your Mac.", AutoSize = true, Margin = new Padding(0, 0, 0, 15) });
        modes.Items.AddRange([VideoMode.Hd, VideoMode.FullHd]);
        modes.SelectedItem = VideoMode.FromId(settings.CameraMode);
        modes.SelectedIndexChanged += (_, _) => {
            if (modes.SelectedItem is not VideoMode mode) return;
            settings.CameraMode = mode.Id; SaveDeviceChoices(); server?.SetCameraMode(mode);
        };
        layout.Controls.AddRange([cameras, modes, shareCamera, cameraStatus,
            new Label { Text = "Microphone", AutoSize = true, Margin = new Padding(0, 20, 0, 5) }, microphones, shareMic, micStatus]);
        var refresh = new Button { Text = "Refresh devices", AutoSize = true, Margin = new Padding(0, 20, 0, 12) };
        layout.Controls.Add(refresh);
        var pair = new Button { Text = "Copy Mac media setup code", AutoSize = true };
        layout.Controls.Add(pair);
        pair.Click += (_, _) => {
            try {
                Clipboard.SetText(server?.CreateMediaSetupCode() ?? throw new IOException("Start the TwinDesk connection first."));
                cameraStatus.Text = "Paste this code into TwinDesk Calls on the Mac. Keep it private; it authorizes media sharing.";
            } catch (Exception e) { cameraStatus.Text = e.Message; }
        };
        layout.Controls.Add(new Label { Text = "On the Mac, select OBS Virtual Camera and TwinDesk Microphone in your calling app.\nStart/stop the camera from the Mac menu. The microphone runs only while a Mac app uses it. Your device choices and permissions are remembered.", AutoSize = true, MaximumSize = new Size(550, 0) });
        microphones.SelectedIndexChanged += (_, _) => {
            if (!restoring && microphones.SelectedItem is MicrophoneDevice device) RememberMicrophone(device);
        };
        shareCamera.CheckedChanged += (_, _) => {
            if (restoring) return;
            if (shareCamera.Checked && cameras.SelectedItem is not WebcamDevice) { shareCamera.Checked = false; cameraStatus.Text = "Choose a camera first."; return; }
            camera?.Cancel(); Interlocked.Increment(ref cameraGeneration);
            server?.SetCameraSharing(shareCamera.Checked);
            cameras.Enabled = !shareCamera.Checked;
            modes.Enabled = !shareCamera.Checked;
            settings.CameraDeviceId = (cameras.SelectedItem as WebcamDevice)?.Id ?? "";
            settings.CameraAllowed = shareCamera.Checked;
            SaveDeviceChoices(); _ = Reconcile();
            if (!shareCamera.Checked) cameraStatus.Text = "Camera is off.";
        };
        shareMic.CheckedChanged += (_, _) => {
            if (restoring) return;
            if (shareMic.Checked && microphones.SelectedItem is not MicrophoneDevice) { shareMic.Checked = false; micStatus.Text = "Choose a microphone first."; return; }
            microphone?.Cancel(); Interlocked.Increment(ref micGeneration);
            server?.SetMicrophoneSharing(shareMic.Checked);
            microphones.Enabled = !shareMic.Checked;
            if (microphones.SelectedItem is MicrophoneDevice device) RememberMicrophone(device);
            settings.MicrophoneAllowed = shareMic.Checked;
            SaveDeviceChoices(); _ = Reconcile();
            if (!shareMic.Checked) micStatus.Text = "Microphone is off.";
        };
        refresh.Click += async (_, _) => await RestorePermissions(server);
        Shown += async (_, _) => { if (cameras.Items.Count == 0 && microphones.Items.Count == 0) await RestorePermissions(server); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        deviceRestoreTimer.Tick += async (_, _) => {
            if (server is not null && (settings.MicrophoneAllowed && !shareMic.Checked || settings.CameraAllowed && !shareCamera.Checked))
                await RestorePermissions(server);
        };
        Disposed += (_, _) => deviceRestoreTimer.Dispose();
        deviceRestoreTimer.Start();
        _ = Handle; // Background connection callbacks can post even while this window is hidden.
        UpdateAvailability();
    }
    private void SaveDeviceChoices()
    {
        try { settings.Save(); } catch (Exception e) { cameraStatus.Text = "Could not save device selection: " + e.Message; }
    }
    private void RememberMicrophone(MicrophoneDevice device)
    {
        if (settings.MicrophoneDeviceId == device.Id && settings.MicrophoneDeviceName == device.Name && settings.MicrophoneContainerId == device.ContainerId) return;
        settings.MicrophoneDeviceId = device.Id;
        settings.MicrophoneDeviceName = device.Name;
        settings.MicrophoneContainerId = device.ContainerId;
        SaveDeviceChoices();
    }
    private async Task RefreshDevices()
    {
        if (enumerating) return;
        enumerating = true;
        try
        {
            // Refresh each inactive medium independently: an enabled camera
            // must not prevent a late USB microphone from being restored.
            if (!shareCamera.Checked)
            {
                try {
                    var devices = await WebcamCapture.DevicesAsync();
                    if (IsDisposed) return;
                    restoring = true;
                    try {
                        cameras.Items.Clear(); cameras.Items.AddRange(devices.Cast<object>().ToArray());
                        cameras.SelectedItem = devices.FirstOrDefault(d => d.Id == settings.CameraDeviceId);
                    } finally { restoring = false; }
                    cameraStatus.Text = devices.Count == 0 ? "No cameras found. Connect a camera and refresh." : "Choose a camera, then enable sharing.";
                } catch (Exception e) { cameraStatus.Text = "Could not list cameras: " + e.Message; }
            }
            if (!shareMic.Checked)
            {
                // Migrate old settings while Windows still knows the saved
                // endpoint, even if that endpoint is currently inactive.
                if (string.IsNullOrEmpty(settings.MicrophoneDeviceName) && MicrophoneCapture.Describe(settings.MicrophoneDeviceId) is MicrophoneDevice saved)
                    RememberMicrophone(saved);
                var inputs = MicrophoneCapture.Devices();
                var selected = MicrophoneSelection.Resolve(inputs, settings.MicrophoneDeviceId, settings.MicrophoneDeviceName, settings.MicrophoneContainerId);
                restoring = true;
                try {
                    microphones.Items.Clear(); microphones.Items.AddRange(inputs.Cast<object>().ToArray());
                    microphones.SelectedItem = selected;
                } finally { restoring = false; }
                if (selected is not null) RememberMicrophone(selected);
                micStatus.Text = settings.MicrophoneAllowed && selected is null
                    ? "Waiting for the saved microphone. Sharing will resume when it is available."
                    : inputs.Count == 0 ? "No microphones found. Connect a microphone and refresh." : "Choose a microphone, then enable sharing.";
            }
        }
        catch (Exception e) { cameraStatus.Text = "Could not list devices: " + e.Message; }
        finally { enumerating = false; UpdateAvailability(); }
    }
    public async Task AttachAsync(BridgeServer? next)
    {
        CancelCapture();
        restoring = true;
        try { shareCamera.Checked = shareMic.Checked = false; }
        finally { restoring = false; }
        if (server is not null)
        {
            server.CameraConnectionChanged -= CameraChanged;
            server.MicrophoneConnectionChanged -= MicChanged;
            server.ConnectionChanged -= MainChanged;
        }
        server = next;
        if (server is not null)
        {
            server.SetCameraMode(VideoMode.FromId(settings.CameraMode));
            server.CameraConnectionChanged += CameraChanged;
            server.MicrophoneConnectionChanged += MicChanged;
            server.ConnectionChanged += MainChanged;
        }
        await Reconcile();
        if (next is not null) _ = RestorePermissions(next);
    }
    private async Task RestorePermissions(BridgeServer? expected)
    {
        if (restoringDevices || IsDisposed) return;
        restoringDevices = true;
        try
        {
            await RefreshDevices();
            if (IsDisposed || server != expected) return;
            var previousCamera = shareCamera.Checked;
            var previousMic = shareMic.Checked;
            restoring = true;
            try
            {
                shareCamera.Checked = settings.CameraAllowed && cameras.SelectedItem is WebcamDevice;
                shareMic.Checked = settings.MicrophoneAllowed && microphones.SelectedItem is MicrophoneDevice;
            }
            finally { restoring = false; }
            cameras.Enabled = modes.Enabled = !shareCamera.Checked;
            microphones.Enabled = !shareMic.Checked;
            if (previousCamera != shareCamera.Checked) server?.SetCameraSharing(shareCamera.Checked);
            if (previousMic != shareMic.Checked) server?.SetMicrophoneSharing(shareMic.Checked);
            await Reconcile();
        }
        catch (Exception e) { if (!IsDisposed) cameraStatus.Text = "Could not restore sharing: " + e.Message; }
        finally { restoringDevices = false; }
    }
    private void SuspendMicrophone(string error)
    {
        // Hardware/capture failure is temporary; only the user's checkbox
        // action may revoke the saved sharing preference or device choice.
        restoring = true;
        try { shareMic.Checked = false; }
        finally { restoring = false; }
        microphone?.Cancel(); Interlocked.Increment(ref micGeneration);
        server?.SetMicrophoneSharing(false);
        microphones.Enabled = true;
        micStatus.Text = error + " Retrying the saved microphone automatically.";
    }
    public void CancelCapture()
    {
        camera?.Cancel(); microphone?.Cancel();
        Interlocked.Increment(ref cameraGeneration); Interlocked.Increment(ref micGeneration);
    }
    private void CameraChanged(bool connected)
    {
        if (!connected) { camera?.Cancel(); Interlocked.Increment(ref cameraGeneration); }
        Post(() => _ = Reconcile());
    }
    private void MicChanged(bool connected)
    {
        if (!connected) { microphone?.Cancel(); Interlocked.Increment(ref micGeneration); }
        Post(() => _ = Reconcile());
    }
    private void MainChanged(bool connected)
    {
        if (!connected) CancelCapture();
        Post(() => _ = Reconcile());
    }
    private void Post(Action action)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) action(); }); }
        catch (InvalidOperationException) { /* The window is closing. */ }
    }
    private void UpdateAvailability()
    {
        shareCamera.Enabled = shareCamera.Checked || server?.Connected == true && !enumerating;
        shareMic.Enabled = shareMic.Checked || server?.Connected == true && !enumerating;
    }
    private int runningCameraGeneration = -1, runningMicGeneration = -1;
    private async Task Reconcile()
    {
        if (IsDisposed) return;
        await changes.WaitAsync();
        try
        {
            if (IsDisposed) return;
            UpdateAvailability();
            var cameraWanted = shareCamera.Checked && server?.CameraConnected == true;
            var micWanted = shareMic.Checked && server?.MicrophoneConnected == true;
            if (camera is not null && (!cameraWanted || runningCameraGeneration != Volatile.Read(ref cameraGeneration)))
            {
                var old = camera; camera = null; await old.DisposeAsync();
            }
            if (microphone is not null && (!micWanted || runningMicGeneration != Volatile.Read(ref micGeneration)))
            {
                var old = microphone; microphone = null; await old.DisposeAsync();
            }
            if (cameraWanted && shareCamera.Checked && camera is null && server?.CameraConnected == true)
            {
                try
                {
                    var device = cameras.SelectedItem as WebcamDevice ?? throw new IOException("Choose a camera first.");
                    runningCameraGeneration = Volatile.Read(ref cameraGeneration);
                    var current = new WebcamCapture(server.MediaSender(false), error => Post(() => { shareCamera.Checked = false; cameraStatus.Text = error; }), VideoMode.FromId(settings.CameraMode));
                    camera = current;
                    cameraStatus.Text = "Starting camera…";
                    await current.StartAsync(device.Id);
                    if (runningCameraGeneration != Volatile.Read(ref cameraGeneration) || !shareCamera.Checked || server?.CameraConnected != true)
                    { camera = null; await current.DisposeAsync(); }
                    else cameraStatus.Text = $"Camera active · {current.Width} × {current.Height} · configured for {current.FramesPerSecond:0.##} fps";
                }
                catch (OperationCanceledException) when (runningCameraGeneration != Volatile.Read(ref cameraGeneration) || !shareCamera.Checked || server?.CameraConnected != true)
                {
                    // Closing a Mac preview during startup withdraws demand;
                    // it must not revoke the user's permission to use the device.
                    if (camera is not null) { var old = camera; camera = null; await old.DisposeAsync(); }
                }
                catch (Exception e)
                {
                    shareCamera.Checked = false;
                    if (camera is not null) { var old = camera; camera = null; await old.DisposeAsync(); }
                    cameraStatus.Text = "Camera unavailable: " + e.Message + " Check Windows camera permissions and close other camera apps.";
                }
            }
            if (micWanted && microphone is null && shareMic.Checked && server?.MicrophoneConnected == true)
            {
                try
                {
                    var device = microphones.SelectedItem as MicrophoneDevice ?? throw new IOException("Choose a microphone first.");
                    runningMicGeneration = Volatile.Read(ref micGeneration);
                    var generation = runningMicGeneration;
                    microphone = new MicrophoneCapture(device.Id, server.MediaSender(true), error => Post(() => {
                        if (generation == Volatile.Read(ref micGeneration)) SuspendMicrophone(error);
                    }));
                    if (runningMicGeneration != Volatile.Read(ref micGeneration) || !shareMic.Checked || server?.MicrophoneConnected != true)
                    { var old = microphone; microphone = null; await old.DisposeAsync(); }
                    else { microphone.Start(); micStatus.Text = "Sharing microphone with the Mac."; }
                }
                catch (Exception e)
                {
                    SuspendMicrophone("Microphone unavailable: " + e.Message);
                    if (microphone is not null) { var old = microphone; microphone = null; await old.DisposeAsync(); }
                }
            }
            if (shareCamera.Checked && camera is null) cameraStatus.Text = "Camera idle · start the camera from the Mac menu.";
            if (shareMic.Checked && microphone is null) micStatus.Text = "Microphone idle · waiting for a Mac app to use it.";
        }
        catch (Exception e) { cameraStatus.Text = "Sharing stopped: " + e.Message; CancelCapture(); }
        finally { changes.Release(); }
    }
}
