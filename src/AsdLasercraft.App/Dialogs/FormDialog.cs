using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using AsdLasercraft.App.Services;

namespace AsdLasercraft.App.Dialogs;

/// <summary>Small code-built form dialog for quick parameter entry (text tool, test grid, arrays, etc.).</summary>
public sealed class FormDialog : Window
{
    private readonly Grid _grid = new() { Margin = new Thickness(20, 16, 20, 18) };
    private readonly Dictionary<string, FrameworkElement> _fields = new();

    public FormDialog(string title, Window? owner)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = title;
        Owner = owner;
        Icon = owner?.Icon;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        Content = _grid;
        WindowTheme.ApplyDark(this);
    }

    private void AddRow(string label, FrameworkElement field)
    {
        int row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new Label { Content = label };
        Grid.SetRow(l, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        _grid.Children.Add(l);
        _grid.Children.Add(field);
        _fields[label] = field;
    }

    private void AddSpanning(UIElement element)
    {
        int row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(element, row);
        Grid.SetColumnSpan(element, 2);
        _grid.Children.Add(element);
    }

    /// <summary>A muted explanatory line spanning both columns.</summary>
    public FormDialog Hint(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(2, 2, 2, 10) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        AddSpanning(tb);
        return this;
    }

    /// <summary>A small section heading.</summary>
    public FormDialog Section(string text)
    {
        var tb = new TextBlock { Text = text.ToUpperInvariant() };
        tb.SetResourceReference(StyleProperty, "Caption");
        AddSpanning(tb);
        return this;
    }

    public FormDialog Text(string label, string value = "", string? tooltip = null)
    {
        AddRow(label, new TextBox { Text = value, MinWidth = 200, ToolTip = tooltip });
        return this;
    }

    public FormDialog Number(string label, double value, string? tooltip = null)
    {
        AddRow(label, new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Tag = "number", ToolTip = tooltip });
        return this;
    }

    public FormDialog Choice<T>(string label, IEnumerable<T> items, T selected)
    {
        var cb = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected };
        AddRow(label, cb);
        return this;
    }

    public FormDialog Check(string label, bool value, string? tooltip = null)
    {
        AddRow(label, new CheckBox { IsChecked = value, ToolTip = tooltip });
        return this;
    }

    public bool Show(out FormDialog result)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 90 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) =>
        {
            foreach (var (label, field) in _fields)
                if (field is TextBox tb && tb.Tag as string == "number" &&
                    !double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _) &&
                    !double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    MessageBox.Show(this, $"'{label}' must be a number.", Title);
                    return;
                }
            DialogResult = true;
        };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 });
        buttons.Children.Add(ok);
        AddSpanning(buttons);

        result = this;
        Loaded += (_, _) => (_fields.Values.FirstOrDefault() as Control)?.Focus();
        return ShowDialog() == true;
    }

    public string GetText(string label) => ((TextBox)_fields[label]).Text;

    public double GetNumber(string label, double fallback)
    {
        var text = GetText(label);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ||
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? v : fallback;
    }

    public int GetInt(string label, int fallback) => (int)Math.Round(GetNumber(label, fallback));

    public T GetChoice<T>(string label) => (T)((ComboBox)_fields[label]).SelectedItem;

    public bool GetCheck(string label) => ((CheckBox)_fields[label]).IsChecked == true;
}
