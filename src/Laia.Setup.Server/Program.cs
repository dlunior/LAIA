using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Laia.Common;
using Laia.Setup.Common;

namespace Laia.Setup.Server;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
        var shotsIdx = Array.FindIndex(args, a => a.Equals("/screenshots", StringComparison.OrdinalIgnoreCase));
        var shotsDir = shotsIdx >= 0 && shotsIdx + 1 < args.Length ? args[shotsIdx + 1] : null;
        var uninstall = Has("/uninstall") || (shotsDir is null && !Payload.Present());
        var plan = new ServerPlan();
        SetupLog.Write($"LAIA Server Setup {plan.Version} ({(uninstall ? "uninstall" : "install")}) args: {string.Join(' ', args)}");

        if (Has("/silent"))
        {
            if (!uninstall) return SilentInstall(plan);
            var rc = RunSilent(plan.UninstallSteps(Has("/purge")));
            Sys.ScheduleDirectoryDelete(plan.InstallDir);
            return rc;
        }

        if (uninstall && shotsDir is null && Sys.RelaunchFromTempIfInside(plan.InstallDir, "/uninstall"))
            return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            SetupLog.Write("UI error: " + e.Exception);
            MessageBox.Show(e.Exception.Message, "LAIA Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var window = new SetupWindow();
        if (uninstall) new UninstallFlow(window, plan).Start();
        else new InstallFlow(window, plan, shotsDir).Start();
        var code = app.Run(window);
        if (uninstall) Sys.ScheduleSelfDelete();
        return code;
    }

    private static int SilentInstall(ServerPlan plan)
    {
        plan.DetectAsync().GetAwaiter().GetResult();
        return RunSilent(plan.InstallSteps());
    }

    private static int RunSilent(List<InstallStep> steps)
    {
        var (ok, error) = InstallRunner.RunAsync(steps, new LogInstallView()).GetAwaiter().GetResult();
        SetupLog.Write(ok ? "SETUP OK" : "SETUP FAILED: " + error);
        return ok ? 0 : 1;
    }
}

internal sealed class InstallFlow(SetupWindow w, ServerPlan plan, string? shotsDir)
{
    private int _page;
    private bool _checksOk;

    public void Start()
    {
        w.Title = "LAIA Server Setup";
        w.Subtitle = "Server setup";
        w.SetSteps("Welcome", "Check this PC", "Install", "Finish");
        w.NextClicked += Next;
        w.BackClicked += () => { if (_page == 1) Welcome(); };
        w.CanClose = () => !w.Busy || MessageBox.Show(w, "Setup is still working. Stop now?", "LAIA Setup", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        Welcome();
        if (shotsDir is not null) w.Loaded += async (_, _) => await ScreenshotTourAsync();
    }

    private void Next()
    {
        switch (_page)
        {
            case 0: _ = ChecksAsync(); break;
            case 1: if (_checksOk) _ = InstallAsync(); break;
            default: w.Close(); break;
        }
    }

    private void Welcome()
    {
        _page = 0;
        w.ShowPage(0, Ui.Page(
            Ui.Eyebrow("LAIA Server"),
            Ui.Title("Turn this PC into your ATC center"),
            Ui.Body("LAIA runs the AI traffic and the air traffic controllers on this computer, so your flight sim PC keeps all its power for flying."),
            Ui.FeatureCard(
                ("", "Runs quietly in the background", "Starts with Windows. Other people can keep using this PC as normal."),
                ("", "Finds your sim PC by itself", "No IP addresses or network settings to type in."),
                ("", "Downloads what it needs", "Speech recognition and the controller voice are fetched automatically (about 2 GB).")),
            Ui.Small($"Installs to {plan.InstallDir}", 14)));
        w.SetButtons("Next");
    }

    private async Task ChecksAsync()
    {
        _page = 1;
        var rows = new[]
        {
            new StatusRow("Windows"), new StatusRow("Processor and memory"), new StatusRow("Graphics card"),
            new StatusRow("Disk space"), new StatusRow("Internet connection"),
        };
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var r in rows) { r.Set(RowState.Working, "Checking…"); list.Children.Add(r); }
        w.ShowPage(1, Ui.Page(Ui.Eyebrow("Step 2 of 4"), Ui.Title("Checking this PC"),
            Ui.Body("LAIA picks the best settings for this computer automatically."), list));
        w.SetButtons("Install", nextEnabled: false, back: true);

        await plan.DetectAsync();

        rows[0].Set(RowState.Ok, plan.WindowsName);
        rows[1].Set(plan.RamGb >= 8 ? RowState.Ok : RowState.Warn, $"{plan.CpuName}, {plan.RamGb:0} GB memory");
        if (plan.UseGpu)
            rows[2].Set(RowState.Ok, $"{plan.Gpu!.Name}, {plan.Gpu.VramText}. Speech recognition will run on the graphics card.");
        else if (plan.Gpu is not null)
            rows[2].Set(RowState.Warn, $"{plan.Gpu.Name}. No compatible NVIDIA card, so speech will run on the processor (a little slower).");
        else
            rows[2].Set(RowState.Warn, "No graphics card found. Speech will run on the processor.");
        var need = plan.UseGpu ? 6 : 3;
        var diskOk = plan.FreeGb < 0 || plan.FreeGb >= need;
        rows[3].Set(diskOk ? RowState.Ok : RowState.Fail, plan.FreeGb < 0 ? "Couldn't measure free space" :
            diskOk ? $"{plan.FreeGb:0} GB free (LAIA needs about {need} GB)" : $"Only {plan.FreeGb:0.0} GB free. Free up at least {need} GB on drive C: and run setup again.");
        rows[4].Set(plan.Internet ? RowState.Ok : RowState.Warn, plan.Internet ? "Connected" : "Couldn't reach the download servers. Setup needs the internet to download speech models.");

        _checksOk = diskOk;
        w.SetButtons("Install", nextEnabled: _checksOk, back: true);
    }

    private async Task InstallAsync()
    {
        _page = 2;
        var steps = plan.InstallSteps();
        var page = new InstallPage("Setting up LAIA Server", "This usually takes 5 to 15 minutes, depending on your internet speed. You can leave it running.", steps);
        w.ShowPage(2, page);
        w.SetButtons(null, cancel: false);
        w.Busy = true;
        var (ok, error) = await InstallRunner.RunAsync(steps, page);
        w.Busy = false;
        page.Complete(ok);
        SetupLog.Write(ok ? "SETUP OK" : "SETUP FAILED: " + error);
        await Task.Delay(600);
        Finish(ok, error);
    }

    private void Finish(bool ok, string? error)
    {
        _page = 3;
        var url = $"http://localhost:{LaiaPorts.Http}";
        if (ok)
        {
            w.ShowPage(3, Ui.Page(
                Ui.Eyebrow("All set"),
                Ui.Title("LAIA Server is running"),
                Ui.Body($"This PC ({Environment.MachineName}) is now your ATC center. It starts automatically with Windows."),
                Ui.FeatureCard(
                    ("", "Next: install LAIA Sim on your flight sim PC", "Run LAIA-Sim-Setup.exe there. It will find this PC on its own."),
                    ("", "Control page", $"See status and the radio log from any browser on your network at http://{Environment.MachineName}:{LaiaPorts.Http}")),
                Ui.Secondary("Open control page", () => Sys.Open(url))));
            w.SetButtons("Finish", cancel: false);
        }
        else
        {
            w.ShowPage(3, Ui.Page(
                Ui.Eyebrow("Setup stopped"),
                Ui.Title("Setup couldn't finish"),
                Ui.Body(error ?? "Something went wrong."),
                Ui.Body("Running setup again usually fixes this. Anything already downloaded is kept.", 10),
                Ui.Link("Open the setup log", () => Sys.Open(SetupLog.FilePath))));
            w.SetButtons("Close", cancel: false);
        }
    }

    /// <summary>Automated build only: renders every page to PNG without installing anything.</summary>
    private async Task ScreenshotTourAsync()
    {
        async Task Shot(string name) { await Task.Delay(900); await w.Dispatcher.InvokeAsync(() => w.SaveScreenshot(Path.Combine(shotsDir!, name)), DispatcherPriority.Background); }
        try
        {
            await Shot("server-1-welcome.png");
            await ChecksAsync();
            await Shot("server-2-checks.png");
            var steps = plan.InstallSteps();
            var page = new InstallPage("Setting up LAIA Server", "This usually takes 5 to 15 minutes, depending on your internet speed. You can leave it running.", steps);
            w.ShowPage(2, page);
            w.SetButtons(null, cancel: false);
            for (var i = 0; i < 4; i++) page.StepFinished(i, RowState.Ok, i == 3 ? "Speech recognition: Whisper medium.en on graphics card" : "");
            page.StepStarted(4, steps[4]);
            page.StepProgress(4, 0.46, 0.4, "131 MB of about 353 MB");
            await Shot("server-3-installing.png");
            Finish(true, null);
            await Shot("server-4-done.png");
            Finish(false, "Downloading speech recognition: Couldn't download the speech recognition model. Check the internet connection and run setup again.");
            await Shot("server-5-error.png");
        }
        catch (Exception ex) { SetupLog.Write("Screenshot tour failed: " + ex); }
        w.CanClose = null;
        w.Close();
    }
}

internal sealed class UninstallFlow(SetupWindow w, ServerPlan plan)
{
    private CheckBox? _purge;
    private bool _done;

    public void Start()
    {
        w.Title = "Remove LAIA Server";
        w.Subtitle = "Server removal";
        w.SetSteps("Confirm", "Remove", "Done");
        w.NextClicked += Next;
        w.CanClose = () => !w.Busy;
        var gb = plan.DataSizeGb();
        _purge = Ui.Check($"Also delete downloaded speech models and settings{(gb > 0.05 ? $" ({gb:0.0} GB)" : "")}", true);
        w.ShowPage(0, Ui.Page(
            Ui.Eyebrow("LAIA Server"),
            Ui.Title("Remove LAIA Server from this PC?"),
            Ui.Body("The LAIA background service will be stopped and removed. Your flight sim PC won't be able to reach ATC until you install it again."),
            Ui.Card(_purge, 22)));
        w.SetButtons("Remove");
    }

    private async void Next()
    {
        if (_done) { w.Close(); return; }
        var steps = plan.UninstallSteps(_purge?.IsChecked == true);
        var page = new InstallPage("Removing LAIA Server", "This takes a few seconds.", steps);
        w.ShowPage(1, page);
        w.SetButtons(null, cancel: false);
        w.Busy = true;
        var (ok, error) = await InstallRunner.RunAsync(steps, page);
        w.Busy = false;
        page.Complete(ok);
        await Task.Delay(500);
        _done = true;
        w.ShowPage(2, Ui.Page(
            Ui.Eyebrow(ok ? "Done" : "Partly done"),
            Ui.Title(ok ? "LAIA Server has been removed" : "Some parts couldn't be removed"),
            Ui.Body(ok ? "Thanks for flying with LAIA." : error ?? "")));
        w.SetButtons("Close", cancel: false);
    }
}
