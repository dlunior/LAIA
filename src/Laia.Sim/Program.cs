namespace Laia.Sim;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\LAIA.Sim.SingleInstance", out var first);
        if (!first) return; // already running in the tray

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.ThreadException += (_, e) => Log.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        var shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0 && shots + 1 < args.Length) { Screenshots.Run(args[shots + 1]); return; }
        Log.Write($"LAIA Sim {Application.ProductVersion} starting");
        Application.Run(new TrayApp(args.Contains("--setup-ptt")));
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LAIA");
    public static string FilePath => Path.Combine(Dir, "laia-sim.log");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}

/// <summary>Automated build only: renders the settings pages, push-to-talk screen and transcript to PNG.</summary>
internal static class Screenshots
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var settings = new SimSettings { Ptt = new PttBinding("joy", 4, 0), Volume = 85 };
        var server = new Laia.Common.ServerInfo("LAIA Server on HEROVIII", "HEROVIII", "192.168.0.211", 47801, "0.1.4");
        using var demo = System.Text.Json.JsonDocument.Parse(
            "{\"simbrief_user\":\"123456\",\"schedule_source\":\"airlabs\",\"airlabs\":{\"set\":true,\"hint\":\"\u20269f3c\"},\"aeroapi\":{\"set\":false,\"hint\":\"\"}}");
        using (var f = new SettingsForm(settings, server, null, null, "ready", demo))
        {
            f.Show();
            for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
            f.SaveScreenshots(dir, "simapp-settings");
            f.Close();
        }
        using (var log = new RadioLogForm())
        {
            log.Show();
            log.Add("YOU", "LaGuardia Ground, Delta 1196, with information Bravo, ready to taxi.", Theme.Text);
            log.Add("ATC", "Delta eleven ninety-six, LaGuardia Ground, roger, hold position, I'll have your taxi instructions shortly.", Theme.Accent);
            for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
            using var bmp = new Bitmap(log.Width, log.Height);
            log.DrawToBitmap(bmp, new Rectangle(0, 0, log.Width, log.Height));
            bmp.Save(Path.Combine(dir, "simapp-transcript.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
