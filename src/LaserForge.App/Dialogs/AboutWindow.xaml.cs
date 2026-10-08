using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Navigation;
using LaserForge.Core;

namespace LaserForge.App.Dialogs;

public partial class AboutWindow : Window
{
    public AboutWindow(Window? owner)
    {
        InitializeComponent();
        Owner = owner;

        VersionText.Text = $"Version {AppInfo.Version}";
        AuthorText.Text = AppInfo.Author;
        EmailText.Text = AppInfo.AuthorEmail;
        EmailLink.NavigateUri = new Uri("mailto:" + AppInfo.AuthorEmail + "?subject=" + Uri.EscapeDataString($"LaserForge {AppInfo.Version}"));
        RepoText.Text = AppInfo.RepositoryUrl.Replace("https://", "");
        RepoLink.NavigateUri = new Uri(AppInfo.RepositoryUrl);
        FullVersionText.Text = AppInfo.VersionDisplay;
        RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        DescriptionText.Text = AppInfo.Description;
        CopyrightText.Text = AppInfo.Copyright;
    }

    /// <summary>Plain-text summary for bug reports.</summary>
    public static string DetailsText() =>
        $"{AppInfo.Product} {AppInfo.VersionDisplay}\n" +
        $"Author: {AppInfo.Author} <{AppInfo.AuthorEmail}>\n" +
        $"Source: {AppInfo.RepositoryUrl}\n" +
        $"Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Couldn't open {url}:\n{ex.Message}", "LaserForge"); }
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        OpenUrl(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DetailsText());
        ((System.Windows.Controls.Button)sender).Content = "Copied ✓";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
