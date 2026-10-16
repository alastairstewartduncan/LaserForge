using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using AsdLasercraft.App.Controls;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;
using AsdLasercraft.Core.Imaging;
using AsdLasercraft.Core.Import;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.App;

// AI: describe-a-design, test grid reader, smart image tools and the assistant chat.
public partial class MainWindow
{
    private AiServices _ai = null!;
    private AssistantSession _assistant = null!;
    private CancellationTokenSource? _chatCts;

    private static readonly string[] Suggestions =
    {
        "What settings should I use to cut 3 mm birch ply?",
        "Check my job before I burn",
        "Add \"Happy Birthday\" in a script font, centred",
        "Copy the selection into a 3 × 2 grid",
        "What does my last alarm mean?",
    };

    private void InitAi()
    {
        _ai = new AiServices(_settings);
        _assistant = new AssistantSession(_ai.Client, new AssistantBridge(this)) { CanEdit = _settings.AssistantCanEdit };
        foreach (var text in Suggestions)
        {
            var chip = new Button { Content = text };
            chip.SetResourceReference(StyleProperty, "ChipButton");
            chip.Click += async (_, _) => await SendChatAsync(text);
            SuggestionPanel.Children.Add(chip);
        }
        RefreshAiUi();
        ShowWelcome();
    }

    /// <summary>Shows or hides AI features and the setup prompt to match the settings and key.</summary>
    private void RefreshAiUi()
    {
        _ai.Refresh();
        var vis = _settings.AiEnabled ? Visibility.Visible : Visibility.Collapsed;
        AiMenu.Visibility = vis;
        AiDesignToolbarButton.Visibility = vis;
        AssistantTab.Visibility = vis;
        if (!_settings.AiEnabled && RightTabs.SelectedItem == AssistantTab) RightTabs.SelectedIndex = 0;

        bool ready = _ai.IsConfigured;
        AiSetupCard.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        ChatScroll.Visibility = ready ? Visibility.Visible : Visibility.Hidden;
        SuggestionPanel.Visibility = ready && ChatPanel.Children.Count <= 1 ? Visibility.Visible : Visibility.Collapsed;
        ChatBox.IsEnabled = ready;
        ChatSendButton.IsEnabled = ready;
        _updatingUi = true;
        ChatEditToggle.IsChecked = _settings.AssistantCanEdit;
        _updatingUi = false;
        _assistant.CanEdit = _settings.AssistantCanEdit;
        ChatStatus.Text = ready ? $"Claude · {_settings.AiModel}" : "Not set up";
    }

    /// <summary>True when AI can be used now; otherwise offers the settings window.</summary>
    private bool EnsureAiReady()
    {
        if (_ai.IsConfigured) return true;
        if (MessageBox.Show(this, "The AI features need a Claude API key. Add one now?", "AI", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            AiSettings_Click(this, new RoutedEventArgs());
        return _ai.IsConfigured;
    }

    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        if (new AiSettingsWindow(this, _settings).ShowDialog() == true)
        {
            RefreshAiUi();
            if (_ai.IsConfigured) ShowToast("AI settings saved");
        }
    }

    // ================================================================== describe a design

    private void AiDesign_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAiReady()) return;
        var win = new DesignWindow(this, _ai);
        if (win.ShowDialog() != true || win.Result is not { } design) return;
        AddDesign(design, win.ResultStyle);
    }

    private void AddDesign(DesignResult design, DesignStyle style)
    {
        List<AsdLasercraft.Core.Model.Shape> shapes = new();
        Edit(() =>
        {
            shapes = DesignShapes.Build(design, _project, style);
            _project.Shapes.AddRange(shapes);
        });
        LayersGrid.Items.Refresh();
        Surface.SelectOnly(shapes.Select(s => s.Id));
        var layers = shapes.Select(s => _project.FindLayer(s.LayerId)?.Name).Distinct();
        ShowToast($"Design added on {string.Join(" and ", layers.Select(l => $"'{l}'"))} – resize it with the handles");
    }

    // ================================================================== test grid reader

    private void AiReadGrid_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAiReady()) return;
        var layer = ActiveLayer;
        var win = new TestGridWindow(this, _ai, _settings, Materials, AppSettings.MaterialsPath, layer);
        if (win.ShowDialog() != true || win.ApplyToLayer is not { } preset || layer == null) return;
        Edit(() => preset.ApplyTo(layer));
        LayersGrid.Items.Refresh();
        LayersGrid.SelectedItem = layer;
        RefreshLayerDetails();
        ShowToast($"'{layer.Name}' set to {preset.Summary}");
    }

    // ================================================================== smart image tools

    private ImageShape? TargetImage(string action)
    {
        var img = Selected().OfType<ImageShape>().FirstOrDefault()
                  ?? (_project.Shapes.OfType<ImageShape>().Count() == 1 ? _project.Shapes.OfType<ImageShape>().First() : null);
        if (img == null) ShowToast($"Select an image to {action}.", ToastKind.Info);
        return img;
    }

    private async void RemoveBackground_Click(object sender, RoutedEventArgs e)
    {
        if (TargetImage("remove its background") is not { } img) return;
        const string modelChoice = "AI model – busy backgrounds (best)";
        const string plainChoice = "Plain background – white wall, sheet, sky";
        var form = new FormDialog("Remove background", this)
            .Hint("Makes everything behind the subject white, so the laser skips it. Both methods run on your PC; the AI model is a one-off 4.6 MB download.")
            .Choice("Method", new[] { modelChoice, plainChoice }, BackgroundModel.IsDownloaded || _settings.BackgroundModelConsent ? modelChoice : plainChoice)
            .Number("Edge softness (0–100)", 50, "Higher fades hair and fur out gently; lower gives a crisp cut-out edge.");
        if (!form.Show(out var r)) return;
        bool useModel = r.GetChoice<string>("Method") == modelChoice;
        double softness = Math.Clamp(r.GetNumber("Edge softness (0–100)", 50), 0, 100) / 100;

        if (useModel && !BackgroundModel.IsDownloaded)
        {
            if (!_settings.BackgroundModelConsent &&
                MessageBox.Show(this, $"Download the background removal model now?\n\n4.6 MB, once, from GitHub (U²-Net via the rembg project, Apache-2.0). " +
                                      "After that it works offline and your photos never leave your PC.", "Remove background",
                                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            _settings.BackgroundModelConsent = true;
            _settings.Save();
            try
            {
                Cursor = Cursors.Wait;
                ShowToast("Downloading the background model…", ToastKind.Info);
                await BackgroundModel.DownloadAsync(null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't download the model:\n{ex.Message}\n\nYou can still use the plain background method.", "Remove background",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            finally { Cursor = null; }
        }

        byte[] result;
        try
        {
            Cursor = Cursors.Wait;
            var (gray, w, h) = (img.Gray, img.PixelWidth, img.PixelHeight);
            result = await Task.Run(() =>
            {
                var mask = useModel ? BackgroundRemoval.PredictMask(BackgroundModel.Load(), gray, w, h) : BackgroundRemoval.PlainBackgroundMask(gray, w, h);
                return BackgroundRemoval.ApplyMask(gray, mask, softness);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Background removal failed:\n{ex.Message}", "Remove background", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally { Cursor = null; }

        Edit(() => img.Gray = result);
        Surface.InvalidateImages();
        ShowToast("Background removed – Ctrl+Z to undo");
    }

    private async void LineArt_Click(object sender, RoutedEventArgs e)
    {
        if (TargetImage("turn into line art") is not { } img) return;
        var win = new LineArtWindow(this, img.Gray, img.PixelWidth, img.PixelHeight);
        if (win.ShowDialog() != true) return;
        Cursor = Cursors.Wait;
        byte[] result;
        var options = win.Options; // read the sliders here, on the UI thread
        var (gray, w, h) = (img.Gray, img.PixelWidth, img.PixelHeight);
        try { result = await Task.Run(() => LineArt.Apply(gray, w, h, options)); }
        finally { Cursor = null; }
        Edit(() =>
        {
            img.Gray = result;
            // Line art is already black and white: burn it as-is.
            img.Brightness = 0; img.Contrast = 0; img.Gamma = 1; img.Invert = false;
            img.Dither = DitherMode.Threshold;
            img.Threshold = 128;
        });
        Surface.InvalidateImages();
        RefreshSelectionUi();
        ShowToast("Converted to line art – Ctrl+Z to undo");
    }

    private void AutoTune_Click(object sender, RoutedEventArgs e)
    {
        if (TargetImage("auto-tune") is not { } img) return;
        var layer = _project.FindLayer(img.LayerId);
        var material = layer?.MaterialName ?? layer?.Name;
        var tune = AutoTune.Tune(img.Gray, material);
        Edit(() => AutoTune.ApplyTo(tune, img));
        Surface.InvalidateImages();
        RefreshSelectionUi();
        ShowToast($"Tuned for {(tune.Material == AutoTune.General ? "general use" : tune.Material.Name.ToLowerInvariant())}. {tune.Explanation}", ToastKind.Info);
    }

    // ================================================================== assistant chat

    private void AiAssistant_Click(object sender, RoutedEventArgs e) => FocusAssistant();

    private void FocusAssistant()
    {
        if (!_settings.AiEnabled) return;
        RightTabs.SelectedItem = AssistantTab;
        Dispatcher.InvokeAsync(() => ChatBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ChatNew_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        _assistant.Reset();
        ChatPanel.Children.Clear();
        ShowWelcome();
        RefreshAiUi();
        ChatBox.Focus();
    }

    private void ChatEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingUi) return;
        _settings.AssistantCanEdit = ChatEditToggle.IsChecked == true;
        _assistant.CanEdit = _settings.AssistantCanEdit;
        _settings.Save();
        ShowToast(_settings.AssistantCanEdit ? "The assistant can now change your design" : "The assistant will only give advice", ToastKind.Info);
    }

    private void ChatBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ChatSend_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void ChatSend_Click(object sender, RoutedEventArgs e)
    {
        if (_chatCts != null) { _chatCts.Cancel(); return; } // the send button doubles as Stop
        var text = ChatBox.Text.Trim();
        if (text.Length == 0) return;
        ChatBox.Clear();
        await SendChatAsync(text);
    }

    private async Task SendChatAsync(string text)
    {
        if (_chatCts != null || !_ai.IsConfigured) return;
        SuggestionPanel.Visibility = Visibility.Collapsed;
        AddUserBubble(text);
        var thinking = AddThinking();
        _chatCts = new CancellationTokenSource();
        ChatSendGlyph.Data = (Geometry)FindResource("I.Stop");
        ChatSendButton.ToolTip = "Stop";
        try
        {
            var reply = await _assistant.SendAsync(text, _chatCts.Token);
            ChatPanel.Children.Remove(thinking);
            AddAssistantBubble(reply.Text, reply.Actions);
        }
        catch (AiException ex) when (ex.Kind == AiErrorKind.Cancelled)
        {
            ChatPanel.Children.Remove(thinking);
            AddNote("Stopped.");
        }
        catch (OperationCanceledException)
        {
            ChatPanel.Children.Remove(thinking);
            AddNote("Stopped.");
        }
        catch (AiException ex)
        {
            ChatPanel.Children.Remove(thinking);
            AddNote(ex.Message, warning: true);
        }
        finally
        {
            _chatCts = null;
            ChatSendGlyph.Data = (Geometry)FindResource("I.Send");
            ChatSendButton.ToolTip = "Send (Enter)";
        }
    }

    private void ShowWelcome()
    {
        if (ChatPanel.Children.Count > 0) return;
        AddAssistantBubble(
            "Hi! I can help with laser settings and materials, explain GRBL alarms, check your job, and lay out text, shapes and arrays for you. " +
            "I can't start the laser – you stay in control of every burn.", Array.Empty<AssistantAction>());
    }

    private Border Bubble(UIElement content, bool user)
    {
        var b = new Border
        {
            Child = content,
            Padding = new Thickness(12, 8, 12, 9),
            Margin = user ? new Thickness(40, 4, 0, 4) : new Thickness(0, 4, 24, 4),
            CornerRadius = user ? new CornerRadius(12, 12, 4, 12) : new CornerRadius(4, 12, 12, 12),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            BorderThickness = new Thickness(1),
        };
        b.SetResourceReference(Border.BackgroundProperty, user ? "Brush.AccentFaint" : "Brush.Panel");
        b.SetResourceReference(Border.BorderBrushProperty, user ? "Brush.AccentSoft" : "Brush.Border");
        return b;
    }

    private void AddUserBubble(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        ChatPanel.Children.Add(Bubble(tb, user: true));
        ScrollChat();
    }

    private void AddAssistantBubble(string text, IReadOnlyList<AssistantAction> actions)
    {
        var stack = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new Glyph { Data = (Geometry)FindResource("I.Sparkle"), Size = 13, Foreground = (Brush)FindResource("Brush.Accent") });
        var name = new TextBlock { Text = "Assistant", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        header.Children.Add(name);
        stack.Children.Add(header);
        if (!string.IsNullOrWhiteSpace(text)) stack.Children.Add(FormatReply(text));
        if (actions.Count > 0)
        {
            var chips = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var a in actions)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new Glyph
                {
                    Data = (Geometry)FindResource(a.Failed ? "I.Warning" : "I.Check"), Size = 12,
                    Foreground = (Brush)FindResource(a.Failed ? "Brush.Warning" : "Brush.Success"),
                });
                var t = new TextBlock { Text = a.Summary, FontSize = 11.5, Margin = new Thickness(5, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center };
                t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
                row.Children.Add(t);
                var chip = new Border { Child = row, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(8) };
                chip.SetResourceReference(Border.BackgroundProperty, "Brush.Bg0");
                chips.Children.Add(chip);
            }
            stack.Children.Add(chips);
        }
        ChatPanel.Children.Add(Bubble(stack, user: false));
        ScrollChat();
    }

    private UIElement AddThinking()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 0, 6) };
        row.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 46, Height = 4, VerticalAlignment = VerticalAlignment.Center });
        var t = new TextBlock { Text = "Thinking…", FontSize = 12, Margin = new Thickness(10, 0, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        row.Children.Add(t);
        ChatPanel.Children.Add(row);
        ScrollChat();
        return row;
    }

    private void AddNote(string text, bool warning = false)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 4, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Brush.Warning" : "Brush.TextMuted");
        ChatPanel.Children.Add(t);
        ScrollChat();
    }

    private void ScrollChat() => Dispatcher.InvokeAsync(() => ChatScroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);

    private static readonly Regex InlineMarkup = new(@"(\*\*[^*]+\*\*|`[^`]+`)", RegexOptions.Compiled);

    /// <summary>Light formatting for replies: paragraphs, bullet and numbered lists, **bold** and `code`.</summary>
    private TextBlock FormatReply(string text)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 };
        var lines = text.Replace("\r", "").Split('\n');
        bool first = true;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (!first) tb.Inlines.Add(new LineBreak());
            first = false;
            if (line.Length == 0) continue;
            var m = Regex.Match(line, @"^\s*(?:[-*•]\s+|(\d+)[.)]\s+)");
            if (m.Success)
            {
                tb.Inlines.Add(new Run(m.Groups[1].Success ? $"  {m.Groups[1].Value}.  " : "  •  ") { Foreground = (Brush)FindResource("Brush.Accent") });
                line = line[m.Length..];
            }
            line = Regex.Replace(line, @"^#{1,4}\s*", "");
            foreach (var part in InlineMarkup.Split(line))
            {
                if (part.Length == 0) continue;
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
                    tb.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold });
                else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                    tb.Inlines.Add(new Run(part[1..^1]) { FontFamily = (FontFamily)FindResource("Font.Mono"), Foreground = (Brush)FindResource("Brush.Info") });
                else
                    tb.Inlines.Add(new Run(part));
            }
        }
        return tb;
    }

    /// <summary>Gives the assistant a view of the live project; every tool call becomes one undoable edit.</summary>
    private sealed class AssistantBridge : IAssistantHost
    {
        private readonly MainWindow _w;
        public AssistantBridge(MainWindow w) { _w = w; }

        public LaserProject Project => _w._project;
        public IReadOnlyCollection<Guid> Selection => _w.Surface.Selection.ToList();
        public MaterialLibrary Materials => _w.Materials;
        public ITextOutliner Outliner => WpfTextOutliner.Instance;
        public IReadOnlyList<string> InstalledFonts => AiServices.InstalledFonts;
        public IReadOnlyList<string> RecentConsole =>
            _w.ConsoleList.Items.Cast<object>().Select(o => o.ToString() ?? "").TakeLast(15).ToList();

        public void BeginEdit(string description)
        {
            _w.ExitSimulation();
            _w._undo.Push(_w._project);
            _w.MarkDirty();
        }

        public void EndEdit(IEnumerable<Guid>? select)
        {
            _w.LayersGrid.Items.Refresh();
            _w.RefreshLayerDetails();
            if (select != null) _w.Surface.SelectOnly(select);
            _w.Surface.InvalidateVisual();
            _w.RefreshSelectionUi();
            _w.UpdateJobEstimate();
        }

        public Task<DesignResult>? GenerateDesign(DesignRequest request, CancellationToken ct) =>
            new DesignGenerator(_w._ai.Client, WpfTextOutliner.Instance).GenerateAsync(request, ct);
    }

    // ---- smoke test hooks
    internal AiServices SmokeAi => _ai;

    internal void SmokeUseFakeAi(IAiClient client)
    {
        _ai.Client = client;
        _assistant = new AssistantSession(client, new AssistantBridge(this)) { CanEdit = true };
        RefreshAiUi();
    }

    internal Task SmokeChat(string text) => SendChatAsync(text);
}
