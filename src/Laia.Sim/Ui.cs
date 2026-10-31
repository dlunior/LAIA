using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Laia.Sim;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(13, 18, 32);
    public static readonly Color Panel = Color.FromArgb(22, 30, 50);
    public static readonly Color Line = Color.FromArgb(38, 50, 80);
    public static readonly Color Text = Color.FromArgb(232, 238, 252);
    public static readonly Color Muted = Color.FromArgb(141, 155, 187);
    public static readonly Color Accent = Color.FromArgb(56, 189, 248);
    public static readonly Color Ok = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(251, 191, 36);
    public static readonly Color Bad = Color.FromArgb(248, 113, 113);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Form f)
    {
        f.BackColor = Bg;
        f.ForeColor = Text;
        f.Font = new Font("Segoe UI", 10f);
        f.HandleCreated += (_, _) =>
        {
            var on = 1;
            DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)); // dark title bar
            var round = 2;
            DwmSetWindowAttribute(f.Handle, 33, ref round, sizeof(int)); // rounded corners (Win 11)
        };
    }

    public static Button PrimaryButton(string text) => MakeButton(text, Accent, Color.FromArgb(4, 18, 31));
    public static Button SecondaryButton(string text) => MakeButton(text, Panel, Text);

    private static Button MakeButton(string text, Color back, Color fore)
    {
        var b = new Button
        {
            Text = text, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 10f), AutoSize = true, Padding = new Padding(14, 6, 14, 6),
            Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = back == Panel ? Line : back;
        b.FlatAppearance.BorderSize = 1;
        return b;
    }

    /// <summary>Tray icon: the LAIA logo with a coloured status light in the corner.</summary>
    public static Icon StatusIcon(Color light)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var baseIcon = AppIcon(32)) g.DrawIcon(baseIcon, new Rectangle(0, 0, 32, 32));
            using var ring = new SolidBrush(Color.FromArgb(13, 18, 32));
            using var fill = new SolidBrush(light);
            g.FillEllipse(ring, 17, 17, 15, 15);
            g.FillEllipse(fill, 19, 19, 11, 11);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public static Icon AppIcon(int size)
    {
        try
        {
            var path = Environment.ProcessPath ?? Application.ExecutablePath;
            using var big = Icon.ExtractAssociatedIcon(path);
            if (big is not null) return new Icon(big, size, size);
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }
}
