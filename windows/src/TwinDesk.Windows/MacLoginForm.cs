using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace TwinDesk;

internal sealed class MacLoginForm : Form
{
    private readonly MacLoginConnection connection;
    private readonly Func<bool> connected;
    private readonly LoginCanvas canvas = new() { Dock = DockStyle.Fill, TabStop = true };
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12), Text = "Click your Mac account and sign in here using this PC's keyboard and mouse." };
    private readonly CancellationTokenSource stop = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer pointerTimer = new() { Interval = 20 };
    private readonly HashSet<uint> held = [];
    private Point? pendingPointer;
    private Point lastPointer;
    private byte buttons;
    private char? highSurrogate;
    private int framePending;
    private bool checking, started;
    private Task? receiver;
    private Task? desktopCheck;

    internal MacLoginForm(MacLoginConnection connection, Func<bool> connected)
    {
        this.connection = connection; this.connected = connected;
        Text = "TwinDesk · Sign in to your Mac"; Size = new Size(1100, 780); MinimumSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(20, 26, 36); ForeColor = Color.White;
        Font = new Font("Segoe UI", 10); Controls.Add(canvas); Controls.Add(status);
        var footer = new Label { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(12, 7, 0, 0), Text = "This temporary view closes after TwinDesk reconnects. Closing it keeps you on the PC." };
        Controls.Add(footer);
        canvas.MouseMove += (_, e) => pendingPointer = Map(e.Location);
        canvas.MouseDown += (_, e) => { canvas.Focus(); FlushPointer(); lastPointer = Map(e.Location); buttons |= ButtonMask(e.Button); Send(() => connection.Screen.Pointer(lastPointer.X, lastPointer.Y, buttons)); };
        canvas.MouseUp += (_, e) => { FlushPointer(); lastPointer = Map(e.Location); buttons &= (byte)~ButtonMask(e.Button); Send(() => connection.Screen.Pointer(lastPointer.X, lastPointer.Y, buttons)); };
        canvas.MouseWheel += (_, e) => { FlushPointer(); var wheel = e.Delta > 0 ? (byte)8 : (byte)16; Send(() => { connection.Screen.Pointer(lastPointer.X, lastPointer.Y, (byte)(buttons | wheel)); connection.Screen.Pointer(lastPointer.X, lastPointer.Y, buttons); }); };
        canvas.KeyDown += (_, e) => {
            var symbol = SpecialKey(e.KeyCode);
            if (symbol == 0 && e.Control && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) symbol = (uint)('a' + (int)e.KeyCode - (int)Keys.A);
            if (symbol != 0) { held.Add(symbol); Send(() => connection.Screen.Key(symbol, true)); e.SuppressKeyPress = true; }
        };
        canvas.KeyUp += (_, e) => {
            var symbol = SpecialKey(e.KeyCode);
            if (symbol == 0 && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) symbol = (uint)('a' + (int)e.KeyCode - (int)Keys.A);
            if (held.Remove(symbol)) { Send(() => connection.Screen.Key(symbol, false)); e.Handled = true; }
        };
        canvas.KeyPress += (_, e) => {
            e.Handled = true;
            if (char.IsHighSurrogate(e.KeyChar)) { highSurrogate = e.KeyChar; return; }
            uint scalar;
            if (char.IsLowSurrogate(e.KeyChar)) { if (highSurrogate is not { } first) return; scalar = (uint)char.ConvertToUtf32(first, e.KeyChar); }
            else { if (char.IsControl(e.KeyChar)) return; scalar = e.KeyChar; }
            highSurrogate = null; var symbol = scalar <= 255 ? scalar : 0x01000000u | scalar;
            Send(() => { connection.Screen.Key(symbol, true); connection.Screen.Key(symbol, false); });
        };
        Deactivate += (_, _) => ReleaseInput();
        pointerTimer.Tick += (_, _) => FlushPointer();
        timer.Tick += (_, _) => { if (!checking) desktopCheck = CheckDesktop(); };
        connection.Screen.Frame += PresentFrame;
        Shown += (_, _) => { canvas.Focus(); pointerTimer.Start(); timer.Start(); receiver = Task.Run(() => connection.Screen.RunAsync(stop.Token)); _ = Receive(); };
        FormClosing += (_, _) => { ReleaseInput(); stop.Cancel(); timer.Stop(); pointerTimer.Stop(); };
        FormClosed += (_, _) => { connection.Screen.Frame -= PresentFrame; timer.Dispose(); pointerTimer.Dispose(); canvas.Image = null; };
    }
    private async Task Receive()
    {
        try { if (receiver is not null) await receiver; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception) { if (!IsDisposed && !stop.IsCancellationRequested) { status.Text = "The Mac sign-in view closed. Checking whether sign-in finished and TwinDesk is reconnecting…"; pointerTimer.Stop(); } }
    }
    internal async Task FinishAsync()
    {
        stop.Cancel();
        if (receiver is not null) { try { await receiver; } catch { } }
        if (desktopCheck is not null) { try { await desktopCheck; } catch { } }
        stop.Dispose();
    }
    private async Task CheckDesktop()
    {
        if (checking || IsDisposed || stop.IsCancellationRequested) return;
        if (connected()) { DialogResult = DialogResult.OK; Close(); return; }
        checking = true;
        try
        {
            if (!started && await connection.DesktopStateAsync(stop.Token) == MacStartupState.DesktopReady)
            {
                await connection.StartTwinDeskAsync(stop.Token); started = true;
                if (!IsDisposed) status.Text = "Signed in. Waiting for TwinDesk to reconnect…";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) status.Text = "The sign-in view is available, but TwinDesk's connection is not ready yet."; }
        finally { checking = false; }
    }
    private void PresentFrame()
    {
        if (IsDisposed || stop.IsCancellationRequested || Interlocked.CompareExchange(ref framePending, 1, 0) != 0) return;
        Bitmap? image = null;
        try
        {
            image = new Bitmap(connection.Screen.Width, connection.Screen.Height, PixelFormat.Format32bppRgb);
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(connection.Screen.Pixels, 0, data.Scan0, connection.Screen.Pixels.Length); }
            finally { image.UnlockBits(data); }
            var ready = image;
            BeginInvoke(() => { try { if (IsDisposed) ready.Dispose(); else canvas.Image = ready; } finally { Interlocked.Exchange(ref framePending, 0); } });
            image = null;
        }
        catch { image?.Dispose(); Interlocked.Exchange(ref framePending, 0); }
    }
    private Point Map(Point point)
    {
        var area = canvas.ImageArea;
        if (area.Width <= 0 || area.Height <= 0) return Point.Empty;
        return new Point(Math.Clamp((point.X - area.X) * connection.Screen.Width / area.Width, 0, connection.Screen.Width - 1), Math.Clamp((point.Y - area.Y) * connection.Screen.Height / area.Height, 0, connection.Screen.Height - 1));
    }
    private void FlushPointer()
    {
        if (pendingPointer is not { } point) return; pendingPointer = null; lastPointer = point;
        Send(() => connection.Screen.Pointer(point.X, point.Y, buttons));
    }
    private void Send(Action action)
    {
        try { action(); } catch { if (!IsDisposed) status.Text = "The Mac input connection stalled. Close this view and reconnect before continuing."; }
    }
    private void ReleaseInput()
    {
        pendingPointer = null; buttons = 0;
        Send(() => { foreach (var key in held) connection.Screen.Key(key, false); connection.Screen.Pointer(lastPointer.X, lastPointer.Y, 0); }); held.Clear();
    }
    private static byte ButtonMask(MouseButtons button) => button switch { MouseButtons.Left => 1, MouseButtons.Middle => 2, MouseButtons.Right => 4, _ => 0 };
    private static uint SpecialKey(Keys key) => key switch {
        Keys.Back => 0xff08, Keys.Tab => 0xff09, Keys.Enter => 0xff0d, Keys.Escape => 0xff1b,
        Keys.Home => 0xff50, Keys.Left => 0xff51, Keys.Up => 0xff52, Keys.Right => 0xff53, Keys.Down => 0xff54,
        Keys.PageUp => 0xff55, Keys.PageDown => 0xff56, Keys.End => 0xff57, Keys.Insert => 0xff63, Keys.Delete => 0xffff,
        Keys.ShiftKey or Keys.LShiftKey => 0xffe1, Keys.RShiftKey => 0xffe2,
        Keys.ControlKey or Keys.LControlKey => 0xffe3, Keys.RControlKey => 0xffe4,
        Keys.Menu or Keys.LMenu => 0xffe9, Keys.RMenu => 0xffea,
        Keys.F1 => 0xffbe, Keys.F2 => 0xffbf, Keys.F3 => 0xffc0, Keys.F4 => 0xffc1, Keys.F5 => 0xffc2, Keys.F6 => 0xffc3,
        Keys.F7 => 0xffc4, Keys.F8 => 0xffc5, Keys.F9 => 0xffc6, Keys.F10 => 0xffc7, Keys.F11 => 0xffc8, Keys.F12 => 0xffc9,
        _ => 0
    };
}

internal sealed class LoginCanvas : Control
{
    private Bitmap? image;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Bitmap? Image { get => image; set { var previous = image; image = value; previous?.Dispose(); Invalidate(); } }
    internal Rectangle ImageArea {
        get {
            if (image is null || ClientSize.Width == 0 || ClientSize.Height == 0) return Rectangle.Empty;
            var scale = Math.Min((double)ClientSize.Width / image.Width, (double)ClientSize.Height / image.Height);
            var width = Math.Max(1, (int)(image.Width * scale)); var height = Math.Max(1, (int)(image.Height * scale));
            return new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        }
    }
    internal LoginCanvas() { DoubleBuffered = true; BackColor = Color.Black; SetStyle(ControlStyles.Selectable, true); }
    protected override bool IsInputKey(Keys keyData) => true;
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); if (image is not null) e.Graphics.DrawImage(image, ImageArea); }
}
