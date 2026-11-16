using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Ai.Library;

namespace AsdLasercraft.App.Dialogs;

/// <summary>
/// "Describe it, get vectors": the user types what they want, the chosen designer agent draws it, a critic agent and
/// built-in checks review it (the designer revising until it passes), the preview shows how it will burn, and the user
/// can compare drafts and ask for changes before adding it to the project as ordinary editable shapes.
/// The same window refines shapes already in the project: it starts from them instead of from a blank brief.
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

    private readonly AiServices _ai;
    private readonly SvgExporter.ShapeExport? _existing;
    private readonly double _bedWidth, _bedHeight;
    private DesignGenerator _generator;
    private CancellationTokenSource? _cts;
    private DesignStyle _resultStyle;
    private bool _showingProblem;

    // designer / skills / review options (built in code into AgentPanel)
    private readonly ComboBox _designerCombo = new() { DisplayMemberPath = "DisplayName", ToolTip = "The designer agent that draws the artwork. Make your own under AI → Agents & skills." };
    private readonly WrapPanel _skillsPanel = new();
    private readonly CheckBox _reviewCheck = new() { Content = "Review and refine before showing me", ToolTip = "A critic agent and built-in checks review each draft, and the designer revises until it passes (up to the limit in settings). Slower and uses more AI credit, but catches missing wording, shapes that won't fill and details too small to burn." };
    private readonly ComboBox _criticCombo = new() { DisplayMemberPath = "Name", Margin = new Thickness(22, 2, 0, 0) };
    private sealed record CriticChoice(Agent? Agent, string Name);

    /// <summary>One version of the design the user can flip back to.</summary>
    private sealed record Draft(string Label, DesignResult Design, string Notes, bool IsOriginal, bool Flagged);
    private readonly List<Draft> _drafts = new();
    private Draft? _shown;

    /// <summary>The design to add, set when the user clicks Add.</summary>
    public DesignResult? Result { get; private set; }
    public DesignStyle ResultStyle => _resultStyle;

    /// <summary>True when the window was opened to refine existing shapes (the result replaces them).</summary>
    public bool IsRefining => _existing != null;

    /// <param name="existing">Shapes from the project to refine, or null to start a new design.</param>
    public DesignWindow(Window owner, AiServices ai, SvgExporter.ShapeExport? existing = null, double bedWidthMm = 0, double bedHeightMm = 0)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);
        _ai = ai;
        _existing = existing;
        _bedWidth = bedWidthMm;
        _bedHeight = bedHeightMm;
        _generator = ai.NewDesigner(ai.DesignerAgent);
        BuildAgentPanel();
        FooterText.Text = ai.Active.KeyRequired || ai.IsFake
            ? $"Your description{(existing != null ? " and the selected shapes are" : " is")} sent to {ai.ProviderName}."
            : "Everything stays on this PC with the provider you chose.";
        if (existing != null) StartFromExisting(existing);

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

    // ------------------------------------------------------------------ designer, skills, review

    private void BuildAgentPanel()
    {
        TextBlock Caption(string text)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(2, 10, 2, 4) };
            t.SetResourceReference(StyleProperty, "Caption");
            return t;
        }
        var lib = _ai.Library;
        var settings = _ai.Settings;

        var designers = lib.AgentsFor(AgentRole.Designer).ToList();
        _designerCombo.ItemsSource = designers;
        _designerCombo.SelectedItem = _ai.DesignerAgent ?? designers.FirstOrDefault();
        if (designers.Count > 0)
        {
            AgentPanel.Children.Add(Caption("DESIGNER"));
            AgentPanel.Children.Add(_designerCombo);
        }

        if (lib.Skills.Count > 0)
        {
            AgentPanel.Children.Add(Caption("EXTRA SKILLS FOR THIS JOB"));
            foreach (var skill in lib.Skills.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
                _skillsPanel.Children.Add(new CheckBox
                {
                    Content = skill.DisplayName, Tag = skill, ToolTip = skill.Description, Margin = new Thickness(0, 0, 12, 2),
                    IsChecked = settings.DesignSkills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase),
                });
            AgentPanel.Children.Add(_skillsPanel);
        }

        AgentPanel.Children.Add(Caption("QUALITY"));
        _reviewCheck.IsChecked = settings.DesignReview;
        AgentPanel.Children.Add(_reviewCheck);
        var critics = lib.AgentsFor(AgentRole.Critic).Select(a => new CriticChoice(a, "Critic: " + a.DisplayName)).ToList();
        critics.Add(new CriticChoice(null, "Built-in checks only (no critic)"));
        _criticCombo.ItemsSource = critics;
        _criticCombo.SelectedItem = critics.FirstOrDefault(c => c.Agent != null && c.Agent == _ai.CriticAgent) ?? critics[0];
        AgentPanel.Children.Add(_criticCombo);
        _reviewCheck.Click += (_, _) => _criticCombo.IsEnabled = _reviewCheck.IsChecked == true;
        _criticCombo.IsEnabled = settings.DesignReview;
    }

    private Agent? SelectedDesigner => _designerCombo.SelectedItem as Agent;
    private Agent? SelectedCritic => (_criticCombo.SelectedItem as CriticChoice)?.Agent;
    private List<Skill> SelectedSkills => _skillsPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (Skill)c.Tag).ToList();

    /// <summary>Remembers the choices for next time.</summary>
    private void SaveChoices()
    {
        var s = _ai.Settings;
        if (SelectedDesigner is { } d) s.DesignerAgent = d.Name;
        if (SelectedCritic is { } c) s.CriticAgent = c.Name;
        s.DesignSkills = SelectedSkills.Select(k => k.Name).ToList();
        s.DesignReview = _reviewCheck.IsChecked == true;
        if (!_ai.IsFake) s.Save();
    }

    /// <summary>Sets the window up to refine shapes from the project instead of drawing from scratch.</summary>
    private void StartFromExisting(SvgExporter.ShapeExport existing)
    {
        Title = "Refine with AI";
        HeaderTitle.Text = "Refine your design";
        HeaderSubtitle.Text = "Say what should change – the designer redraws the selected shapes, and you compare before and after.";
        PromptCaption.Text = "WHAT SHOULD CHANGE?";
        PromptBox.ToolTip = "e.g. make the border thicker, simplify the leaves, add a hanging hole at the top. Put any exact wording in quotes.";
        NewDesignPanel.Visibility = Visibility.Collapsed;
        GenerateGlyph.Text = "Refine design";
        _resultStyle = existing.Style;
        try
        {
            var original = DesignGenerator.Parse("```svg\n" + existing.Svg + "\n```",
                new DesignRequest { WidthMm = Math.Max(existing.Bounds.Width, 1), Style = existing.Style }, WpfTextOutliner.Instance);
            _drafts.Add(new Draft("Original", original, "This is what you have now. Say what should change on the left.", IsOriginal: true, Flagged: false));
            ShowDrafts(_drafts[0]);
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            RefinePanel.Visibility = Visibility.Visible;
            RefineBox.Visibility = Visibility.Collapsed;
            RefineButton.Visibility = Visibility.Collapsed;
        }
        catch (AiException)
        {
            // The preview of the original is a nicety; refining still works without it.
        }
    }

    private DesignRequest ExistingRequest() => new()
    {
        Description = "",
        WidthMm = Math.Max(_existing!.Bounds.Width, 1),
        Style = _existing.Style,
        AvailableFonts = AiServices.InstalledFonts,
    };

    // ------------------------------------------------------------------ drafts

    private void ShowDrafts(Draft select)
    {
        _shown = select;
        DraftsPanel.Children.Clear();
        if (_drafts.Count > 1)
            foreach (var d in _drafts)
            {
                var draft = d;
                var b = new Button { Content = draft.Label, ToolTip = draft.IsOriginal ? "What you started with" : "Show this draft" };
                b.SetResourceReference(StyleProperty, "ChipButton");
                if (ReferenceEquals(draft, select))
                {
                    b.SetResourceReference(ForegroundProperty, "Brush.Accent");
                    b.FontWeight = FontWeights.SemiBold;
                }
                else if (draft.Flagged) b.SetResourceReference(ForegroundProperty, "Brush.Warning");
                b.Click += (_, _) => ShowDrafts(draft);
                DraftsPanel.Children.Add(b);
            }
        Preview.RenderStyle = _resultStyle;
        Preview.Design = select.Design;
        NotesText.Text = select.Notes;
        AddButton.IsEnabled = _cts == null && !select.IsOriginal;
    }

    private static string RoundNotes(RefineRound r, RefineOutcome outcome)
    {
        var parts = new List<string>();
        if (ReferenceEquals(r, outcome.Best)) parts.Add(outcome.Summary.Trim());
        if (r.Critic is { } c)
        {
            if (c.Score >= 0) parts.Add($"Critic's score: {c.Score}/10.");
            if (c.Accept && c.Praise.Length > 0) parts.Add(c.Praise);
            if (!c.Accept && c.Issues.Count > 0) parts.Add("Critic: " + string.Join("; ", c.Issues.Take(3)) + ".");
        }
        var found = r.Findings.Where(f => f.Severity != FindingSeverity.Note).Select(f => f.Message).Take(3).ToList();
        if (found.Count > 0) parts.Add("Checks: " + string.Join(" ", found));
        else if (r.Critic == null || r.Critic.Accept) parts.Add("Passed the built-in checks.");
        if (ReferenceEquals(r, outcome.Best) && outcome.Recurring.Count > 0)
            parts.Add("The same problem kept coming back, so it is worth adding a rule for it to a skill (AI → Agents & skills).");
        return string.Join(" ", parts.Where(p => p.Length > 0));
    }

    private void ShowOutcome(RefineOutcome outcome, bool fresh)
    {
        if (fresh) _drafts.RemoveAll(d => !d.IsOriginal);
        Draft? best = null;
        foreach (var r in outcome.Rounds)
        {
            var label = outcome.Rounds.Count == 1 ? (_drafts.Any(d => !d.IsOriginal) ? $"Draft {_drafts.Count(d => !d.IsOriginal) + 1}" : "Result")
                      : r.Number == 1 ? "First draft" : $"Revision {r.Number - 1}";
            if (ReferenceEquals(r, outcome.Best) && outcome.Rounds.Count > 1) label += " ★";
            var d = new Draft(label, r.Design, RoundNotes(r, outcome), IsOriginal: false, Flagged: !r.Passed);
            _drafts.Add(d);
            if (ReferenceEquals(r, outcome.Best)) best = d;
        }
        ShowDrafts(best ?? _drafts[^1]);
    }

    private DesignStyle SelectedStyle =>
        StyleLine.IsChecked == true ? DesignStyle.LineArt : StyleBoth.IsChecked == true ? DesignStyle.EngraveAndCut : DesignStyle.Engrave;

    private void PromptBox_TextChanged(object sender, TextChangedEventArgs e) =>
        GenerateButton.IsEnabled = PromptBox.Text.Trim().Length >= 3 && _cts == null;

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        DesignRequest request;
        DesignStyle style;
        string brief = PromptBox.Text.Trim();
        if (_existing != null)
        {
            request = ExistingRequest();
            style = _existing.Style;
        }
        else
        {
            if (!TryNum(WidthBox.Text, out var width) || width < 5 || width > 2000)
            {
                MessageBox.Show(this, "Enter a width between 5 and 2000 mm.", Title);
                return;
            }
            double height = TryNum(HeightBox.Text, out var h) ? Math.Clamp(h, 0, 2000) : 0;
            style = SelectedStyle;
            request = new DesignRequest { Description = brief, WidthMm = width, HeightMm = height, Style = style, AvailableFonts = AiServices.InstalledFonts };
        }

        SaveChoices();
        var skills = SelectedSkills;
        var generator = _ai.NewDesigner(SelectedDesigner, skills);
        _generator = generator;
        bool review = _reviewCheck.IsChecked == true;
        var options = _ai.NewRefineOptions(review ? SelectedCritic : null, style, _bedWidth, _bedHeight, skills);
        options.Progress = text => PlaceholderText.Text = text;
        if (!review) options.MaxRevisions = 0;
        var existing = _existing;

        await RunAsync(async ct =>
        {
            var refiner = new DesignRefiner(generator, options);
            if (existing == null) return await refiner.RunAsync(request, ct);
            options.Progress?.Invoke("Redrawing with your change…");
            var first = await generator.ReviseAsync(existing.Svg, brief, request, ct);
            return await refiner.ContinueAsync(first, request, "", ct);
        }, style, existing == null ? "Drawing your design…" : "Redrawing with your change…", fresh: true);
    }

    private async void Refine_Click(object sender, RoutedEventArgs e)
    {
        var change = RefineBox.Text.Trim();
        if (change.Length == 0 || !_generator.HasDesign) return;
        var generator = _generator;
        await RunAsync(async ct =>
        {
            var design = await generator.RefineAsync(change, ct);
            var outcome = new RefineOutcome();
            outcome.Rounds.Add(new RefineRound { Number = 1, Design = design });
            outcome.Best = outcome.Rounds[0];
            outcome.Summary = string.IsNullOrWhiteSpace(design.Notes) ? "Changed as you asked." : design.Notes;
            return outcome;
        }, _resultStyle, "Making the change…", fresh: false);
    }

    private void RefineBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Refine_Click(sender, e); e.Handled = true; }
    }

    private async Task RunAsync(Func<CancellationToken, Task<RefineOutcome>> work, DesignStyle style, string busyText, bool fresh)
    {
        _cts = new CancellationTokenSource();
        _showingProblem = false;
        PlaceholderGlyph.Data = (Geometry)FindResource("I.Sparkle");
        SetBusy(true, busyText);
        bool ok = false;
        try
        {
            var outcome = await work(_cts.Token);
            _resultStyle = style;
            ShowOutcome(outcome, fresh);
            if (string.IsNullOrWhiteSpace(NotesText.Text)) NotesText.Text = "Happy with it? Add it, or ask for a change below.";
            RefinePanel.Visibility = Visibility.Visible;
            RefineBox.Visibility = Visibility.Visible;
            RefineButton.Visibility = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            GenerateGlyph.Text = _existing != null ? "Try a different change" : "Start again";
            RefineBox.Clear();
            ok = true;
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
            if (ok || _shown != null) AddButton.IsEnabled = _shown is { IsOriginal: false };
        }
    }

    internal void SetBusy(bool busy, string? text)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = !busy && PromptBox.Text.Trim().Length >= 3;
        RefineButton.IsEnabled = !busy;
        RefineBox.IsEnabled = !busy;
        AddButton.IsEnabled = !busy && Preview.Design != null && _shown is not { IsOriginal: true };
        _designerCombo.IsEnabled = !busy;
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
        if (Preview.Design == null || _shown is { IsOriginal: true }) return;
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

    /// <summary>Shows a finished review run (several drafts with the critic's notes) without calling any AI.</summary>
    internal void SmokeShowOutcome(string prompt, RefineOutcome outcome, DesignStyle style, double widthMm)
    {
        PromptBox.Text = prompt;
        WidthBox.Text = widthMm.ToString(CultureInfo.CurrentCulture);
        (style switch { DesignStyle.LineArt => StyleLine, DesignStyle.EngraveAndCut => StyleBoth, _ => StyleEngrave }).IsChecked = true;
        _resultStyle = style;
        ShowOutcome(outcome, fresh: true);
        RefinePanel.Visibility = Visibility.Visible;
        PlaceholderPanel.Visibility = Visibility.Collapsed;
        GenerateGlyph.Text = "Start again";
    }
}
