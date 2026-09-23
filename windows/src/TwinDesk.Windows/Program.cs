using System.Text.Json;

namespace TwinDesk;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--diagnose")
        {
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { monitors = Monitors.Scan(), audio = AudioPlayer.Devices() }, new JsonSerializerOptions { WriteIndented = true })); return;
        }
        var data = Array.IndexOf(args, "--data");
        if (data >= 0 && args.Length > data + 1) Settings.DataDirectory = Path.GetFullPath(args[data + 1]);
        else
        {
            var location = Path.Combine(AppContext.BaseDirectory, "data-directory.txt");
            if (File.Exists(location)) Settings.DataDirectory = Path.GetFullPath(File.ReadAllText(location).Trim());
        }
        var startInTray = Array.IndexOf(args, "--startup") >= 0;
        if (startInTray) StartupTrace.Write("launch");
        using var instance = new Mutex(true, "Local\\TwinDesk-" + Environment.UserName, out var first);
        if (startInTray) StartupTrace.Write($"mutex first={first}");
        if (!first) { MessageBox.Show("TwinDesk is already running. Open it from the system tray.", "TwinDesk"); return; }
        ApplicationConfiguration.Initialize();
        if (startInTray) StartupTrace.Write("winforms initialized");
        var preview = Array.IndexOf(args, "--render-preview");
        if (preview >= 0 && args.Length > preview + 1)
        {
            using var form = new MainForm();
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000);
            form.Show();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(20); }
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            bitmap.Save(args[preview + 1]); form.Close(); return;
        }
        try { Application.Run(new MainForm(startInTray)); }
        catch (Exception e) { MessageBox.Show(e.Message, "TwinDesk could not start", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}

internal static class StartupTrace
{
    internal static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            File.AppendAllText(Settings.PathFor("startup.log"), $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
