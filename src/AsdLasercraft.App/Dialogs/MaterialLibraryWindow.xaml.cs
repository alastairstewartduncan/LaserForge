using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Tools;

namespace AsdLasercraft.App.Dialogs;

/// <summary>
/// Browse, edit and apply material presets. Edits are saved to %AppData%\ASD Lasercraft\materials.json when
/// the window closes. "Apply" copies the preset onto the layer that was selected when the window opened.
/// </summary>
public partial class MaterialLibraryWindow : Window
{
    private readonly MaterialLibrary _library;
    private readonly string _path;
    private readonly Layer? _layer;
    private readonly ObservableCollection<MaterialPreset> _items;
    private readonly ICollectionView _view;

    /// <summary>The preset applied, if the user pressed Apply.</summary>
    public MaterialPreset? Applied { get; private set; }

    public MaterialLibraryWindow(Window owner, MaterialLibrary library, string path, Layer? layer)
    {
        InitializeComponent();
        Owner = owner;
        Icon = owner.Icon;
        WindowTheme.ApplyDark(this);
        _library = library;
        _path = path;
        _layer = layer;
        _items = new ObservableCollection<MaterialPreset>(library.Presets);
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.SortDescriptions.Add(new SortDescription(nameof(MaterialPreset.Material), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(MaterialPreset.ThicknessMm), ListSortDirection.Ascending));
        PresetsGrid.ItemsSource = _view;
        ModeColumn.ItemsSource = Enum.GetValues<LayerMode>();

        if (layer == null)
        {
            ApplyButton.IsEnabled = false;
            ApplyGlyph.Text = "Select a layer to apply";
        }
        else ApplyGlyph.Text = $"Apply to '{layer.Name}'";
        Closing += (_, _) => Save();
    }

    private MaterialPreset? Current => PresetsGrid.SelectedItem as MaterialPreset;

    private void Save()
    {
        PresetsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        _library.Presets = _items.ToList();
        try { _library.Save(_path); }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't save the material library:\n{ex.Message}", Title); }
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        _view.Filter = q.Length == 0 ? null : o => o is MaterialPreset p &&
            (p.Material.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Operation.Contains(q, StringComparison.OrdinalIgnoreCase) ||
             p.Notes.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } preset || _layer == null) return;
        PresetsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        Applied = preset;
        DialogResult = true;
    }

    private void PresetsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking the row header area applies; double-clicking a cell edits it (handled by the grid).
        if (e.OriginalSource is FrameworkElement { DataContext: MaterialPreset } && PresetsGrid.CurrentColumn == null) Apply_Click(sender, e);
    }

    private void PresetsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var p in _items)
            {
                p.SpeedMmMin = Math.Clamp(p.SpeedMmMin, 1, 100_000);
                p.PowerPercent = Math.Clamp(p.PowerPercent, 0, 100);
                p.Passes = Math.Clamp(p.Passes, 1, 100);
                p.LineIntervalMm = Math.Clamp(p.LineIntervalMm, 0.02, 5);
            }
        });
    }

    private void AddFromLayer_Click(object sender, RoutedEventArgs e)
    {
        if (_layer == null) { MessageBox.Show(this, "Select a layer in the main window first.", Title); return; }
        var form = new FormDialog("Save layer as preset", this)
            .Hint($"Saves the speed, power, passes and line interval of layer '{_layer.Name}'.")
            .Text("Material", Current?.Material ?? "Plywood")
            .Number("Thickness (mm)", Current?.ThicknessMm ?? 3)
            .Text("Operation", _layer.Mode switch { LayerMode.Line => "Cut", LayerMode.Image => "Photo", _ => "Engrave" })
            .Text("Notes", "");
        if (!form.Show(out var r)) return;
        var preset = MaterialPreset.FromLayer(_layer, r.GetText("Material"), r.GetNumber("Thickness (mm)", 0), r.GetText("Operation"));
        preset.Notes = r.GetText("Notes");
        _items.Add(preset);
        PresetsGrid.SelectedItem = preset;
        PresetsGrid.ScrollIntoView(preset);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        var copy = p.Clone();
        copy.Operation += " (copy)";
        _items.Add(copy);
        PresetsGrid.SelectedItem = copy;
        PresetsGrid.ScrollIntoView(copy);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } p) return;
        if (MessageBox.Show(this, $"Delete '{p.DisplayName}'?", Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            _items.Remove(p);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Add back any of the built-in presets you have deleted? Your own presets are kept.", Title,
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var d in MaterialLibrary.CreateDefault().Presets)
            if (!_items.Any(p => p.DisplayName == d.DisplayName)) _items.Add(d);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
