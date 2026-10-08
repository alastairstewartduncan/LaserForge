using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace LaserForge.App.Dialogs;

/// <summary>Small code-built form dialog for quick parameter entry (text tool, test grid, etc.).</summary>
public sealed class FormDialog : Window
{
    private readonly Grid _grid = new() { Margin = new Thickness(12) };
    private readonly Dictionary<string, FrameworkElement> _fields = new();

    public FormDialog(string title, Window? owner)
    {
        Title = title;
        Owner = owner;
        Icon = owner?.Icon;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        Content = _grid;
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

    public FormDialog Text(string label, string value = "")
    {
        AddRow(label, new TextBox { Text = value, MinWidth = 200 });
        return this;
    }

    public FormDialog Number(string label, double value)
    {
        AddRow(label, new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Tag = "number" });
        return this;
    }

    public FormDialog Choice<T>(string label, IEnumerable<T> items, T selected)
    {
        var cb = new ComboBox { ItemsSource = items.ToList(), SelectedItem = selected };
        AddRow(label, cb);
        return this;
    }

    public FormDialog Check(string label, bool value)
    {
        AddRow(label, new CheckBox { IsChecked = value });
        return this;
    }

    public bool Show(out FormDialog result)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            foreach (var (label, field) in _fields)
                if (field is TextBox tb && tb.Tag as string == "number" && !double.TryParse(tb.Text, out _))
                {
                    MessageBox.Show(this, $"'{label}' must be a number.", Title);
                    return;
                }
            DialogResult = true;
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
        int row = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, row);
        Grid.SetColumnSpan(buttons, 2);
        _grid.Children.Add(buttons);

        result = this;
        Loaded += (_, _) => (_fields.Values.FirstOrDefault() as Control)?.Focus();
        return ShowDialog() == true;
    }

    public string GetText(string label) => ((TextBox)_fields[label]).Text;

    public double GetNumber(string label, double fallback) =>
        double.TryParse(GetText(label), NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : fallback;

    public int GetInt(string label, int fallback) => (int)Math.Round(GetNumber(label, fallback));

    public T GetChoice<T>(string label) => (T)((ComboBox)_fields[label]).SelectedItem;

    public bool GetCheck(string label) => ((CheckBox)_fields[label]).IsChecked == true;
}
