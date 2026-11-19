using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AsdLasercraft.App.Controls;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Gcode;

namespace AsdLasercraft.App.Dialogs;

public enum JobConfirmResult { Cancel, Start, Simulate }

/// <summary>
/// "Ready to burn" summary shown before a job starts (or is exported): size, time and distance, the pre-flight
/// check results, and a safety reminder. Errors disable the start button; warnings need a second look.
/// </summary>
public partial class JobConfirmWindow : Window
{
    public JobConfirmResult Result { get; private set; } = JobConfirmResult.Cancel;

    public JobConfirmWindow(Window owner, GcodeJob job, IReadOnlyList<PreflightIssue> issues, string subtitle,
                            string startText = "Start burning", bool safetyNote = true, bool offerSimulate = true)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);

        SubtitleText.Text = subtitle;
        SizeText.Text = job.Bounds.IsEmpty ? "–" : $"{job.Bounds.Width:0.#} × {job.Bounds.Height:0.#} mm";
        TimeText.Text = FormatTime(job.EstimatedTime);
        DistanceText.Text = job.CutDistanceMm >= 1000 ? $"{job.CutDistanceMm / 1000:0.0} m" : $"{job.CutDistanceMm:0} mm";
        StartIcon.Text = startText;
        SafetyBox.Visibility = safetyNote ? Visibility.Visible : Visibility.Collapsed;
        SimulateButton.Visibility = offerSimulate ? Visibility.Visible : Visibility.Collapsed;

        bool hasError = issues.Any(i => i.Severity == PreflightSeverity.Error);
        bool hasWarning = issues.Any(i => i.Severity == PreflightSeverity.Warning);
        if (hasError)
        {
            TitleText.Text = "Can't start yet";
            HeaderIcon.Data = (Geometry)FindResource("I.Error");
            HeaderIcon.Foreground = (Brush)FindResource("Brush.Danger");
            StartButton.IsEnabled = false;
        }
        else if (hasWarning)
        {
            TitleText.Text = "Check before burning";
            StartIcon.Text = startText + " anyway";
        }

        if (issues.Count == 0)
            IssuesPanel.Children.Add(Row(PreflightSeverity.Info, "All checks passed.", success: true));
        foreach (var issue in issues.OrderByDescending(i => i.Severity))
            IssuesPanel.Children.Add(Row(issue.Severity, issue.Message));
    }

    private UIElement Row(PreflightSeverity severity, string message, bool success = false)
    {
        var (icon, brush) = success
            ? ("I.Success", "Brush.Success")
            : severity switch
            {
                PreflightSeverity.Error => ("I.Error", "Brush.Danger"),
                PreflightSeverity.Warning => ("I.Warning", "Brush.Warning"),
                _ => ("I.Info", "Brush.Info"),
            };
        var panel = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
        var glyph = new Glyph { Data = (Geometry)FindResource(icon), Foreground = (Brush)FindResource(brush), Size = 17, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(glyph, Dock.Left);
        panel.Children.Add(glyph);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, severity == PreflightSeverity.Info ? "Brush.TextMuted" : "Brush.Text");
        panel.Children.Add(text);
        return panel;
    }

    public static string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        Result = JobConfirmResult.Start;
        DialogResult = true;
    }

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        Result = JobConfirmResult.Simulate;
        DialogResult = true;
    }
}
