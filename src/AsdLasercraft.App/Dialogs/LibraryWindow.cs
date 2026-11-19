using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Controls;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Ai.Library;

namespace AsdLasercraft.App.Dialogs;

/// <summary>
/// Agents &amp; skills: create, edit, import and export them, with AI help to draft and improve. Edits apply as you type
/// and are written to the library folder when the window closes. AI proposals are always shown for review first.
/// </summary>
public sealed class LibraryWindow : Window
{
    private readonly AiServices _ai;
    private readonly Func<SvgExporter.ShapeExport?>? _selection;
    private AiLibrary Lib => _ai.Library;

    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly StackPanel _editor = new() { Margin = new Thickness(18, 10, 20, 12) };
    private readonly TextBlock _empty = new() { Text = "Pick a skill or agent on the left, or make a new one.", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _kindLabel = new() { FontSize = 12 };
    private readonly TextBox _title = new();
    private readonly TextBox _description = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false, MaxHeight = 70 };
    private readonly StackPanel _agentPanel = new();
    private readonly ComboBox _role = new();
    private readonly ComboBox _provider = new() { DisplayMemberPath = "Name" };
    private readonly TextBox _model = new() { ToolTip = "Leave empty to use the provider's chosen model." };
    private readonly StackPanel _toolsBlock = new();
    private readonly CheckBox _allTools = new() { Content = "Every tool", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 4) };
    private readonly WrapPanel _tools = new();
    private readonly WrapPanel _skills = new();
    private readonly TextBlock _bodyCaption = new() { Text = "INSTRUCTIONS" };
    private readonly TextBox _body = new()
    {
        AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap, MinHeight = 200, MaxHeight = 420,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6),
    };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private readonly List<Button> _busyButtons = new();
    private readonly Button _improve, _duplicate, _export, _delete;

    private object? _item;
    private bool _loading, _dirty;
    private CancellationTokenSource? _cts;

    private sealed record ProviderChoice(string Id, string Name);
    private static readonly ProviderChoice CurrentProvider = new("", "Whichever provider is in use");

    /// <param name="selection">Exports the user's selected shapes, for "make a skill from this design"; null if nothing suitable is selected.</param>
    public LibraryWindow(Window owner, AiServices ai, Func<SvgExporter.ShapeExport?>? selection = null)
    {
        _ai = ai;
        _selection = selection;
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Agents & skills";
        Owner = owner;
        Icon = owner.Icon;
        Width = 1040; Height = 700; MinWidth = 860; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowTheme.ApplyDark(this);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        // ---- header
        var header = new Border { Padding = new Thickness(22, 14, 22, 14), BorderThickness = new Thickness(0, 0, 0, 1) };
        header.SetResourceReference(Border.BackgroundProperty, "Brush.HeaderGradient");
        header.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        var headText = new StackPanel();
        headText.Children.Add(new TextBlock { Text = "Agents & skills", FontSize = 18, FontWeight = FontWeights.SemiBold });
        headText.Children.Add(Muted("A skill is a reusable pack of know-how (a material, a product, a house style). An agent is a role for the AI: a designer, a critic or an assistant, with the skills and tools you give it."));
        header.Child = headText;
        root.Children.Add(header);

        // ---- body
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var left = new DockPanel { Margin = new Thickness(14, 12, 0, 12) };
        var newRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(newRow, Dock.Top);
        newRow.Children.Add(Chip("+ Skill", "Start a blank skill", (_, _) => NewSkill()));
        newRow.Children.Add(Chip("+ Agent", "Start a blank agent", (_, _) => NewAgent()));
        newRow.Children.Add(Chip("Import…", "Bring in skills or agents made for other AI tools: SKILL.md files and .zip/.skill packs, agent .md files, saved instructions as .txt, or assistant .json", (_, _) => Import()));
        newRow.Children.Add(Chip("Paste", "Import instructions copied from another AI tool (for example a custom assistant's instructions)", (_, _) => Paste()));
        left.Children.Add(newRow);
        var listFrame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = _list, Padding = new Thickness(2) };
        listFrame.SetResourceReference(Border.BackgroundProperty, "Brush.Bg0");
        listFrame.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        left.Children.Add(listFrame);
        body.Children.Add(left);
        _list.SelectionChanged += (_, _) => { if (!_loading) ShowItem((_list.SelectedItem as ListBoxItem)?.Tag); };

        var rightGrid = new Grid();
        Grid.SetColumn(rightGrid, 1);
        body.Children.Add(rightGrid);
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        rightGrid.Children.Add(_empty);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _editor };
        rightGrid.Children.Add(scroll);

        BuildEditor();

        // ---- per-item actions
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _improve = ActionButton("Improve with AI…", "I.Sparkle", "Have the AI review this and propose a clearer, tighter version. You see the changes before anything is saved.", (_, _) => _ = ImproveAsync());
        _duplicate = ActionButton("Duplicate", "I.Duplicate", "Make a copy to change", (_, _) => Duplicate());
        _export = ActionButton("Export…", "I.Export", "Save this in a format other AI tools can use", (_, _) => Export());
        _delete = ActionButton("Delete", "I.Delete", "Remove this from your library", (_, _) => Delete());
        foreach (var b in new[] { _improve, _duplicate, _export, _delete }) actions.Children.Add(b);
        _editor.Children.Add(actions);

        // ---- footer
        var footer = new DockPanel { Margin = new Thickness(16, 0, 20, 16), LastChildFill = true };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        var close = new Button { Content = "Done", MinWidth = 110, IsDefault = false, IsCancel = true };
        close.SetResourceReference(StyleProperty, "PrimaryButton");
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var exportAll = new Button { Content = "Export everything…", Margin = new Thickness(2, 2, 8, 2), ToolTip = "One zip of every skill and agent, for backup or another PC" };
        exportAll.Click += (_, _) => ExportAll();
        DockPanel.SetDock(exportAll, Dock.Right);
        footer.Children.Add(exportAll);
        var draft = ActionButton("Draft with AI…", "I.Sparkle", "Describe the skill or agent you want and the AI writes a first version for you to review", (_, _) => _ = DraftAsync());
        DockPanel.SetDock(draft, Dock.Left);
        footer.Children.Add(draft);
        _status.Margin = new Thickness(12, 0, 12, 0);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        footer.Children.Add(_status);
        _busyButtons.AddRange(new[] { draft, _improve });

        RefreshList(null);
        ShowItem(null);
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            Commit();
            if (_dirty) _ai.SaveLibrary();
        };
    }

    /// <summary>True when anything was changed, so the caller can refresh pickers that list agents.</summary>
    public bool Changed => _dirty;

    // ================================================================== building blocks

    private TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, "Caption");
        return t;
    }

    private TextBlock Muted(string text, double size = 12.5)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Margin = new Thickness(0, 3, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        return t;
    }

    private Button Chip(string text, string tip, RoutedEventHandler click)
    {
        var b = new Button { Content = text, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "ChipButton");
        b.Click += click;
        return b;
    }

    private Button ActionButton(string text, string icon, string tip, RoutedEventHandler click)
    {
        var b = new Button { Content = new Glyph { Data = (Geometry)FindResource(icon), Text = text, Size = 14 }, ToolTip = tip, Margin = new Thickness(0, 0, 8, 0) };
        b.Click += click;
        return b;
    }

    private void BuildEditor()
    {
        _kindLabel.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        _editor.Children.Add(_kindLabel);
        _editor.Children.Add(Caption("NAME"));
        _editor.Children.Add(_title);
        _editor.Children.Add(Caption("WHEN TO USE IT"));
        _editor.Children.Add(_description);

        // agent-only fields
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        void Cell(int col, string caption, FrameworkElement field)
        {
            var sp = new StackPanel();
            sp.Children.Add(Caption(caption));
            sp.Children.Add(field);
            Grid.SetColumn(sp, col);
            grid.Children.Add(sp);
        }
        _role.ItemsSource = new[] { "Assistant (chat)", "Designer", "Critic" };
        _role.ToolTip = "Designers draw artwork from a brief. Critics review a design before you see it. Assistants run the chat and can edit your design with tools.";
        Cell(0, "ROLE", _role);
        Cell(2, "AI PROVIDER", _provider);
        Cell(4, "MODEL", _model);
        _agentPanel.Children.Add(grid);

        _toolsBlock.Children.Add(Caption("TOOLS THIS AGENT MAY USE"));
        _toolsBlock.Children.Add(_allTools);
        _toolsBlock.Children.Add(_tools);
        _toolsBlock.Children.Add(Muted("Unticked tools are refused even if the AI asks for them. It can always look at your project and run the pre-flight check, and no tool can frame, start or stop the laser.", 12));
        _agentPanel.Children.Add(_toolsBlock);

        _agentPanel.Children.Add(Caption("SKILLS IT DRAWS ON"));
        _agentPanel.Children.Add(_skills);
        _editor.Children.Add(_agentPanel);

        _bodyCaption.SetResourceReference(StyleProperty, "Caption");
        _editor.Children.Add(_bodyCaption);
        _body.SetResourceReference(FontFamilyProperty, "Font.Mono");
        _body.FontSize = 12.5;
        _editor.Children.Add(_body);

        foreach (var name in AgentRuntime.KnownToolNames())
        {
            var cb = new CheckBox { Content = name.Replace('_', ' '), Tag = name, Margin = new Thickness(0, 0, 14, 4), MinWidth = 150 };
            cb.Click += (_, _) => Commit();
            _tools.Children.Add(cb);
        }
        _allTools.Click += (_, _) => { foreach (CheckBox cb in _tools.Children) cb.IsEnabled = _allTools.IsChecked != true; Commit(); };

        _title.TextChanged += (_, _) => Commit();
        _title.LostFocus += (_, _) => { if (_item != null) RefreshList(_item); };
        _description.TextChanged += (_, _) => Commit();
        _body.TextChanged += (_, _) => Commit();
        _model.TextChanged += (_, _) => Commit();
        _provider.SelectionChanged += (_, _) => Commit();
        _role.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            Commit();
            _toolsBlock.Visibility = _role.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshList(_item);
        };
    }

    // ================================================================== list

    private void RefreshList(object? select)
    {
        bool was = _loading;
        _loading = true;
        _list.Items.Clear();
        ListBoxItem? chosen = null;
        void Header(string text)
        {
            var t = Caption(text);
            t.Margin = new Thickness(6, 8, 0, 2);
            _list.Items.Add(new ListBoxItem { Content = t, IsEnabled = false, Focusable = false, IsHitTestVisible = false });
        }
        void Entry(object item, string name, string tag)
        {
            var row = new DockPanel();
            var badge = new TextBlock { Text = tag, FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            badge.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDim");
            DockPanel.SetDock(badge, Dock.Right);
            row.Children.Add(badge);
            row.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            var li = new ListBoxItem { Content = row, Tag = item, Padding = new Thickness(8, 5, 8, 5), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            _list.Items.Add(li);
            if (ReferenceEquals(item, select)) chosen = li;
        }
        Header("AGENTS");
        foreach (var a in Lib.Agents.OrderBy(a => a.Role == AgentRole.General ? 0 : a.Role == AgentRole.Designer ? 1 : 2).ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase))
            Entry(a, a.DisplayName, a.Role switch { AgentRole.Designer => "designer", AgentRole.Critic => "critic", _ => "assistant" });
        Header("SKILLS");
        foreach (var s in Lib.Skills.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)) Entry(s, s.DisplayName, "");
        if (chosen != null) { _list.SelectedItem = chosen; chosen.BringIntoView(); }
        _loading = was;
    }

    private static bool IsBuiltIn(object item) => item switch
    {
        Skill s => BuiltIns.Skills().Any(b => b.Name == s.Name),
        Agent a => BuiltIns.Agents().Any(b => b.Name == a.Name),
        _ => false,
    };

    // ================================================================== editor

    private void ShowItem(object? item)
    {
        Commit();
        FinishRename();
        _item = item;
        _loading = true;
        bool has = item != null;
        _editor.Visibility = has ? Visibility.Visible : Visibility.Hidden;
        _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (item is Skill s)
        {
            _kindLabel.Text = IsBuiltIn(s) ? "SKILL  ·  comes with the app (your edits are kept)" : "SKILL";
            _title.Text = s.DisplayName;
            _description.Text = s.Description;
            _body.Text = s.Body;
            _bodyCaption.Text = "GUIDANCE";
            _agentPanel.Visibility = Visibility.Collapsed;
        }
        else if (item is Agent a)
        {
            _kindLabel.Text = IsBuiltIn(a) ? "AGENT  ·  comes with the app (your edits are kept)" : "AGENT";
            _title.Text = a.DisplayName;
            _description.Text = a.Description;
            _body.Text = a.Instructions;
            _bodyCaption.Text = "INSTRUCTIONS (WRITTEN TO THE AI: \"YOU ARE…\")";
            _agentPanel.Visibility = Visibility.Visible;
            _role.SelectedIndex = a.Role switch { AgentRole.Designer => 1, AgentRole.Critic => 2, _ => 0 };
            _toolsBlock.Visibility = a.Role == AgentRole.General ? Visibility.Visible : Visibility.Collapsed;

            var providers = new List<ProviderChoice> { CurrentProvider };
            providers.AddRange(_ai.Settings.AiProviders.Select(p => new ProviderChoice(p.Id, p.Name)));
            _provider.ItemsSource = providers;
            _provider.SelectedItem = providers.FirstOrDefault(p => p.Id == a.Provider) ?? CurrentProvider;
            _model.Text = a.Model;

            _allTools.IsChecked = a.HasAllTools;
            foreach (CheckBox cb in _tools.Children)
            {
                cb.IsChecked = a.HasAllTools || a.Tools.Contains((string)cb.Tag, StringComparer.OrdinalIgnoreCase);
                cb.IsEnabled = !a.HasAllTools;
            }
            _skills.Children.Clear();
            foreach (var skill in Lib.Skills.OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var cb = new CheckBox { Content = skill.DisplayName, Tag = skill.Name, ToolTip = skill.Description, Margin = new Thickness(0, 0, 14, 4), IsChecked = a.Skills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase) };
                cb.Click += (_, _) => Commit();
                _skills.Children.Add(cb);
            }
            if (_skills.Children.Count == 0) _skills.Children.Add(Muted("No skills yet."));
        }
        _improve.IsEnabled = _duplicate.IsEnabled = _export.IsEnabled = _delete.IsEnabled = has && _cts == null;
        _loading = false;
    }

    /// <summary>Writes the fields on screen into the item being edited.</summary>
    private void Commit()
    {
        if (_loading || _item == null) return;
        var title = _title.Text.Trim();
        var bodyText = _body.Text.Replace("\r\n", "\n"); // text boxes use Windows line endings; the library uses \n
        if (_item is Skill s)
        {
            if (s.DisplayName == title && s.Description == _description.Text.Trim() && s.Body == bodyText) return;
            s.Title = title; s.Description = _description.Text.Trim(); s.Body = bodyText;
        }
        else if (_item is Agent a)
        {
            var before = a.ToMarkdown();
            a.Title = title;
            a.Description = _description.Text.Trim();
            a.Instructions = bodyText;
            a.Role = _role.SelectedIndex switch { 1 => AgentRole.Designer, 2 => AgentRole.Critic, _ => AgentRole.General };
            a.Provider = (_provider.SelectedItem as ProviderChoice)?.Id ?? "";
            a.Model = _model.Text.Trim();
            a.Tools = a.Role != AgentRole.General ? new List<string>()
                : _allTools.IsChecked == true ? new List<string> { Agent.AllTools }
                : _tools.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            var attached = _skills.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
            if (!attached.SequenceEqual(a.Skills)) a.Skills = attached;
            if (a.ToMarkdown() == before) return; // nothing really changed (e.g. just looked at it)
        }
        _dirty = true;
    }

    /// <summary>
    /// Gives a user-made item an id that matches its (possibly edited) name, and keeps references to it in step.
    /// Built-in items keep their id so the app can recognise them.
    /// </summary>
    private void FinishRename()
    {
        if (_item == null || IsBuiltIn(_item)) return;
        if (_item is Skill s)
        {
            var wanted = AiLibrary.Slug(s.DisplayName);
            if (wanted == s.Name || wanted == "untitled" || Lib.Skills.Any(x => x != s && x.Name == wanted)) return;
            foreach (var a in Lib.Agents)
                for (int i = 0; i < a.Skills.Count; i++) if (a.Skills[i] == s.Name) a.Skills[i] = wanted;
            var settings = _ai.Settings;
            for (int i = 0; i < settings.DesignSkills.Count; i++) if (settings.DesignSkills[i] == s.Name) settings.DesignSkills[i] = wanted;
            s.Name = wanted;
            _dirty = true;
        }
        else if (_item is Agent a)
        {
            var wanted = AiLibrary.Slug(a.DisplayName);
            if (wanted == a.Name || wanted == "untitled" || Lib.Agents.Any(x => x != a && x.Name == wanted)) return;
            var settings = _ai.Settings;
            if (settings.AssistantAgent == a.Name) settings.AssistantAgent = wanted;
            if (settings.DesignerAgent == a.Name) settings.DesignerAgent = wanted;
            if (settings.CriticAgent == a.Name) settings.CriticAgent = wanted;
            a.Name = wanted;
            _dirty = true;
        }
    }

    private void Select(object item)
    {
        RefreshList(item);
        ShowItem(item);
        _title.Focus();
        _title.SelectAll();
    }

    // ================================================================== new / duplicate / delete

    private void NewSkill()
    {
        var s = new Skill { Name = "new-skill", Title = "New skill", Description = "", Body = "# New skill\n\n- " };
        Lib.Add(s);
        _dirty = true;
        Select(s);
    }

    private void NewAgent()
    {
        var a = new Agent { Name = "new-agent", Title = "New agent", Role = AgentRole.General, Instructions = "You are " };
        Lib.Add(a);
        _dirty = true;
        Select(a);
    }

    private void Duplicate()
    {
        Commit();
        if (_item is Skill s)
        {
            var copy = s.Clone();
            copy.BuiltIn = false;
            copy.Title = s.DisplayName + " (copy)";
            copy.Name = AiLibrary.Slug(copy.Title);
            Lib.Add(copy);
            _dirty = true;
            Select(copy);
        }
        else if (_item is Agent a)
        {
            var copy = a.Clone();
            copy.BuiltIn = false;
            copy.Title = a.DisplayName + " (copy)";
            copy.Name = AiLibrary.Slug(copy.Title);
            Lib.Add(copy);
            _dirty = true;
            Select(copy);
        }
    }

    private void Delete()
    {
        if (_item == null) return;
        var name = _item is Skill s0 ? s0.DisplayName : ((Agent)_item).DisplayName;
        if (MessageBox.Show(this, $"Delete \"{name}\"?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var item = _item;
        _item = null;
        if (item is Skill s) Lib.Remove(s);
        else if (item is Agent a) Lib.Remove(a);
        _dirty = true;
        RefreshList(null);
        ShowItem(null);
    }

    // ================================================================== import / export

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import skills or agents",
            Filter = "Skills and agents (*.md;*.mdc;*.txt;*.json;*.zip;*.skill)|*.md;*.mdc;*.txt;*.json;*.zip;*.skill|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        var all = new ImportResult();
        foreach (var file in dlg.FileNames)
        {
            try
            {
                var r = LibraryPorter.ImportFile(file);
                all.Skills.AddRange(r.Skills);
                all.Agents.AddRange(r.Agents);
                all.Notes.AddRange(r.Notes);
                if (r.IsEmpty && r.Notes.Count == 0) all.Notes.Add($"{Path.GetFileName(file)}: no skill or agent was found in this file.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
            {
                all.Notes.Add($"{Path.GetFileName(file)}: couldn't be read ({ex.Message})");
            }
        }
        Finish(all);
    }

    private void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch (Exception) { text = ""; }
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "Copy the instructions in the other app first, then click Paste.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (text.Length > 200_000) text = text[..200_000];
        Finish(LibraryPorter.ImportText("Pasted instructions.txt", text));
    }

    /// <summary>Adds what was imported and tells the user what did and didn't come across.</summary>
    private void Finish(ImportResult r)
    {
        Commit();
        object? first = null;
        foreach (var s in r.Skills) { Lib.Add(s); first ??= s; }
        foreach (var a in r.Agents)
        {
            // Attached skills that came in the same import (possibly renamed) or already exist stay attached.
            a.Skills = a.Skills.Where(n => Lib.FindSkill(n) != null).ToList();
            Lib.Add(a);
            first = a;
        }
        if (!r.IsEmpty) _dirty = true;
        var summary = r.IsEmpty ? "Nothing was imported." : $"Imported {Count(r.Skills.Count, "skill")} and {Count(r.Agents.Count, "agent")}.";
        var notes = r.Notes.Count > 0 ? "\n\nWorth knowing:\n• " + string.Join("\n• ", r.Notes.Take(12)) : "";
        if (first != null) Select(first);
        MessageBox.Show(this, summary + notes + (r.IsEmpty ? "" : "\n\nImported text is treated as guidance only: it can't change what the AI is allowed to do in this app."),
                        "Import", MessageBoxButton.OK, r.IsEmpty ? MessageBoxImage.Warning : MessageBoxImage.Information);

        static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
    }

    private static readonly (string Label, ExportFormat Format, string Filter)[] SkillFormats =
    {
        ("Skill pack (.zip with SKILL.md) – for Claude and other tools that use skill folders", ExportFormat.SkillZip, "Zip archive (*.zip)|*.zip"),
        ("Markdown file (.md) – this app's own format", ExportFormat.Markdown, "Markdown (*.md)|*.md"),
        ("Plain text (.txt) – to paste into any assistant's custom instructions", ExportFormat.PlainText, "Text (*.txt)|*.txt"),
        ("JSON (.json) – for scripts and other software", ExportFormat.Json, "JSON (*.json)|*.json"),
    };

    private static readonly (string Label, ExportFormat Format, string Filter)[] AgentFormats =
    {
        ("Markdown file (.md) – agent file with a header, as used by this app and coding agents", ExportFormat.Markdown, "Markdown (*.md)|*.md"),
        ("Plain text (.txt) – instructions with its skills included, to paste into any assistant", ExportFormat.PlainText, "Text (*.txt)|*.txt"),
        ("Pack (.zip) – the agent plus the skills it uses", ExportFormat.SkillZip, "Zip archive (*.zip)|*.zip"),
        ("JSON (.json) – for scripts and other software", ExportFormat.Json, "JSON (*.json)|*.json"),
    };

    private void Export()
    {
        Commit();
        FinishRename();
        if (_item == null) return;
        var formats = _item is Skill ? SkillFormats : AgentFormats;
        var form = new FormDialog("Export", this)
            .Hint("Instructions travel well between AI tools. Tool permissions and model choices don't: the other tool will use its own.")
            .Choice("Format", formats.Select(f => f.Label), formats[0].Label);
        if (!form.Show(out var r)) return;
        var chosen = formats.First(f => f.Label == r.GetChoice<string>("Format"));
        var exported = _item is Skill s ? LibraryPorter.Export(s, chosen.Format) : LibraryPorter.Export((Agent)_item, chosen.Format, Lib);
        SaveExport(exported, chosen.Filter);
    }

    private void ExportAll()
    {
        Commit();
        FinishRename();
        SaveExport(LibraryPorter.ExportAll(Lib), "Zip archive (*.zip)|*.zip");
    }

    private void SaveExport(LibraryPorter.Exported exported, string filter)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export", FileName = exported.FileName, Filter = filter };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, exported.Bytes);
            SetStatus($"Saved {Path.GetFileName(dlg.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Couldn't save the file:\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ================================================================== AI assist

    private bool AiReady()
    {
        if (_ai.IsConfigured) return true;
        MessageBox.Show(this, "Set up an AI provider first (AI → AI settings), then the AI can draft and improve these for you.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async Task DraftAsync()
    {
        if (_cts != null || !AiReady()) return;
        const string skillChoice = "A skill (know-how to reuse)";
        const string agentChoice = "An agent (a role for the AI)";
        const string fromDesign = "A skill from my selected design";
        var selection = _selection?.Invoke();
        var choices = new List<string> { skillChoice, agentChoice };
        if (selection is { TooDetailed: false }) choices.Add(fromDesign);
        var form = new FormDialog("Draft with AI", this)
            .Hint("Say what you want in a sentence or two, e.g. \"round Scout badges cut from 3 mm ply with the group name curved round the top\". You'll see the draft before anything is saved.")
            .Choice("Make", choices, skillChoice)
            .Text("Describe it");
        if (!form.Show(out var r)) return;
        var what = r.GetChoice<string>("Make");
        var description = r.GetText("Describe it").Trim();
        if (what != fromDesign && description.Length < 3) { SetStatus("Describe what you want first."); return; }

        var assistant = new LibraryAssistant(_ai.Client);
        var proposal = await RunAsync("Drafting…", ct => what == agentChoice ? assistant.DraftAgentAsync(description, Lib, ct)
            : what == fromDesign ? assistant.SkillFromDesignAsync(selection!.Svg, $"{selection.Bounds.Width:0.#} × {selection.Bounds.Height:0.#} mm, {selection.Style}", description, ct)
            : assistant.DraftSkillAsync(description, ct));
        if (proposal == null) return;
        if (!new ProposalWindow(this, "AI draft", null, proposal).ShowDialog().GetValueOrDefault()) { SetStatus("Draft discarded."); return; }
        Commit();
        object added;
        if (proposal.Skill != null) { Lib.Add(proposal.Skill); added = proposal.Skill; }
        else { Lib.Add(proposal.Agent!); added = proposal.Agent!; }
        _dirty = true;
        Select(added);
        SetStatus("Added. Edit it freely – it's yours now.");
    }

    private async Task ImproveAsync()
    {
        if (_cts != null || _item == null || !AiReady()) return;
        Commit();
        var form = new FormDialog("Improve with AI", this)
            .Hint("The AI will tighten the wording, make vague rules specific and point out gaps. Tell it about anything that has been going wrong, or leave this empty.")
            .Text("What should it fix?");
        if (!form.Show(out var r)) return;
        var feedback = r.GetText("What should it fix?").Trim();
        var item = _item;
        var before = item is Skill s0 ? s0.ToMarkdown() : ((Agent)item).ToMarkdown();
        var assistant = new LibraryAssistant(_ai.Client);
        var proposal = await RunAsync("Reviewing…", ct => item is Skill s ? assistant.ImproveAsync(s, feedback, ct) : assistant.ImproveAsync((Agent)item, Lib, feedback, ct));
        if (proposal == null || !ReferenceEquals(item, _item)) return;
        if (!new ProposalWindow(this, "Suggested improvements", before, proposal).ShowDialog().GetValueOrDefault()) { SetStatus("Left as it was."); return; }

        if (item is Skill skill && proposal.Skill is { } ps)
        {
            skill.Title = ps.Title.Length > 0 ? ps.Title : skill.Title;
            skill.Description = ps.Description;
            skill.Body = ps.Body;
        }
        else if (item is Agent agent && proposal.Agent is { } pa)
        {
            agent.Title = pa.Title.Length > 0 ? pa.Title : agent.Title;
            agent.Description = pa.Description;
            agent.Instructions = pa.Instructions;
            agent.Tools = pa.Tools;      // already limited to what the agent had before
            agent.Skills = pa.Skills;
        }
        _dirty = true;
        _item = null; // the fields on screen are now stale; don't let Commit write them back
        RefreshList(item);
        ShowItem(item);
        SetStatus("Updated.");
    }

    private async Task<LibraryProposal?> RunAsync(string busyText, Func<CancellationToken, Task<LibraryProposal>> work)
    {
        _cts = new CancellationTokenSource();
        foreach (var b in _busyButtons) b.IsEnabled = false;
        Cursor = Cursors.AppStarting;
        SetStatus(busyText);
        try
        {
            var p = await work(_cts.Token);
            SetStatus("");
            return p;
        }
        catch (AiException ex) when (ex.Kind == AiErrorKind.Cancelled) { return null; }
        catch (OperationCanceledException) { return null; }
        catch (AiException ex)
        {
            SetStatus(ex.Message, warning: true);
            return null;
        }
        finally
        {
            _cts = null;
            Cursor = null;
            foreach (var b in _busyButtons) b.IsEnabled = true;
            _improve.IsEnabled = _item != null;
        }
    }

    private void SetStatus(string text, bool warning = false)
    {
        _status.Text = text;
        _status.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Brush.Warning" : "Brush.TextMuted");
    }

    // ---- smoke test hook
    internal void SmokeSelect(string agentName)
    {
        if (Lib.FindAgent(agentName) is { } a) { RefreshList(a); ShowItem(a); }
    }
}

/// <summary>
/// Shows an AI-written skill or agent for review, beside the current version when there is one. Nothing is saved
/// unless the user clicks Use this.
/// </summary>
public sealed class ProposalWindow : Window
{
    public ProposalWindow(Window owner, string title, string? before, LibraryProposal proposal)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = title;
        Owner = owner;
        Icon = owner.Icon;
        Width = before == null ? 720 : 1080; Height = 640; MinWidth = 600; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        WindowTheme.ApplyDark(this);

        var root = new Grid { Margin = new Thickness(20, 16, 20, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(new TextBlock { Text = proposal.DisplayName, FontSize = 17, FontWeight = FontWeights.SemiBold });
        var kind = proposal.Skill != null ? "Skill" : $"Agent · {proposal.Agent!.Role.ToString().ToLowerInvariant()}";
        var extras = new List<string> { kind };
        if (proposal.Agent is { } a)
        {
            if (a.Role == AgentRole.General) extras.Add(a.Tools.Count == 0 ? "advice only (no editing tools)" : a.HasAllTools ? "every tool" : "tools: " + string.Join(", ", a.Tools));
            if (a.Skills.Count > 0) extras.Add("skills: " + string.Join(", ", a.Skills));
        }
        var meta = new TextBlock { Text = string.Join("   ·   ", extras), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        top.Children.Add(meta);
        if (proposal.Notes.Count > 0)
        {
            var notes = new TextBlock { Text = "• " + string.Join("\n• ", proposal.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0) };
            top.Children.Add(notes);
        }
        root.Children.Add(top);

        var panes = new Grid();
        Grid.SetRow(panes, 1);
        root.Children.Add(panes);
        panes.ColumnDefinitions.Add(new ColumnDefinition());
        if (before != null)
        {
            panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            panes.ColumnDefinitions.Add(new ColumnDefinition());
            panes.Children.Add(Pane("NOW", before, 0));
            panes.Children.Add(Pane("SUGGESTED", proposal.Markdown, 2));
        }
        else
        {
            panes.Children.Add(Pane("DRAFT", proposal.Markdown, 0));
        }

        var buttons = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        var hint = new TextBlock { Text = "Nothing changes unless you click Use this. You can edit it afterwards.", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        DockPanel.SetDock(hint, Dock.Left);
        buttons.Children.Add(hint);
        var use = new Button { Content = "Use this", MinWidth = 120, IsDefault = true };
        use.SetResourceReference(StyleProperty, "PrimaryButton");
        use.Click += (_, _) => DialogResult = true;
        DockPanel.SetDock(use, Dock.Right);
        buttons.Children.Add(use);
        var discard = new Button { Content = "Discard", MinWidth = 90, IsCancel = true, Margin = new Thickness(2, 2, 8, 2) };
        DockPanel.SetDock(discard, Dock.Right);
        buttons.Children.Add(discard);
    }

    private DockPanel Pane(string caption, string text, int column)
    {
        var dock = new DockPanel();
        Grid.SetColumn(dock, column);
        var cap = new TextBlock { Text = caption };
        cap.SetResourceReference(StyleProperty, "Caption");
        DockPanel.SetDock(cap, Dock.Top);
        dock.Children.Add(cap);
        var box = new TextBox
        {
            Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6),
        };
        box.SetResourceReference(FontFamilyProperty, "Font.Mono");
        dock.Children.Add(box);
        return dock;
    }
}
