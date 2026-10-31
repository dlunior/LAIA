using System.Windows;
using System.Windows.Controls;

namespace Laia.Setup.Common;

/// <summary>The "Installing" page: progress bar on top, one status row per step underneath.</summary>
public sealed class InstallPage : StackPanel, IInstallView
{
    private readonly ProgressBlock _progress = new();
    private readonly List<StatusRow> _rows = new();
    private readonly IReadOnlyList<InstallStep> _steps;

    public InstallPage(string title, string subtitle, IReadOnlyList<InstallStep> steps)
    {
        _steps = steps;
        Children.Add(Ui.Eyebrow("Installing"));
        Children.Add(Ui.Title(title));
        Children.Add(Ui.Body(subtitle));
        var progressCard = Ui.Card(_progress, top: 18);
        Children.Add(progressCard);
        var list = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var s in steps)
        {
            var r = new StatusRow(s.Title);
            _rows.Add(r);
            list.Children.Add(r);
        }
        Children.Add(new ScrollViewer { Content = list, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) });
        _progress.Update("Getting ready…", 0);
    }

    private void Ui_(Action a) => Dispatcher.BeginInvoke(a);

    public void StepStarted(int index, InstallStep step) => Ui_(() =>
    {
        _rows[index].Set(RowState.Working, "");
        _rows[index].BringIntoView();
        _progress.Update(step.Title + "…", null);
    });

    public void StepProgress(int index, double overall, double? stepFraction, string detail) => Ui_(() =>
    {
        _progress.Update(_steps[index].Title + "…", overall, detail);
        if (!string.IsNullOrEmpty(detail) && _rows[index].State == RowState.Working) _rows[index].Set(RowState.Working, detail);
    });

    public void StepFinished(int index, RowState state, string? detail) => Ui_(() => _rows[index].Set(state, detail ?? ""));

    public void Complete(bool ok) => Ui_(() => _progress.Update(ok ? "All done" : "Setup stopped", ok ? 1 : 0));
}
