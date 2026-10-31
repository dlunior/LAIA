namespace Laia.Sim;

/// <summary>"Press the button you want to use for push-to-talk." Works with keyboard, mouse side buttons and joysticks.</summary>
internal sealed class PttSetupForm : Form
{
    private readonly Label _detected;
    private readonly Button _save;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 20 };
    private HashSet<PttBinding> _baseline = new();
    public PttBinding? Result { get; private set; }

    public PttSetupForm(PttBinding? current)
    {
        Theme.Apply(this);
        Text = "LAIA Sim - Push-to-talk";
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(520, 300);
        TopMost = true;
        KeyPreview = true;

        var title = new Label { Text = "Choose your push-to-talk button", Font = new Font("Segoe UI Semibold", 16f), AutoSize = true, Location = new Point(28, 24) };
        var body = new Label
        {
            Text = "Press and release the key, mouse button or joystick/yoke button you want to hold while talking to ATC.",
            ForeColor = Theme.Muted, Location = new Point(30, 68), Size = new Size(460, 48),
        };
        var card = new Panel { BackColor = Theme.Panel, Location = new Point(30, 128), Size = new Size(460, 70) };
        _detected = new Label
        {
            Text = current is null ? "Waiting for a button…" : $"Current: {current.Describe()}  -  press a new one to change",
            Font = new Font("Segoe UI Semibold", 12f), ForeColor = Theme.Accent, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        };
        card.Controls.Add(_detected);

        _save = Theme.PrimaryButton("Use this button");
        _save.Enabled = current is not null;
        Result = current;
        _save.Location = new Point(330, 230);
        _save.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        var cancel = Theme.SecondaryButton("Cancel");
        cancel.Location = new Point(222, 230);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        CancelButton = cancel;

        Controls.AddRange(new Control[] { title, body, card, _save, cancel });

        Theme.Done(this);
        Shown += (_, _) => { _baseline = Input.Snapshot(); _poll.Start(); Activate(); };
        _poll.Tick += (_, _) => Poll();
        FormClosed += (_, _) => _poll.Dispose();
    }

    private void Poll()
    {
        var now = Input.Snapshot();
        // Forget buttons from the baseline once they are released, so they can be chosen too.
        _baseline.IntersectWith(now);
        var pressed = now.FirstOrDefault(b => !_baseline.Contains(b) && !(b.Kind == "key" && b.Code is 0x01 or 0x02 or 0x0D));
        if (pressed is null) return;
        _baseline.Add(pressed);
        Result = pressed;
        _detected.Text = pressed.Describe();
        _save.Enabled = true;
    }
}

/// <summary>On-screen transcript of radio calls (you and ATC).</summary>
internal sealed class RadioLogForm : Form
{
    private readonly RichTextBox _box;

    public RadioLogForm()
    {
        Theme.Apply(this);
        Text = "LAIA - Radio";
        Icon = Theme.AppIcon(32);
        StartPosition = FormStartPosition.Manual;
        Size = new Size(520, 300);
        TopMost = true;
        ShowInTaskbar = false;
        MinimumSize = new Size(320, 160);
        _box = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Bg, ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 11f), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
        };
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 8, 10), BackColor = Theme.Bg };
        pad.Controls.Add(_box);
        Controls.Add(pad);
        Theme.DarkScroll(_box);
        Theme.Done(this);
        var placed = false;
        Shown += (_, _) =>
        {
            if (placed) return;
            placed = true;
            var wa = Screen.FromControl(this).WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 24);
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Add(string who, string text, Color color)
    {
        if (IsDisposed) return;
        _box.SelectionStart = _box.TextLength;
        _box.SelectionColor = Theme.Muted;
        _box.SelectionFont = new Font("Segoe UI", 9f);
        _box.AppendText($"{DateTime.Now:HH:mm:ss}  {who}\n");
        _box.SelectionColor = color;
        _box.SelectionFont = new Font("Segoe UI", 11f, who == "ATC" ? FontStyle.Bold : FontStyle.Regular);
        _box.AppendText(text + "\n\n");
        _box.ScrollToCaret();
    }
}
