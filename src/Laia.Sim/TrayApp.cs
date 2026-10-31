using System.Diagnostics;
using Laia.Common;

namespace Laia.Sim;

/// <summary>
/// The whole sim-side app: a radio icon in the system tray. It finds the LAIA Server on its own,
/// records while push-to-talk is held, sends the call to the server and plays the controller's reply.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private enum Light { Searching, WarmingUp, Ready, Talking, Problem }

    private readonly NotifyIcon _tray;
    private readonly SimSettings _settings = SimSettings.Load();
    private readonly PttMonitor _ptt = new();
    private readonly Recorder _recorder = new();
    private readonly ServerLink _link = new();
    private readonly RadioLogForm _radioLog = new();
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pttItem;
    private readonly ToolStripMenuItem _logItem;
    private readonly System.Windows.Forms.Timer _heartbeat = new() { Interval = 5000 };
    private readonly Dictionary<Light, Icon> _icons;
    private ServerInfo? _server;
    private bool _busy;
    private int _misses;
    private bool _searching;
    private string _lastSpeech = "";

    public TrayApp(bool forcePttSetup)
    {
        _icons = new()
        {
            [Light.Searching] = Theme.StatusIcon(Theme.Muted),
            [Light.WarmingUp] = Theme.StatusIcon(Theme.Warn),
            [Light.Ready] = Theme.StatusIcon(Theme.Ok),
            [Light.Talking] = Theme.StatusIcon(Theme.Accent),
            [Light.Problem] = Theme.StatusIcon(Theme.Bad),
        };

        _statusItem = new ToolStripMenuItem("Looking for LAIA Server…") { Enabled = false };
        _pttItem = new ToolStripMenuItem("Settings…", null, async (_, _) => await OpenSettingsAsync()) { Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
        _logItem = new ToolStripMenuItem("Show radio transcript", null, (_, _) => ToggleLog()) { Checked = _settings.ShowTranscript };
        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pttItem);
        menu.Items.Add(_logItem);
        menu.Items.Add(new ToolStripMenuItem("Open ATC control page", null, (_, _) => OpenControlPage()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit LAIA Sim", null, (_, _) => ExitThread()));

        _tray = new NotifyIcon
        {
            Icon = _icons[Light.Searching],
            Text = "LAIA Sim - looking for server",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ToggleLog(forceShow: true);

        ApplyAudioSettings();
        _ptt.Binding = _settings.Ptt;
        _ptt.Pressed += OnPttPressed;
        _ptt.Released += OnPttReleased;
        UpdatePttMenu();

        _heartbeat.Tick += async (_, _) => await HeartbeatAsync();
        _heartbeat.Start();

        // Kick off once the message loop is running.
        var kick = new System.Windows.Forms.Timer { Interval = 300 };
        kick.Tick += async (_, _) =>
        {
            kick.Stop();
            kick.Dispose();
            if (_settings.Ptt is null || forcePttSetup) SetupPtt();
            await DiscoverAsync();
        };
        kick.Start();
    }

    // ------------------------------------------------------------------ server connection

    private async Task DiscoverAsync()
    {
        if (_searching) return;
        _searching = true;
        try
        {
            SetState(Light.Searching, "Looking for LAIA Server on your network…");
            var found = await Discovery.FindServersAsync(TimeSpan.FromSeconds(2));
            if (found.Count == 0)
            {
                SetState(Light.Searching, "LAIA Server not found yet - is the ATC PC on?");
                return;
            }
            var pick = found.FirstOrDefault(s => string.Equals(s.Machine, _settings.PreferredServer, StringComparison.OrdinalIgnoreCase))
                       ?? found.OrderBy(s => s.Machine).First();
            var isNew = _server?.Machine != pick.Machine;
            _server = pick;
            _misses = 0;
            if (_settings.PreferredServer != pick.Machine) { _settings.PreferredServer = pick.Machine; _settings.Save(); }
            Log.Write($"Paired with {pick.Machine} at {pick.Address}:{pick.Port} (v{pick.Version}); {found.Count} server(s) found");
            if (isNew && found.Count > 1)
                _tray.ShowBalloonTip(5000, "LAIA", $"Found {found.Count} LAIA servers. Using {pick.Machine}.", ToolTipIcon.Info);
            await HeartbeatAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Discovery error: " + ex.Message);
            SetState(Light.Problem, "Network problem while looking for the server");
        }
        finally { _searching = false; }
    }

    private async Task HeartbeatAsync()
    {
        if (_busy) return;
        if (_server is null) { await DiscoverAsync(); return; }
        var (ok, speech, detail) = await _link.StatusAsync(_server);
        if (!ok)
        {
            if (++_misses >= 2) { _server = null; await DiscoverAsync(); }
            return;
        }
        _misses = 0;
        if (speech != _lastSpeech) Log.Write($"Server speech status: {speech} {detail}");
        var wasReady = _lastSpeech == "ready";
        _lastSpeech = speech;
        switch (speech)
        {
            case "ready":
                SetState(Light.Ready, _settings.Ptt is null
                    ? $"Connected to {_server.Machine} - set a push-to-talk button"
                    : $"Connected to {_server.Machine} - hold {_settings.Ptt.Describe()} to talk");
                if (!wasReady && !_settings.FirstRunDone && _settings.Ptt is not null)
                {
                    _settings.FirstRunDone = true;
                    _settings.Save();
                    _tray.ShowBalloonTip(6000, "LAIA is ready", $"Hold {_settings.Ptt.Describe()} and call the controller, e.g. \"LaGuardia Ground, Delta 123, radio check.\"", ToolTipIcon.Info);
                    if (_settings.ShowTranscript) ToggleLog(forceShow: true);
                }
                break;
            case "error":
                SetState(Light.Problem, $"{_server.Machine}: {detail ?? "speech engine problem"}");
                break;
            default:
                SetState(Light.WarmingUp, $"{_server.Machine} is warming up its speech engine…");
                break;
        }
    }

    // ------------------------------------------------------------------ push-to-talk

    private void OnPttPressed()
    {
        if (_busy) return;
        if (_server is null || _lastSpeech != "ready")
        {
            _tray.ShowBalloonTip(3000, "LAIA", _server is null ? "Not connected to LAIA Server yet." : "The server is still warming up.", ToolTipIcon.Warning);
            return;
        }
        try
        {
            if (_settings.RadioClicks) _ = Player.PlayAsync(Player.Click(false));
            _recorder.Start();
            SetState(Light.Talking, "Transmitting…");
        }
        catch (Exception ex)
        {
            Log.Write("Microphone error: " + ex);
            SetState(Light.Problem, "Couldn't open the microphone. Check Windows sound settings.");
        }
    }

    private async void OnPttReleased()
    {
        if (!_recorder.IsRecording || _server is null) return;
        _busy = true;
        try
        {
            var (wav, length) = await _recorder.StopAsync();
            if (_settings.RadioClicks) _ = Player.PlayAsync(Player.Click(true));
            if (length < TimeSpan.FromMilliseconds(350)) { await HeartbeatAsync(); return; }
            SetState(Light.Talking, "Waiting for ATC…");
            var result = await _link.TransmitAsync(_server, wav);
            if (result is null) { Log.Write("Server heard nothing real; no reply"); return; }
            _radioLog.Add("YOU", string.IsNullOrWhiteSpace(result.Heard) ? "(nothing heard)" : result.Heard, Theme.Text);
            _radioLog.Add("ATC", result.Reply, Theme.Accent);
            await Player.PlayAsync(result.Audio);
        }
        catch (RadioException ex)
        {
            Log.Write("Radio error: " + ex.Message);
            _tray.ShowBalloonTip(4000, "LAIA", ex.Message, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Log.Write("Transmit failed: " + ex);
            _tray.ShowBalloonTip(4000, "LAIA", "Lost contact with the LAIA Server.", ToolTipIcon.Warning);
            _misses++;
        }
        finally
        {
            _busy = false;
            await HeartbeatAsync();
        }
    }

    private void SetupPtt()
    {
        _ptt.Suspended = true;
        try
        {
            using var f = new PttSetupForm(_settings.Ptt);
            if (f.ShowDialog() == DialogResult.OK && f.Result is not null)
            {
                _settings.Ptt = f.Result;
                _settings.Save();
                _ptt.Binding = f.Result;
                Log.Write("Push-to-talk set to " + f.Result.Describe());
            }
        }
        finally { _ptt.Suspended = false; }
        UpdatePttMenu();
        _ = HeartbeatAsync();
    }

    private void UpdatePttMenu() { }

    private void ApplyAudioSettings()
    {
        _recorder.DeviceId = _settings.MicDeviceId;
        Player.DeviceId = _settings.SpeakerDeviceId;
        Player.Volume = Math.Clamp(_settings.Volume, 0, 100) / 100f;
    }

    private bool _settingsOpen;

    private async Task OpenSettingsAsync()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        _ptt.Suspended = true;
        try
        {
            using var f = new SettingsForm(_settings, _server, _link, async () => { _server = null; await DiscoverAsync(); }, _lastSpeech);
            if (f.ShowDialog() == DialogResult.OK)
            {
                ApplyAudioSettings();
                _ptt.Binding = _settings.Ptt;
                Log.Write("Settings saved");
            }
        }
        finally { _ptt.Suspended = false; _settingsOpen = false; }
        await HeartbeatAsync();
    }

    // ------------------------------------------------------------------ misc UI

    private void ToggleLog(bool forceShow = false)
    {
        if (forceShow || !_radioLog.Visible)
        {
            _radioLog.Show();
            _settings.ShowTranscript = true;
        }
        else
        {
            _radioLog.Hide();
            _settings.ShowTranscript = false;
        }
        _logItem.Checked = _settings.ShowTranscript;
        _settings.Save();
    }

    private void OpenControlPage()
    {
        if (_server is null) { _tray.ShowBalloonTip(3000, "LAIA", "Not connected to LAIA Server yet.", ToolTipIcon.Info); return; }
        try { Process.Start(new ProcessStartInfo(_server.BaseUrl) { UseShellExecute = true }); } catch { }
    }

    private void SetState(Light light, string text)
    {
        _tray.Icon = _icons[light];
        _statusItem.Text = text;
        var tip = "LAIA Sim - " + text;
        _tray.Text = tip.Length > 63 ? tip[..62] + "…" : tip;
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _ptt.Dispose();
        _heartbeat.Dispose();
        _radioLog.Dispose();
        base.ExitThreadCore();
    }
}
