using System.Drawing.Drawing2D;
using System.Text.Json;
using Laia.Common;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace Laia.Sim;

/// <summary>
/// LAIA Sim settings. Audio and push-to-talk are saved on this PC. Flight plan, Navigraph and
/// traffic-data keys are sent to LAIA Server and stored (encrypted) on the ATC PC, which uses them.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly SimSettings _s;
    private readonly ServerInfo? _server;
    private readonly ServerLink? _link;
    private readonly Func<Task>? _rediscover;
    private readonly string _speech;
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(32, 24, 32, 12) };
    private readonly Panel _nav = new() { Dock = DockStyle.Left, Width = 210, BackColor = Color.FromArgb(9, 13, 24), Padding = new Padding(12, 20, 12, 12) };
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, NavItem> _navItems = new();
    private readonly MicMonitor _mic = new();
    private readonly System.Windows.Forms.Timer _meterTimer = new() { Interval = 50 };

    // Audio
    private DarkCombo _micBox = null!, _spkBox = null!;
    private LevelMeter _meter = null!;
    private Slider _volume = null!;
    private CheckBox _clicks = null!, _autostart = null!;
    // Push-to-talk
    private PttBinding? _ptt;
    private Label _pttLabel = null!;
    // Server-side
    private TextBox _simbrief = null!, _airlabs = null!, _aeroapi = null!;
    private RadioButton _srcAig = null!, _srcAirlabs = null!;
    private Label _airlabsHint = null!, _aeroapiHint = null!, _serverBanner = null!;
    private bool _airlabsRemove, _aeroapiRemove;
    private string _loadedSimbrief = "", _loadedSource = "aig";
    private readonly List<Control> _serverControls = new();

    public SettingsForm(SimSettings settings, ServerInfo? server, ServerLink? link, Func<Task>? rediscover, string speechStatus, JsonDocument? demoServerSettings = null)
    {
        _s = settings;
        _server = server;
        _link = link;
        _rediscover = rediscover;
        _speech = speechStatus;
        _ptt = settings.Ptt;

        Theme.Apply(this);
        Text = "LAIA Sim - Settings";
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 600);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Bg };
        footer.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
        var save = Theme.PrimaryButton("Save");
        var cancel = Theme.SecondaryButton("Cancel");
        save.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        footer.Resize += (_, _) =>
        {
            save.Location = new Point(footer.Width - save.Width - 28, 16);
            cancel.Location = new Point(save.Left - cancel.Width - 10, 16);
        };
        save.Click += async (_, _) => await SaveAsync();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        AcceptButton = save;
        CancelButton = cancel;

        BuildNav();
        Controls.Add(_content);
        Controls.Add(footer);
        Controls.Add(_nav);

        AddPage("Audio", "", BuildAudio());
        AddPage("Push-to-talk", "", BuildPtt());
        AddPage("Flight plan", "", BuildFlightPlan());
        AddPage("Navigraph", "", BuildNavigraph());
        AddPage("Traffic data", "", BuildTraffic());
        AddPage("Connection", "", BuildConnection());
        AddPage("About", "", BuildAbout());
        ShowPage("Audio");

        _meterTimer.Tick += (_, _) => { _meter.Level = _mic.Level; _meter.Invalidate(); };
        Shown += async (_, _) =>
        {
            _mic.Start(SelectedId(_micBox));
            _meterTimer.Start();
            if (demoServerSettings is not null) ApplyServerSettings(demoServerSettings);
            else await LoadServerSettingsAsync();
        };
        FormClosed += (_, _) => { _meterTimer.Dispose(); _mic.Dispose(); };
    }

    // ------------------------------------------------------------------ layout helpers

    private void BuildNav()
    {
        var title = new Label { Text = "Settings", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(18, 18) };
        _nav.Controls.Add(title);
    }

    private void AddPage(string name, string glyph, Control page)
    {
        var item = new NavItem(name, glyph) { Location = new Point(10, 70 + _navItems.Count * 44), Width = 188 };
        item.Click += (_, _) => ShowPage(name);
        _nav.Controls.Add(item);
        _navItems[name] = item;
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _content.Controls.Add(page);
        _pages[name] = page;
    }

    public void ShowPage(string name)
    {
        foreach (var (k, p) in _pages) p.Visible = k == name;
        foreach (var (k, n) in _navItems) { n.Selected = k == name; n.Invalidate(); }
    }

    public IEnumerable<string> PageNames => _pages.Keys;

    private static FlowLayoutPanel Page(string title, string subtitle)
    {
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg };
        f.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 18f), ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 4) });
        f.Controls.Add(Hint(subtitle, 600, 0, 14));
        return f;
    }

    private static Label Hint(string text, int width = 600, int top = 4, int bottom = 0) => new()
    {
        Text = text, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 9.5f), MaximumSize = new Size(width, 0), AutoSize = true,
        Margin = new Padding(0, top, 0, bottom),
    };

    private static Label FieldTitle(string text) => new()
    {
        Text = text, ForeColor = Theme.Text, Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = true, Margin = new Padding(0, 16, 0, 6),
    };

    private static TextBox Input(bool secret = false) => new()
    {
        Width = 360, BackColor = Theme.Panel, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11f),
        UseSystemPasswordChar = secret, Margin = new Padding(0, 0, 10, 0),
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Text = text, Checked = value, ForeColor = Theme.Text, AutoSize = true, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 16, 0, 0),
        FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderColor = Theme.Muted, CheckedBackColor = Theme.Accent },
    };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var r = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Theme.Bg };
        r.Controls.AddRange(controls);
        return r;
    }

    private static Label Result() => new() { AutoSize = true, MaximumSize = new Size(600, 0), Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, 6, 0, 0), ForeColor = Theme.Muted };

    // ------------------------------------------------------------------ pages

    private Control BuildAudio()
    {
        var p = Page("Audio", "Choose the microphone you talk on and where ATC's voice plays.");
        p.Controls.Add(FieldTitle("Microphone"));
        _micBox = new DarkCombo(AudioDevices.List(DataFlow.Capture), _s.MicDeviceId);
        _micBox.SelectedIndexChanged += (_, _) => _mic.Start(SelectedId(_micBox));
        p.Controls.Add(_micBox);
        _meter = new LevelMeter { Width = 420, Height = 10, Margin = new Padding(0, 10, 0, 0) };
        p.Controls.Add(_meter);
        p.Controls.Add(Hint("Talk normally: the bar should reach the middle. If it barely moves, LAIA may not hear you."));

        p.Controls.Add(FieldTitle("ATC voice plays on"));
        _spkBox = new DarkCombo(AudioDevices.List(DataFlow.Render), _s.SpeakerDeviceId);
        var test = Theme.SecondaryButton("Play test");
        test.Margin = new Padding(10, 0, 0, 0);
        test.Click += async (_, _) => await PlayTestAsync();
        p.Controls.Add(Row(_spkBox, test));

        p.Controls.Add(FieldTitle("ATC volume"));
        _volume = new Slider { Width = 420, Height = 28, Value = _s.Volume };
        p.Controls.Add(_volume);

        _clicks = Check("Play a radio click when I press and release push-to-talk", _s.RadioClicks);
        p.Controls.Add(_clicks);
        return p;
    }

    private Control BuildPtt()
    {
        var p = Page("Push-to-talk", "Hold this button while you talk to ATC. Keyboard keys, mouse side buttons and joystick or yoke buttons all work.");
        p.Controls.Add(FieldTitle("Current button"));
        _pttLabel = new Label { Text = _ptt?.Describe() ?? "Not set yet", ForeColor = Theme.Accent, Font = new Font("Segoe UI Semibold", 13f), AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        var change = Theme.SecondaryButton("Change…");
        change.Click += (_, _) =>
        {
            using var f = new PttSetupForm(_ptt);
            if (f.ShowDialog(this) == DialogResult.OK && f.Result is not null) { _ptt = f.Result; _pttLabel.Text = _ptt.Describe(); }
        };
        p.Controls.Add(Row(_pttLabel, change));
        return p;
    }

    private Control BuildFlightPlan()
    {
        var p = Page("Flight plan", "LAIA loads your latest SimBrief flight plan to prepare traffic, routes and runways for your flight.");
        p.Controls.Add(ServerBanner());
        p.Controls.Add(FieldTitle("SimBrief username or Pilot ID"));
        _simbrief = Input();
        var test = Theme.SecondaryButton("Test");
        var result = Result();
        test.Click += async (_, _) => await TestAsync("simbrief", _simbrief.Text, result, test);
        p.Controls.Add(Row(_simbrief, test));
        p.Controls.Add(result);
        p.Controls.Add(Hint("Find your Pilot ID on simbrief.com under Account Settings. Your username works too.", top: 10));
        _serverControls.AddRange(new Control[] { _simbrief, test });
        return p;
    }

    private Control BuildNavigraph()
    {
        var p = Page("Navigraph", "With a Navigraph subscription, LAIA uses current procedures, airspace and frequencies worldwide.");
        var btn = Theme.PrimaryButton("Sign in with Navigraph");
        btn.Enabled = false;
        btn.Margin = new Padding(0, 12, 0, 0);
        p.Controls.Add(btn);
        p.Controls.Add(Hint("Coming soon: this button turns on once Navigraph approves LAIA. You'll sign in on Navigraph's own website, so LAIA never sees your password.", top: 12));
        p.Controls.Add(Hint("Without Navigraph, LAIA uses free FAA data for US procedures.", top: 10));
        return p;
    }

    private Control BuildTraffic()
    {
        var p = Page("Traffic data", "Where LAIA gets airline schedules. AIG aircraft models are always used for the planes themselves.");
        p.Controls.Add(ServerBanner());
        p.Controls.Add(FieldTitle("Airline schedules come from"));
        _srcAig = new RadioButton { Text = "AIG flight plans (no key needed)", ForeColor = Theme.Text, AutoSize = true, Font = new Font("Segoe UI", 10.5f), Checked = true, Margin = new Padding(0, 0, 0, 4) };
        _srcAirlabs = new RadioButton { Text = "AirLabs live schedules (your key; falls back to AIG automatically)", ForeColor = Theme.Text, AutoSize = true, Font = new Font("Segoe UI", 10.5f) };
        p.Controls.Add(_srcAig);
        p.Controls.Add(_srcAirlabs);

        p.Controls.Add(FieldTitle("AirLabs API key"));
        _airlabs = Input(secret: true);
        var testA = Theme.SecondaryButton("Test");
        var removeA = Theme.SecondaryButton("Remove");
        removeA.Margin = new Padding(8, 0, 0, 0);
        var resA = Result();
        _airlabsHint = Hint("");
        testA.Click += async (_, _) => await TestAsync("airlabs", _airlabs.Text, resA, testA);
        removeA.Click += (_, _) => { _airlabsRemove = true; _airlabs.Text = ""; _airlabsHint.Text = "Will be removed when you save."; };
        p.Controls.Add(Row(_airlabs, testA, removeA));
        p.Controls.Add(_airlabsHint);
        p.Controls.Add(resA);

        p.Controls.Add(FieldTitle("FlightAware AeroAPI key (optional)"));
        _aeroapi = Input(secret: true);
        var testF = Theme.SecondaryButton("Test");
        var removeF = Theme.SecondaryButton("Remove");
        removeF.Margin = new Padding(8, 0, 0, 0);
        var resF = Result();
        _aeroapiHint = Hint("");
        testF.Click += async (_, _) => await TestAsync("aeroapi", _aeroapi.Text, resF, testF);
        removeF.Click += (_, _) => { _aeroapiRemove = true; _aeroapi.Text = ""; _aeroapiHint.Text = "Will be removed when you save."; };
        p.Controls.Add(Row(_aeroapi, testF, removeF));
        p.Controls.Add(_aeroapiHint);
        p.Controls.Add(resF);

        p.Controls.Add(Hint($"Keys are stored encrypted on your LAIA Server PC{(_server is null ? "" : $" ({_server.Machine})")}, not on this PC. Each Test button uses one call on that service.", top: 14));
        _serverControls.AddRange(new Control[] { _srcAig, _srcAirlabs, _airlabs, testA, removeA, _aeroapi, testF, removeF });
        return p;
    }

    private Control BuildConnection()
    {
        var p = Page("Connection", "LAIA Sim finds your LAIA Server automatically.");
        p.Controls.Add(FieldTitle("LAIA Server"));
        var status = _server is null
            ? "Not connected. Make sure the ATC PC is on and LAIA Server is installed."
            : $"{_server.Machine} at {_server.Address}  ·  version {_server.Version}  ·  speech engine {(_speech == "ready" ? "ready" : _speech)}";
        p.Controls.Add(new Label { Text = status, ForeColor = _server is null ? Theme.Warn : Theme.Ok, Font = new Font("Segoe UI", 10.5f), AutoSize = true, MaximumSize = new Size(600, 0) });
        var search = Theme.SecondaryButton("Search again");
        search.Click += async (_, _) => { if (_rediscover is not null) { search.Enabled = false; await _rediscover(); search.Enabled = true; } };
        var page = Theme.SecondaryButton("Open control page");
        page.Margin = new Padding(10, 0, 0, 0);
        page.Enabled = _server is not null;
        page.Click += (_, _) => { if (_server is not null) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_server.BaseUrl) { UseShellExecute = true }); } catch { } };
        var row = Row(search, page);
        row.Margin = new Padding(0, 12, 0, 0);
        p.Controls.Add(row);
        _autostart = Check("Start LAIA Sim when Windows starts", ReadAutostart());
        _autostart.Margin = new Padding(0, 24, 0, 0);
        p.Controls.Add(_autostart);
        return p;
    }

    private Control BuildAbout()
    {
        var p = Page("About", "LAIA - Local AI ATC. AI traffic and air traffic control for your flight simulator.");
        p.Controls.Add(new Label { Text = $"LAIA Sim version {Application.ProductVersion.Split('+')[0]}", ForeColor = Theme.Text, AutoSize = true, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(0, 12, 0, 0) });
        var logs = Theme.SecondaryButton("Open log folder");
        logs.Margin = new Padding(0, 14, 0, 0);
        logs.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", Log.Dir); } catch { } };
        p.Controls.Add(logs);
        p.Controls.Add(Hint("Airspace data includes VATGlasses (CC BY-NC-SA 4.0) where available.", top: 20));
        return p;
    }

    private Label ServerBanner()
    {
        var l = new Label
        {
            Text = "Connect to LAIA Server to change these settings.", ForeColor = Theme.Warn, AutoSize = true, Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(0, 0, 0, 4), Visible = false,
        };
        _serverBanner = l;
        return l;
    }

    // ------------------------------------------------------------------ behaviour

    private static string? SelectedId(DarkCombo c) => (c.SelectedItem as AudioDevices.Device)?.Id;

    private async Task PlayTestAsync()
    {
        var prevDev = Player.DeviceId;
        var prevVol = Player.Volume;
        Player.DeviceId = SelectedId(_spkBox);
        Player.Volume = _volume.Value / 100f;
        try
        {
            byte[]? audio = null;
            if (_server is not null && _speech == "ready")
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    using var c = new StringContent("{\"text\":\"Radio check, read you five by five.\",\"radio\":true}", System.Text.Encoding.UTF8, "application/json");
                    using var r = await http.PostAsync(_server.BaseUrl + "/api/say", c);
                    if (r.IsSuccessStatusCode) audio = await r.Content.ReadAsByteArrayAsync();
                }
                catch { }
            }
            await Player.PlayAsync(audio ?? Player.Click(true));
        }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't play on that device: " + ex.Message, "LAIA Sim"); }
        finally { Player.DeviceId = prevDev; Player.Volume = prevVol; }
    }

    private async Task TestAsync(string service, string typed, Label result, Button btn)
    {
        if (_link is null || _server is null) { result.ForeColor = Theme.Warn; result.Text = "Connect to LAIA Server first."; return; }
        btn.Enabled = false;
        result.ForeColor = Theme.Muted;
        result.Text = "Testing…";
        var (ok, msg) = await _link.TestServiceAsync(_server, service, string.IsNullOrWhiteSpace(typed) ? null : typed.Trim());
        result.ForeColor = ok ? Theme.Ok : Theme.Bad;
        result.Text = msg;
        btn.Enabled = true;
    }

    private async Task LoadServerSettingsAsync()
    {
        if (_link is null || _server is null) { SetServerEnabled(false); return; }
        using var doc = await _link.GetSettingsAsync(_server);
        if (doc is null) { SetServerEnabled(false); return; }
        ApplyServerSettings(doc);
    }

    private void ApplyServerSettings(JsonDocument doc)
    {
        var r = doc.RootElement;
        _loadedSimbrief = r.TryGetProperty("simbrief_user", out var sb) ? sb.GetString() ?? "" : "";
        _loadedSource = r.TryGetProperty("schedule_source", out var src) ? src.GetString() ?? "aig" : "aig";
        _simbrief.Text = _loadedSimbrief;
        _srcAirlabs.Checked = _loadedSource == "airlabs";
        _srcAig.Checked = !_srcAirlabs.Checked;
        _airlabsHint.Text = KeyHint(r, "airlabs");
        _aeroapiHint.Text = KeyHint(r, "aeroapi");
        SetServerEnabled(true);
    }

    private static string KeyHint(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var k) || !k.TryGetProperty("set", out var set) || !set.GetBoolean())
            return "No key saved.";
        return $"Saved ({k.GetProperty("hint").GetString()}). Leave blank to keep it, or type a new key to replace it.";
    }

    private void SetServerEnabled(bool on)
    {
        foreach (var c in _serverControls) c.Enabled = on;
        foreach (var page in _pages.Values)
            foreach (Control c in page.Controls)
                if (c is Label l && l.Text.StartsWith("Connect to LAIA Server")) l.Visible = !on;
    }

    private async Task SaveAsync()
    {
        // Local settings
        _s.MicDeviceId = SelectedId(_micBox);
        _s.SpeakerDeviceId = SelectedId(_spkBox);
        _s.Volume = _volume.Value;
        _s.RadioClicks = _clicks.Checked;
        _s.Ptt = _ptt;
        _s.Save();
        WriteAutostart(_autostart.Checked);

        // Server settings: only what changed
        if (_link is not null && _server is not null && _simbrief.Enabled)
        {
            var changes = new Dictionary<string, string>();
            if (_simbrief.Text.Trim() != _loadedSimbrief) changes["simbrief_user"] = _simbrief.Text.Trim();
            var src = _srcAirlabs.Checked ? "airlabs" : "aig";
            if (src != _loadedSource) changes["schedule_source"] = src;
            if (_airlabs.Text.Trim().Length > 0) changes["airlabs_key"] = _airlabs.Text.Trim();
            else if (_airlabsRemove) changes["airlabs_key"] = "";
            if (_aeroapi.Text.Trim().Length > 0) changes["aeroapi_key"] = _aeroapi.Text.Trim();
            else if (_aeroapiRemove) changes["aeroapi_key"] = "";
            if (changes.Count > 0 && !await _link.PutSettingsAsync(_server, changes))
            {
                MessageBox.Show(this, "Your audio settings were saved, but LAIA Server didn't accept the other changes. Check that the ATC PC is on and try again.", "LAIA Sim", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool ReadAutostart()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("LAIA Sim") is not null; } catch { return false; }
    }

    private static void WriteAutostart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("LAIA Sim", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("LAIA Sim", false);
        }
        catch (Exception ex) { Log.Write("Autostart change failed: " + ex.Message); }
    }

    /// <summary>Automated build only: saves a PNG of each page.</summary>
    public void SaveScreenshots(string dir, string prefix)
    {
        Directory.CreateDirectory(dir);
        foreach (var name in PageNames.ToList())
        {
            ShowPage(name);
            Refresh();
            Application.DoEvents();
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(Path.Combine(dir, $"{prefix}-{name.ToLowerInvariant().Replace(' ', '-')}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    // ------------------------------------------------------------------ small custom controls

    private sealed class NavItem : Control
    {
        private readonly string _glyph;
        public bool Selected { get; set; }
        private bool _hover;

        public NavItem(string text, string glyph)
        {
            Text = text;
            _glyph = glyph;
            Height = 38;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            MouseEnter += (_, _) => { _hover = true; Invalidate(); };
            MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            if (Selected || _hover)
            {
                using var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8);
                using var b = new SolidBrush(Selected ? Color.FromArgb(28, 40, 66) : Color.FromArgb(18, 26, 44));
                g.FillPath(b, path);
            }
            if (Selected) { using var a = new SolidBrush(Theme.Accent); g.FillRectangle(a, 0, 10, 3, Height - 20); }
            using var icon = new Font("Segoe Fluent Icons", 11f);
            using var iconFallback = new Font("Segoe MDL2 Assets", 11f);
            var f = icon.Name == "Segoe Fluent Icons" ? icon : iconFallback;
            TextRenderer.DrawText(g, _glyph, f, new Point(14, 11), Selected ? Theme.Accent : Theme.Muted);
            using var font = new Font("Segoe UI", 10.5f, Selected ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(g, Text, font, new Point(42, 9), Selected ? Theme.Text : Theme.Muted);
        }
    }

    private sealed class DarkCombo : ComboBox
    {
        public DarkCombo(List<AudioDevices.Device> items, string? selectedId)
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 10.5f);
            ItemHeight = 24;
            Width = 420;
            Margin = new Padding(0);
            foreach (var i in items) Items.Add(i);
            SelectedIndex = Math.Max(0, items.FindIndex(i => i.Id == selectedId));
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using var b = new SolidBrush(selected ? Color.FromArgb(28, 40, 66) : Theme.Panel);
            e.Graphics.FillRectangle(b, e.Bounds);
            var item = (AudioDevices.Device)Items[e.Index];
            TextRenderer.DrawText(e.Graphics, item.Name, Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private sealed class LevelMeter : Control
    {
        public float Level { get; set; }
        public LevelMeter() => SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            using (var track = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            using (var tb = new SolidBrush(Color.FromArgb(28, 39, 66))) g.FillPath(tb, track);
            // Speech peaks around 0.1-0.5; scale so normal talking lands mid-bar.
            var v = (float)Math.Clamp(Math.Sqrt(Level) * 1.1, 0, 1);
            var w = (int)(v * (Width - 1));
            if (w < Height) return;
            var color = v > 0.9 ? Theme.Bad : v > 0.25 ? Theme.Ok : Theme.Warn;
            using var bar = Rounded(new Rectangle(0, 0, w, Height - 1), Height / 2);
            using var bb = new SolidBrush(color);
            g.FillPath(bb, bar);
        }
    }

    private sealed class Slider : Control
    {
        private int _value;
        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }
        public Slider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
            MouseDown += (_, e) => Set(e.X);
            MouseMove += (_, e) => { if (e.Button == MouseButtons.Left) Set(e.X); };
        }

        private void Set(int x) => Value = (int)Math.Round((x - 9) * 100.0 / Math.Max(1, Width - 60));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Theme.Bg);
            var trackW = Width - 60;
            var y = Height / 2;
            using (var t = new SolidBrush(Color.FromArgb(28, 39, 66))) g.FillRectangle(t, 9, y - 2, trackW, 4);
            var x = 9 + trackW * Value / 100;
            using (var a = new SolidBrush(Theme.Accent)) g.FillRectangle(a, 9, y - 2, x - 9, 4);
            using (var th = new SolidBrush(Theme.Text)) g.FillEllipse(th, x - 8, y - 8, 16, 16);
            TextRenderer.DrawText(g, $"{Value}%", new Font("Segoe UI", 10f), new Point(Width - 44, y - 9), Theme.Muted);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
