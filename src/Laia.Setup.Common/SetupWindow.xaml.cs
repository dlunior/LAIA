using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Laia.Setup.Common;

/// <summary>
/// The shared modern installer window: sidebar with logo and steps, page area, and a footer with
/// Back / Next / Cancel. Each installer feeds it pages built with <see cref="Ui"/>.
/// </summary>
public partial class SetupWindow : Window
{
    private string[] _steps = Array.Empty<string>();
    private int _current;
    public event Action? NextClicked;
    public event Action? BackClicked;
    /// <summary>Return false to keep the window open.</summary>
    public Func<bool>? CanClose { get; set; }
    public bool Busy { get; set; }

    public SetupWindow()
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = v is null ? "" : $"Version {v.ToString(3)}";
    }

    public string Subtitle { set => ProductSubtitle.Text = value; }

    public void SetSteps(params string[] steps)
    {
        _steps = steps;
        RenderSteps();
    }

    public void ShowPage(int stepIndex, FrameworkElement page)
    {
        _current = stepIndex;
        RenderSteps();
        PageHost.Content = page;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        PageShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    public void SetButtons(string? next, bool nextEnabled = true, bool back = false, bool cancel = true, string cancelText = "Cancel")
    {
        NextButton.Visibility = next is null ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = next ?? "";
        NextButton.IsEnabled = nextEnabled;
        BackButton.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = cancel ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = cancelText;
    }

    public bool NextEnabled { set => NextButton.IsEnabled = value; }

    private void RenderSteps()
    {
        StepsPanel.Children.Clear();
        for (var i = 0; i < _steps.Length; i++)
        {
            var done = i < _current;
            var active = i == _current;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var accent = (Brush)FindResource("Accent");
            var circle = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1.5),
                BorderBrush = done || active ? accent : (Brush)FindResource("Faint"),
                Background = done ? accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            circle.Child = new TextBlock
            {
                Text = done ? "" : (i + 1).ToString(),
                FontFamily = done ? (FontFamily)FindResource("SymbolFont") : (FontFamily)FindResource("UiFont"),
                FontSize = done ? 12 : 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = done ? new SolidColorBrush(Color.FromRgb(4, 18, 31)) : active ? accent : (Brush)FindResource("Faint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(circle);

            var label = new TextBlock
            {
                Text = _steps[i],
                FontSize = 14.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? (Brush)FindResource("Text") : done ? (Brush)FindResource("Muted") : (Brush)FindResource("Faint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            StepsPanel.Children.Add(row);

            if (i < _steps.Length - 1)
            {
                StepsPanel.Children.Add(new Rectangle
                {
                    Width = 1.5, Height = 16, Fill = done ? accent : (Brush)FindResource("Line"),
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(13.25, -2, 0, 4),
                });
            }
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnNext(object sender, RoutedEventArgs e) => NextClicked?.Invoke();
    private void OnBack(object sender, RoutedEventArgs e) => BackClicked?.Invoke();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (CanClose is not null && !CanClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>Saves a PNG of the window (used by the automated build to show what each page looks like).</summary>
    public void SaveScreenshot(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Ceiling(Frame.ActualWidth + Frame.Margin.Left + Frame.Margin.Right);
        var h = (int)Math.Ceiling(Frame.ActualHeight + Frame.Margin.Top + Frame.Margin.Bottom);
        var target = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(target);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
