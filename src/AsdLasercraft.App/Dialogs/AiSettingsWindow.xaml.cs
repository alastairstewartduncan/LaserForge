using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;

namespace AsdLasercraft.App.Dialogs;

/// <summary>
/// Provider, API key, model and privacy options for the AI features. Each provider keeps its own key, address and
/// model; nothing is saved until Save is clicked.
/// </summary>
public partial class AiSettingsWindow : Window
{
    private readonly AppSettings _settings;
    /// <summary>Working copies, so Cancel leaves the real settings alone.</summary>
    private readonly List<AiProviderProfile> _profiles;
    /// <summary>Keys typed but not yet saved, by provider id.</summary>
    private readonly Dictionary<string, string> _newKeys = new();
    private readonly HashSet<string> _removedKeys = new();
    private AiProviderProfile? _current;
    private bool _loading;

    public AiSettingsWindow(Window owner, AppSettings settings)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);
        _settings = settings;
        settings.EnsureProviders();
        _profiles = settings.AiProviders.Select(p => p.Clone()).ToList();

        EnabledCheck.IsChecked = settings.AiEnabled;
        CanEditCheck.IsChecked = settings.AssistantCanEdit;
        PhotoCheck.IsChecked = settings.AiPhotoConsent;

        ProviderCombo.ItemsSource = _profiles;
        ProviderCombo.SelectedItem = _profiles.FirstOrDefault(p => p.Id == settings.AiProvider) ?? _profiles[0];
    }

    // ------------------------------------------------------------------ provider switching

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Stash();
        _current = ProviderCombo.SelectedItem as AiProviderProfile;
        if (_current == null) return;
        _loading = true;
        var p = _current;
        AddressPanel.Visibility = p.Kind == AiProviderKind.OpenAiCompatible ? Visibility.Visible : Visibility.Collapsed;
        BaseUrlBox.Text = p.BaseUrl;
        KeyBox.Password = _newKeys.TryGetValue(p.Id, out var typed) ? typed : "";
        KeyCaption.Text = p.KeyRequired ? "API KEY" : "API KEY (OPTIONAL)";
        bool hasLink = !string.IsNullOrWhiteSpace(p.KeyUrl);
        KeyHelp.Visibility = hasLink ? Visibility.Visible : Visibility.Collapsed;
        if (hasLink && Uri.TryCreate(p.KeyUrl, UriKind.Absolute, out var uri)) ConsoleLinkText.Text = uri.Host;

        var suggestions = AiProviderCatalog.SuggestedModels(p).ToList();
        if (p.Model.Length > 0 && !suggestions.Contains(p.Model)) suggestions.Insert(0, p.Model);
        ModelCombo.ItemsSource = suggestions;
        ModelCombo.Text = p.Model;
        ModelHint.Text = p.Kind == AiProviderKind.Anthropic
            ? "The default is a good balance of quality, speed and cost."
            : p.KeyRequired
                ? "Model names change often: enter your key, then click Get list to choose from the models it can use."
                : "Start the model server on this PC, then click Get list to choose from the models it has loaded.";
        VisionCheck.IsChecked = p.SupportsVision;
        ToolsCheck.IsChecked = p.SupportsTools;
        TestResult.Text = "";
        _loading = false;
        UpdateKeyStatus();
    }

    /// <summary>Copies what's on screen into the working profile before switching away or saving.</summary>
    private void Stash()
    {
        if (_current == null || _loading) return;
        _current.BaseUrl = BaseUrlBox.Text.Trim();
        _current.Model = ModelCombo.Text.Trim();
        _current.SupportsVision = VisionCheck.IsChecked == true;
        _current.SupportsTools = ToolsCheck.IsChecked == true;
        var typed = KeyBox.Password.Trim();
        if (typed.Length > 0) { _newKeys[_current.Id] = typed; _removedKeys.Remove(_current.Id); }
        else _newKeys.Remove(_current.Id);
    }

    private string? EffectiveKey(AiProviderProfile p)
    {
        if (_current == p && KeyBox.Password.Trim().Length > 0) return KeyBox.Password.Trim();
        if (_newKeys.TryGetValue(p.Id, out var typed)) return typed;
        return _removedKeys.Contains(p.Id) ? null : SecretStore.GetKey(p.Id);
    }

    // ------------------------------------------------------------------ key

    private void UpdateKeyStatus()
    {
        if (_current == null) return;
        var saved = _removedKeys.Contains(_current.Id) ? null : SecretStore.GetKey(_current.Id);
        if (KeyBox.Password.Length > 0)
        {
            bool odd = _current.Kind == AiProviderKind.Anthropic && !KeyBox.Password.Trim().StartsWith("sk-ant-", StringComparison.Ordinal);
            KeyStatus.Text = odd ? "That doesn't look like a Claude API key (they start with sk-ant-)." : "New key – it will be saved when you click Save.";
            KeyStatus.Foreground = (Brush)FindResource(odd ? "Brush.Warning" : "Brush.TextMuted");
        }
        else if (!string.IsNullOrEmpty(saved))
        {
            KeyStatus.Text = $"Saved key: {SecretStore.Mask(saved)}. Type a new one to replace it.";
            KeyStatus.Foreground = (Brush)FindResource("Brush.Success");
        }
        else
        {
            KeyStatus.Text = _current.KeyRequired ? "No key saved yet for this provider." : "No key needed for a model running on this PC. Add one only if your server asks for it.";
            KeyStatus.Foreground = (Brush)FindResource("Brush.TextMuted");
        }
        RemoveKeyButton.IsEnabled = !string.IsNullOrEmpty(saved);
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TestResult.Text = "";
        UpdateKeyStatus();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_loading) TestResult.Text = "";
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (MessageBox.Show(this, $"Forget the saved key for {_current.Name}?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _removedKeys.Add(_current.Id);
        _newKeys.Remove(_current.Id);
        KeyBox.Clear();
        UpdateKeyStatus();
    }

    private void ConsoleLink_Click(object sender, RoutedEventArgs e)
    {
        if (_current != null && !string.IsNullOrWhiteSpace(_current.KeyUrl)) AboutWindow.OpenUrl(_current.KeyUrl);
    }

    // ------------------------------------------------------------------ models and test

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        Stash();
        var p = _current;
        FetchModelsButton.IsEnabled = false;
        ShowResult(null, "Getting the model list…");
        try
        {
            var models = await AiModelLister.ListAsync(p, EffectiveKey(p));
            if (_current != p) return; // the user moved to another provider meanwhile
            var keep = ModelCombo.Text;
            ModelCombo.ItemsSource = models;
            ModelCombo.Text = keep;
            if (models.Count == 0) ShowResult(false, "The provider returned no models.");
            else
            {
                ShowResult(true, $"{models.Count} models found – pick one from the Model list.");
                if (keep.Trim().Length == 0) ModelCombo.IsDropDownOpen = true;
            }
        }
        catch (AiException ex)
        {
            ShowResult(false, ex.Message);
        }
        finally
        {
            FetchModelsButton.IsEnabled = true;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        Stash();
        var p = _current.Clone();
        var key = EffectiveKey(_current);
        if (p.KeyRequired && string.IsNullOrWhiteSpace(key)) { ShowResult(false, "Enter a key first."); return; }
        if (p.Kind != AiProviderKind.Anthropic && p.Model.Length == 0) { ShowResult(false, "Choose a model first."); return; }
        TestButton.IsEnabled = false;
        ShowResult(null, "Checking…");
        try
        {
            var problem = await AiServices.TestAsync(p, key);
            ShowResult(problem == null, problem ?? "Connected – AI is ready.");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ShowResult(bool? ok, string text)
    {
        TestResult.Text = text;
        TestResult.Foreground = (Brush)FindResource(ok == null ? "Brush.TextMuted" : ok == true ? "Brush.Success" : "Brush.Danger");
    }

    // ------------------------------------------------------------------ save

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Stash();
        foreach (var id in _removedKeys) SecretStore.DeleteKey(id);
        foreach (var (id, key) in _newKeys)
        {
            var profile = _profiles.First(p => p.Id == id);
            if (!SecretStore.SaveKey(id, key, AiClientFactory.ShortName(profile) + " API"))
            {
                MessageBox.Show(this, "Windows couldn't save the key in Credential Manager.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        _settings.AiProviders = _profiles;
        if (_current != null) _settings.AiProvider = _current.Id;
        var claude = _profiles.FirstOrDefault(p => p.Id == AiProviderCatalog.AnthropicId);
        if (claude != null && claude.Model.Length > 0) _settings.AiModel = claude.Model; // keeps older versions of the app in step
        _settings.AiEnabled = EnabledCheck.IsChecked == true;
        _settings.AssistantCanEdit = CanEditCheck.IsChecked == true;
        _settings.AiPhotoConsent = PhotoCheck.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }

    // ---- smoke test hook
    internal void SmokeSelect(string providerId)
    {
        var p = _profiles.FirstOrDefault(x => x.Id == providerId);
        if (p != null) ProviderCombo.SelectedItem = p;
    }
}
