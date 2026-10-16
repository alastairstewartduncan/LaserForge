using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;

namespace AsdLasercraft.App.Dialogs;

/// <summary>
/// "Describe it, get vectors": the user types what they want, Claude draws it, the preview shows how it will burn,
/// and they can ask for changes before adding it to the project as ordinary editable shapes.
/// </summary>
public partial class DesignWindow : Window
{
    private static readonly (string Label, string Prompt, DesignStyle Style, double Width)[] Examples =
    {
        ("Family sign", "A rustic family sign with \"The Duncans\" in a bold serif, \"Est. 2010\" underneath, and a simple pine tree on each side", DesignStyle.Engrave, 150),
        ("Coaster", "A round coaster with a geometric mountain range and a moon, with a cut-out circle around it", DesignStyle.EngraveAndCut, 90),
        ("Pet tag", "A bone-shaped pet tag with the name \"Biscuit\" in a friendly rounded font and a small paw print", DesignStyle.EngraveAndCut, 45),
        ("Bookmark", "A tall bookmark with a mandala at the top and a cut outline with a hole for a tassel", DesignStyle.EngraveAndCut, 40),
        ("Line drawing", "A minimalist single-line drawing of a hummingbird drinking from a flower", DesignStyle.LineArt, 80),
        ("Badge", "A circular badge with \"HANDMADE\" curved around the top edge, \"WITH LOVE\" around the bottom and a heart in the middle", DesignStyle.Engrave, 60),
    };

    private readonly DesignGenerator _generator;
    private CancellationTokenSource? _cts;
    private DesignStyle _resultStyle;
    private bool _showingProblem;

    /// <summary>The design to add, set when the user clicks Add.</summary>
    public DesignResult? Result { get; private set; }
    public DesignStyle ResultStyle => _resultStyle;

    public DesignWindow(Window owner, AiServices ai)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);
        _generator = new DesignGenerator(ai.Client, WpfTextOutliner.Instance);

        foreach (var ex in Examples)
        {
            var b = new Button { Content = ex.Label, ToolTip = ex.Prompt };
            b.SetResourceReference(StyleProperty, "ChipButton");
            b.Click += (_, _) =>
            {
                PromptBox.Text = ex.Prompt;
                WidthBox.Text = ex.Width.ToString(CultureInfo.CurrentCulture);
                HeightBox.Text = "";
                (ex.Style switch { DesignStyle.LineArt => StyleLine, DesignStyle.EngraveAndCut => StyleBoth, _ => StyleEngrave }).IsChecked = true;
                PromptBox.Focus();
                PromptBox.CaretIndex = PromptBox.Text.Length;
            };
            ExamplesPanel.Children.Add(b);
        }
        Loaded += (_, _) => PromptBox.Focus();
        Closed += (_, _) => _cts?.Cancel();
    }

    private DesignStyle SelectedStyle =>
        StyleLine.IsChecked == true ? DesignStyle.LineArt : StyleBoth.IsChecked == true ? DesignStyle.EngraveAndCut : DesignStyle.Engrave;

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        GenerateButton.IsEnabled = PromptBox.Text.Trim().Length >= 3 && _cts == null;

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNum(WidthBox.Text, out var width) || width < 5 || width > 2000)
        {
            MessageBox.Show(this, "Enter a width between 5 and 2000 mm.", Title);
            return;
        }
        double height = TryNum(HeightBox.Text, out var h) ? Math.Clamp(h, 0, 2000) : 0;
        var style = SelectedStyle;
        var request = new DesignRequest
        {
            Description = PromptBox.Text,
            WidthMm = width,
            HeightMm = height,
            Style = style,
            AvailableFonts = AiServices.InstalledFonts,
        };
        await RunAsync(ct => _generator.GenerateAsync(request, ct), style, "Drawing your design…");
    }

    private async void Refine_Click(object sender, RoutedEventArgs e)
    {
        var change = RefineBox.Text.Trim();
        if (change.Length == 0 || !_generator.HasDesign) return;
        await RunAsync(ct => _generator.RefineAsync(change, ct), _resultStyle, "Making the change…");
    }

    private void RefineBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Refine_Click(sender, e); e.Handled = true; }
    }

    private async Task RunAsync(Func<CancellationToken, Task<DesignResult>> work, DesignStyle style, string busyText)
    {
        _cts = new CancellationTokenSource();
        _showingProblem = false;
        PlaceholderGlyph.Data = (Geometry)FindResource("I.Sparkle");
        SetBusy(true, busyText);
        try
        {
            var design = await work(_cts.Token);
            _resultStyle = style;
            Preview.RenderStyle = style;
            Preview.Design = design;
            NotesText.Text = string.IsNullOrWhiteSpace(design.Notes) ? "Happy with it? Add it, or ask for a change below." : design.Notes;
            RefinePanel.Visibility = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = true;
            GenerateGlyph.Text = "Start again";
            RefineBox.Clear();
        }
        catch (AiException ex) when (ex.Kind == AiErrorKind.Cancelled) { }
        catch (OperationCanceledException) { }
        catch (AiException ex)
        {
            ShowProblem(ex.Message);
        }
        finally
        {
            _cts = null;
            SetBusy(false, null);
        }
    }

    internal void SetBusy(bool busy, string? text)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = !busy && PromptBox.Text.Trim().Length >= 3;
        RefineButton.IsEnabled = !busy;
        RefineBox.IsEnabled = !busy;
        AddButton.IsEnabled = !busy && Preview.Design != null;
        Cursor = busy ? Cursors.AppStarting : null;
        if (busy)
        {
            PlaceholderPanel.Visibility = Visibility.Visible;
            PlaceholderGlyph.Foreground = (Brush)FindResource("Brush.Accent");
            PlaceholderText.Text = text;
            Preview.Opacity = 0.25;
        }
        else
        {
            Preview.Opacity = 1;
            PlaceholderGlyph.Foreground = (Brush)FindResource(_showingProblem ? "Brush.Warning" : "Brush.TextDim");
            if (Preview.Design != null && !_showingProblem) PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowProblem(string message)
    {
        _showingProblem = true;
        PlaceholderPanel.Visibility = Visibility.Visible;
        PlaceholderGlyph.Data = (Geometry)FindResource("I.Warning");
        PlaceholderText.Text = message;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Preview.Design == null) return;
        Result = Preview.Design;
        DialogResult = true;
    }

    private static bool TryNum(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ---- smoke test hooks
    internal void SmokeShow(string prompt, DesignResult design, DesignStyle style, double widthMm)
    {
        PromptBox.Text = prompt;
        WidthBox.Text = widthMm.ToString(CultureInfo.CurrentCulture);
        (style switch { DesignStyle.LineArt => StyleLine, DesignStyle.EngraveAndCut => StyleBoth, _ => StyleEngrave }).IsChecked = true;
        _resultStyle = style;
        Preview.RenderStyle = style;
        Preview.Design = design;
        NotesText.Text = design.Notes;
        RefinePanel.Visibility = Visibility.Visible;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        AddButton.IsEnabled = true;
        GenerateGlyph.Text = "Start again";
    }
}
