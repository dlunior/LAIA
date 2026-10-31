using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Laia.Common;
using Laia.Setup.Common;

namespace Laia.Setup.Sim;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool Has(string a) => args.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
        var shotsIdx = Array.FindIndex(args, a => a.Equals("/screenshots", StringComparison.OrdinalIgnoreCase));
        var shotsDir = shotsIdx >= 0 && shotsIdx + 1 < args.Length ? args[shotsIdx + 1] : null;
        var uninstall = Has("/uninstall") || (shotsDir is null && !Payload.Present());
        var plan = new SimPlan();
        SetupLog.Write($"LAIA Sim Setup {plan.Version} ({(uninstall ? "uninstall" : "install")}) args: {string.Join(' ', args)}");

        if (Has("/silent"))
        {
            List<InstallStep> steps;
            if (uninstall) steps = plan.UninstallSteps(Has("/purge"));
            else { plan.DetectLocal(); steps = plan.InstallSteps(); }
            var (ok, error) = InstallRunner.RunAsync(steps, new LogInstallView()).GetAwaiter().GetResult();
            SetupLog.Write(ok ? "SETUP OK" : "SETUP FAILED: " + error);
            if (uninstall) Sys.ScheduleDirectoryDelete(plan.InstallDir);
            return ok ? 0 : 1;
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
        if (uninstall) new SimUninstallFlow(window, plan).Start();
        else new SimInstallFlow(window, plan, shotsDir).Start();
        var code = app.Run(window);
        if (uninstall) Sys.ScheduleSelfDelete();
        return code;
    }
}

internal sealed class SimInstallFlow(SetupWindow w, SimPlan plan, string? shotsDir)
{
    private int _page;
    private CheckBox? _launch;

    public void Start()
    {
        w.Title = "LAIA Sim Setup";
        w.Subtitle = "Sim PC setup";
        w.SetSteps("Welcome", "Check this PC", "Install", "Finish");
        w.NextClicked += Next;
        w.BackClicked += () => { if (_page == 1) Welcome(); };
        w.CanClose = () => !w.Busy;
        Welcome();
        if (shotsDir is not null) w.Loaded += async (_, _) => await ScreenshotTourAsync();
    }

    private void Next()
    {
        switch (_page)
        {
            case 0: _ = ChecksAsync(); break;
            case 1: _ = InstallAsync(); break;
            default:
                if (_launch?.IsChecked == true) plan.Launch();
                w.Close();
                break;
        }
    }

    private void Welcome()
    {
        _page = 0;
        w.ShowPage(0, Ui.Page(
            Ui.Eyebrow("LAIA Sim"),
            Ui.Title("Your radio to LAIA ATC"),
            Ui.Body("LAIA Sim is a small radio that lives in your system tray. Hold your push-to-talk button, talk to ATC, and hear the reply. All the heavy work happens on your LAIA Server PC."),
            Ui.FeatureCard(
                ("", "Push-to-talk on any button", "Keyboard, mouse side button, yoke or joystick. You'll pick it in a moment."),
                ("", "Finds LAIA Server automatically", "No IP addresses or network settings."),
                ("", "Light on your sim", "No admin rights needed. Starts quietly with Windows.")),
            Ui.Small($"Installs to {plan.InstallDir}", 14)));
        w.SetButtons("Next");
    }

    private async Task ChecksAsync()
    {
        _page = 1;
        var sim = new StatusRow("Microsoft Flight Simulator");
        var aig = new StatusRow("AIG aircraft models");
        var server = new StatusRow("LAIA Server on your network");
        var others = new StatusRow("Other traffic and ATC apps");
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var r in new[] { sim, aig, server, others }) { r.Set(RowState.Working, "Checking…"); list.Children.Add(r); }
        w.ShowPage(1, Ui.Page(Ui.Eyebrow("Step 2 of 4"), Ui.Title("Checking this PC"),
            Ui.Body("LAIA Sim looks for your simulator, your AI aircraft and your LAIA Server on its own."), list));
        w.SetButtons("Install", nextEnabled: false, back: true);

        await Task.WhenAll(Task.Run(plan.DetectLocal), plan.DiscoverAsync());

        if (plan.Sims.Count == 0)
            sim.Set(RowState.Warn, "Not found. You can still install; LAIA will look again when it starts.");
        else
            sim.Set(RowState.Ok, string.Join("\n", plan.Sims.Select(s => $"{s.DisplayName}" + (s.CommunityPath is null ? "" : $"  ·  Community folder: {s.CommunityPath}"))));

        if (plan.AiManager)
            aig.Set(RowState.Ok, plan.AigPackages > 0 ? $"AI Manager installed, {plan.AigPackages} aircraft packages found" : "AI Manager installed");
        else
            aig.Set(RowState.Warn, "AIG AI Manager not found. LAIA uses AIG models for its airliners. You can install it later.");

        if (plan.Servers.Count == 1)
            server.Set(RowState.Ok, $"Found {plan.Servers[0].Machine} ({plan.Servers[0].Address})");
        else if (plan.Servers.Count > 1)
            server.Set(RowState.Ok, $"Found {plan.Servers.Count}: {string.Join(", ", plan.Servers.Select(s => s.Machine))}. LAIA Sim will use {plan.Servers.OrderBy(s => s.Machine).First().Machine}.");
        else
            server.Set(RowState.Warn, "Not found yet. Install LAIA Server on your ATC PC (or turn it on). LAIA Sim will keep looking.");

        if (plan.Conflicts.Count == 0)
            others.Set(RowState.Ok, "None found");
        else
            others.Set(RowState.Warn, $"Found {string.Join(", ", plan.Conflicts)}. That's fine, just don't run them at the same time as LAIA.");

        w.SetButtons("Install", nextEnabled: true, back: true);
    }

    private async Task InstallAsync()
    {
        _page = 2;
        var steps = plan.InstallSteps();
        var page = new InstallPage("Installing LAIA Sim", "This only takes a few seconds.", steps);
        w.ShowPage(2, page);
        w.SetButtons(null, cancel: false);
        w.Busy = true;
        var (ok, error) = await InstallRunner.RunAsync(steps, page);
        w.Busy = false;
        page.Complete(ok);
        SetupLog.Write(ok ? "SETUP OK" : "SETUP FAILED: " + error);
        await Task.Delay(500);
        Finish(ok, error);
    }

    private void Finish(bool ok, string? error)
    {
        _page = 3;
        if (ok)
        {
            _launch = Ui.Check("Start LAIA Sim now", true);
            w.ShowPage(3, Ui.Page(
                Ui.Eyebrow("All set"),
                Ui.Title("LAIA Sim is installed"),
                Ui.Body("When it starts, it will ask you to press your push-to-talk button once. After that, look for the LAIA radio icon in your system tray."),
                Ui.FeatureCard(
                    ("", "Green light means ready", "Hold your push-to-talk button and call the controller, for example \"LaGuardia Ground, Delta 123, radio check.\""),
                    ("", "Radio transcript", "Your calls and ATC's replies appear in a small window. Double-click the tray icon to show or hide it.")),
                Ui.Card(_launch, 16)));
            w.SetButtons("Finish", cancel: false);
        }
        else
        {
            w.ShowPage(3, Ui.Page(
                Ui.Eyebrow("Setup stopped"),
                Ui.Title("Setup couldn't finish"),
                Ui.Body(error ?? "Something went wrong."),
                Ui.Link("Open the setup log", () => Sys.Open(SetupLog.FilePath))));
            w.SetButtons("Close", cancel: false);
        }
    }

    private async Task ScreenshotTourAsync()
    {
        async Task Shot(string name) { await Task.Delay(900); await w.Dispatcher.InvokeAsync(() => w.SaveScreenshot(Path.Combine(shotsDir!, name)), DispatcherPriority.Background); }
        try
        {
            await Shot("sim-1-welcome.png");
            await ChecksAsync();
            await Shot("sim-2-checks.png");
            var steps = plan.InstallSteps();
            var page = new InstallPage("Installing LAIA Sim", "This only takes a few seconds.", steps);
            w.ShowPage(2, page);
            w.SetButtons(null, cancel: false);
            page.StepFinished(0, RowState.Ok, "");
            page.StepStarted(1, steps[1]);
            page.StepProgress(1, 0.55, 0.6, "LaiaSim.exe");
            await Shot("sim-3-installing.png");
            Finish(true, null);
            await Shot("sim-4-done.png");
        }
        catch (Exception ex) { SetupLog.Write("Screenshot tour failed: " + ex); }
        w.CanClose = null;
        w.Close();
    }
}

internal sealed class SimUninstallFlow(SetupWindow w, SimPlan plan)
{
    private CheckBox? _purge;
    private bool _done;

    public void Start()
    {
        w.Title = "Remove LAIA Sim";
        w.Subtitle = "Sim PC removal";
        w.SetSteps("Confirm", "Remove", "Done");
        w.NextClicked += Next;
        w.CanClose = () => !w.Busy;
        _purge = Ui.Check("Also delete my LAIA Sim settings (push-to-talk button, etc.)", false);
        w.ShowPage(0, Ui.Page(
            Ui.Eyebrow("LAIA Sim"),
            Ui.Title("Remove LAIA Sim from this PC?"),
            Ui.Body("The tray radio will be closed and removed. Your simulator and AI aircraft are not touched."),
            Ui.Card(_purge, 22)));
        w.SetButtons("Remove");
    }

    private async void Next()
    {
        if (_done) { w.Close(); return; }
        var steps = plan.UninstallSteps(_purge?.IsChecked == true);
        var page = new InstallPage("Removing LAIA Sim", "This takes a few seconds.", steps);
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
            Ui.Title(ok ? "LAIA Sim has been removed" : "Some parts couldn't be removed"),
            Ui.Body(ok ? "Thanks for flying with LAIA." : error ?? "")));
        w.SetButtons("Close", cancel: false);
    }
}
