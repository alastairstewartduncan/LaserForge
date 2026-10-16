using System.Windows;
using System.Windows.Media;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Ai;

namespace AsdLasercraft.App.Dialogs;

/// <summary>API key, model and privacy options for the AI features.</summary>
public partial class AiSettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _keyRemoved;

    public AiSettingsWindow(Window owner, AppSettings settings)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);
        _settings = settings;

        ModelCombo.ItemsSource = ClaudeClient.SuggestedModels;
        ModelCombo.Text = string.IsNullOrWhiteSpace(settings.AiModel) ? ClaudeClient.DefaultModel : settings.AiModel;
        EnabledCheck.IsChecked = settings.AiEnabled;
        CanEditCheck.IsChecked = settings.AssistantCanEdit;
        PhotoCheck.IsChecked = settings.AiPhotoConsent;
        UpdateKeyStatus();
    }

    private void UpdateKeyStatus()
    {
        var saved = _keyRemoved ? null : SecretStore.ApiKey;
        if (KeyBox.Password.Length > 0)
        {
            bool looksRight = KeyBox.Password.Trim().StartsWith("sk-ant-", StringComparison.Ordinal);
            KeyStatus.Text = looksRight ? "New key – it will be saved when you click Save." : "That doesn't look like a Claude API key (they start with sk-ant-).";
            KeyStatus.Foreground = (Brush)FindResource(looksRight ? "Brush.TextMuted" : "Brush.Warning");
        }
        else if (!string.IsNullOrEmpty(saved))
        {
            KeyStatus.Text = $"Saved key: {SecretStore.Mask(saved)}. Type a new one to replace it.";
            KeyStatus.Foreground = (Brush)FindResource("Brush.Success");
        }
        else
        {
            KeyStatus.Text = "No key saved yet – the AI features need one.";
            KeyStatus.Foreground = (Brush)FindResource("Brush.TextMuted");
        }
        RemoveKeyButton.IsEnabled = !string.IsNullOrEmpty(saved);
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        TestResult.Text = "";
        UpdateKeyStatus();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Forget the saved Claude API key?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _keyRemoved = true;
        KeyBox.Clear();
        UpdateKeyStatus();
    }

    private void ConsoleLink_Click(object sender, RoutedEventArgs e) => AboutWindow.OpenUrl("https://console.anthropic.com/settings/keys");

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Length > 0 ? KeyBox.Password : _keyRemoved ? null : SecretStore.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) { ShowResult(false, "Enter a key first."); return; }
        TestButton.IsEnabled = false;
        TestResult.Foreground = (Brush)FindResource("Brush.TextMuted");
        TestResult.Text = "Checking…";
        try
        {
            using var client = new ClaudeClient(() => key) { Model = ModelName };
            await client.SendAsync(new AiRequest { MaxTokens = 20, Messages = { AiMessage.User("Reply with just the word: ready") } });
            ShowResult(true, "Connected – AI is ready.");
        }
        catch (AiException ex)
        {
            ShowResult(false, ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void ShowResult(bool ok, string text)
    {
        TestResult.Text = text;
        TestResult.Foreground = (Brush)FindResource(ok ? "Brush.Success" : "Brush.Danger");
    }

    private string ModelName => string.IsNullOrWhiteSpace(ModelCombo.Text) ? ClaudeClient.DefaultModel : ModelCombo.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_keyRemoved) SecretStore.DeleteApiKey();
        if (KeyBox.Password.Trim().Length > 0 && !SecretStore.SaveApiKey(KeyBox.Password))
        {
            MessageBox.Show(this, "Windows couldn't save the key in Credential Manager.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.AiModel = ModelName;
        _settings.AiEnabled = EnabledCheck.IsChecked == true;
        _settings.AssistantCanEdit = CanEditCheck.IsChecked == true;
        _settings.AiPhotoConsent = PhotoCheck.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }
}
