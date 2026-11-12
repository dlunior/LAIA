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


/// <summary>
/// Smart traffic notices (M8): a small always-on-top window that shows only what matters now — traffic on final or on
/// the runway, traffic crossing downfield, a runway or taxiway about to be busy, traffic waiting for you, and your place
/// in the arrival sequence. Never instructions. Warnings in amber, the rest in blue; empty when there's nothing.
/// </summary>
internal sealed class NoticesForm : Form
{
    private readonly FlowLayoutPanel _list;
    private readonly Label _empty;

    public NoticesForm()
    {
        Theme.Apply(this);
        Text = "LAIA - Traffic notices";
        Icon = Theme.AppIcon(32);
        StartPosition = FormStartPosition.Manual;
        Size = new Size(460, 190);
        MinimumSize = new Size(300, 110);
        TopMost = true;
        ShowInTaskbar = false;
        _list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Bg, Padding = new Padding(10, 8, 6, 8) };
        _empty = new Label { Text = "No traffic notices", AutoSize = true, ForeColor = Theme.Muted, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(4, 6, 4, 4) };
        _list.Controls.Add(_empty);
        Controls.Add(_list);
        Theme.Done(this);
        var placed = false;
        Shown += (_, _) =>
        {
            if (placed) return;
            placed = true;
            var wa = Screen.FromControl(this).WorkingArea;
            Location = new Point(wa.Right - Width - 24, wa.Top + 24);
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        _list.Resize += (_, _) => { foreach (Control c in _list.Controls) if (c is Label l && l != _empty) l.MaximumSize = new Size(_list.ClientSize.Width - 24, 0); };
    }

    private string _shown = "";

    public void Show(List<Laia.Common.SimConnect.NoticeDto> notices)
    {
        if (IsDisposed) return;
        var key = string.Join("|", notices.Select(n => n.Id + n.Text));
        if (key == _shown) return;
        _shown = key;
        _list.SuspendLayout();
        _list.Controls.Clear();
        if (notices.Count == 0) _list.Controls.Add(_empty);
        foreach (var n in notices)
            _list.Controls.Add(new Label
            {
                Text = n.Text, AutoSize = true, MaximumSize = new Size(Math.Max(200, _list.ClientSize.Width - 24), 0),
                ForeColor = n.Warning ? Color.FromArgb(245, 165, 36) : Color.FromArgb(76, 195, 255),
                Font = new Font("Segoe UI", 11.5f, FontStyle.Bold), Margin = new Padding(4, 4, 4, 6),
            });
        _list.ResumeLayout();
    }
}
