using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Laia.Setup.Common;

public enum RowState { Pending, Working, Ok, Warn, Fail, Skipped }

/// <summary>Small factory for consistently styled page content.</summary>
public static class Ui
{
    private static ResourceDictionary? _theme;
    private static ResourceDictionary Theme => _theme ??= new ResourceDictionary
    {
        Source = new Uri("pack://application:,,,/Laia.Setup.Common;component/Theme.xaml", UriKind.Absolute),
    };
    public static T Res<T>(string key) => (T)Theme[key];
    public static Brush Paint(string key) => Res<Brush>(key);

    public static StackPanel Page(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static TextBlock Eyebrow(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Paint("Accent"),
        Margin = new Thickness(0, 6, 0, 8),
    };

    public static TextBlock Title(string text) => new()
    {
        Text = text, FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Paint("Text"),
        FontFamily = Res<FontFamily>("DisplayFont"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
    };

    public static TextBlock Body(string text, double top = 0) => new()
    {
        Text = text, FontSize = 15, Foreground = Paint("Muted"), TextWrapping = TextWrapping.Wrap,
        LineHeight = 23, Margin = new Thickness(0, top, 0, 0),
    };

    public static TextBlock Small(string text, double top = 0) => new()
    {
        Text = text, FontSize = 12.5, Foreground = Paint("Faint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>A rounded card holding feature lines like "✓ Runs quietly in the background".</summary>
    public static Border FeatureCard(params (string glyph, string title, string text)[] items)
    {
        var sp = new StackPanel();
        foreach (var (glyph, title, text) in items)
        {
            var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var icon = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(0x26, 0x38, 0xBD, 0xF8)),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = glyph, FontFamily = Res<FontFamily>("SymbolFont"), FontSize = 15, Foreground = Paint("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            g.Children.Add(icon);
            var t = new StackPanel();
            t.Children.Add(new TextBlock { Text = title, Foreground = Paint("Text"), FontWeight = FontWeights.SemiBold, FontSize = 14.5 });
            t.Children.Add(new TextBlock { Text = text, Foreground = Paint("Muted"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            sp.Children.Add(g);
        }
        return Card(sp, top: 22);
    }

    public static Border Card(UIElement child, double top = 16) => new()
    {
        Background = Paint("Panel"), BorderBrush = Paint("Line"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 14, 20, 14), Margin = new Thickness(0, top, 0, 0), Child = child,
    };

    public static CheckBox Check(string text, bool isChecked) => new()
    {
        Content = text, IsChecked = isChecked, Style = Res<Style>("ModernCheck"), Margin = new Thickness(0, 14, 0, 0),
    };

    public static Button Link(string text, Action onClick)
    {
        var b = new Button { Content = text, Style = Res<Style>("LinkButton"), Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Secondary(string text, Action onClick)
    {
        var b = new Button { Content = text, Style = Res<Style>("SecondaryButton"), Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };
}

/// <summary>A status line with an icon: spinner while working, then a check, warning or cross.</summary>
public sealed class StatusRow : Border
{
    private readonly Border _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    public RowState State { get; private set; }

    public StatusRow(string title, string detail = "")
    {
        Padding = new Thickness(0, 9, 0, 9);
        BorderBrush = Ui.Paint("Line");
        BorderThickness = new Thickness(0, 0, 0, 1);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        _icon = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
        g.Children.Add(_icon);
        var text = new StackPanel();
        _title = new TextBlock { Text = title, FontSize = 14.5, Foreground = Ui.Paint("Text"), TextWrapping = TextWrapping.Wrap };
        _detail = new TextBlock { Text = detail, FontSize = 13, Foreground = Ui.Paint("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(_title);
        text.Children.Add(_detail);
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        Child = g;
        Set(RowState.Pending, detail);
    }

    public void Set(RowState state, string? detail = null)
    {
        State = state;
        if (detail is not null) _detail.Text = detail;
        _detail.Visibility = string.IsNullOrEmpty(_detail.Text) ? Visibility.Collapsed : Visibility.Visible;
        _title.Foreground = state == RowState.Pending ? Ui.Paint("Muted") : Ui.Paint("Text");
        _icon.Child = null;
        _icon.Background = Brushes.Transparent;
        _icon.BorderThickness = new Thickness(0);
        switch (state)
        {
            case RowState.Pending:
                _icon.BorderBrush = Ui.Paint("Faint");
                _icon.BorderThickness = new Thickness(1.5);
                break;
            case RowState.Working:
                _icon.Child = Spinner();
                break;
            case RowState.Ok: Glyph("", "Ok"); break;
            case RowState.Warn: Glyph("", "Warn"); break;
            case RowState.Fail: Glyph("", "Bad"); break;
            case RowState.Skipped: Glyph("", "Faint"); break;
        }
    }

    public string Title { set => _title.Text = value; }

    private void Glyph(string glyph, string colorKey)
    {
        var c = ((SolidColorBrush)Ui.Paint(colorKey)).Color;
        _icon.Background = new SolidColorBrush(Color.FromArgb(0x2A, c.R, c.G, c.B));
        _icon.Child = new TextBlock
        {
            Text = glyph, FontFamily = Ui.Res<FontFamily>("SymbolFont"), FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = Ui.Paint(colorKey), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static UIElement Spinner()
    {
        var arc = new Ellipse
        {
            Width = 22, Height = 22, Stroke = Ui.Paint("Accent"), StrokeThickness = 2.5,
            StrokeDashArray = new DoubleCollection { 6, 4 }, StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var rot = new RotateTransform();
        arc.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }
}

/// <summary>Overall progress: a headline, a detail line and a bar.</summary>
public sealed class ProgressBlock : StackPanel
{
    private readonly TextBlock _headline;
    private readonly TextBlock _percent;
    private readonly TextBlock _detail;
    private readonly ProgressBar _bar;

    public ProgressBlock()
    {
        Margin = new Thickness(0, 6, 0, 0);
        var top = new DockPanel();
        _percent = new TextBlock { FontSize = 14, Foreground = Ui.Paint("Muted") };
        DockPanel.SetDock(_percent, Dock.Right);
        top.Children.Add(_percent);
        _headline = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ui.Paint("Text"), TextTrimming = TextTrimming.CharacterEllipsis };
        top.Children.Add(_headline);
        Children.Add(top);
        _bar = new ProgressBar { Style = Ui.Res<Style>("ModernProgress"), Margin = new Thickness(0, 10, 0, 8) };
        Children.Add(_bar);
        _detail = new TextBlock { FontSize = 12.5, Foreground = Ui.Paint("Faint"), TextTrimming = TextTrimming.CharacterEllipsis };
        Children.Add(_detail);
    }

    public void Update(string headline, double? fraction, string detail = "")
    {
        _headline.Text = headline;
        _detail.Text = detail;
        if (fraction is double f)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Clamp(f, 0, 1);
            _percent.Text = $"{Math.Clamp(f, 0, 1) * 100:0}%";
        }
        else
        {
            _bar.IsIndeterminate = true;
            _percent.Text = "";
        }
    }
}
