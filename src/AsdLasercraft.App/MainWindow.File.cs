using System.IO;
using System.Windows;
using System.Windows.Controls;
using AsdLasercraft.App.Dialogs;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Gcode;
using AsdLasercraft.Core.Import;
using AsdLasercraft.Core.Model;
using AsdLasercraft.Core.Serialization;
using Microsoft.Win32;
using Bounds = AsdLasercraft.Core.Geometry.Bounds;
using Vec2 = AsdLasercraft.Core.Geometry.Vec2;

namespace AsdLasercraft.App;

// File menu: new / open / save, imports, G-code export, recent files and drag & drop.
public partial class MainWindow
{
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var p = LaserProject.CreateDefault();
        p.Machine = _project.Machine.Clone(); // keep the user's machine setup
        _undo.Clear();
        _dirty = false;
        SetProject(p, null, fit: true);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new OpenFileDialog { Filter = ProjectFilter };
        if (dlg.ShowDialog(this) != true) return;
        OpenProject(dlg.FileName);
    }

    internal void OpenFromCommandLine(string path) => OpenProject(path);

    private void OpenProject(string path)
    {
        try
        {
            var p = ProjectSerializer.Load(path);
            _undo.Clear();
            _dirty = false;
            SetProject(p, path, fit: true);
            _settings.AddRecent(path);
            _settings.Save();
            RefreshRecentMenu();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open project:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject(_filePath);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProject(null);

    private bool SaveProject(string? path)
    {
        if (path == null)
        {
            var dlg = new SaveFileDialog { Filter = ProjectFilter, DefaultExt = ProjectSerializer.FileExtension };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            ProjectSerializer.Save(_project, path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save:\n{ex.Message}", "Save", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        _filePath = path;
        _dirty = false;
        UpdateTitle();
        _settings.AddRecent(path);
        _settings.Save();
        RefreshRecentMenu();
        ShowToast($"Saved {Path.GetFileName(path)}");
        return true;
    }

    private void RefreshRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var path in _settings.RecentFiles.Where(File.Exists))
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path };
            item.Click += (_, _) => { if (ConfirmDiscard()) OpenProject(path); };
            RecentMenu.Items.Add(item);
        }
        RecentMenu.IsEnabled = RecentMenu.Items.Count > 0;
    }

    private void ImportImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ImageLoader.FileFilter };
        if (dlg.ShowDialog(this) != true) return;
        ImportImage(dlg.FileName, null);
    }

    private void ImportImage(string path, Vec2? at)
    {
        try
        {
            var (gray, w, h) = ImageLoader.LoadGray(path);
            // Default: longest side 100 mm, clamped to the bed. Resize afterwards with the handles or W/H boxes.
            double longest = Math.Min(100, Math.Min(_project.Machine.BedWidthMm, _project.Machine.BedHeightMm) - 20);
            double wmm = w >= h ? longest : longest * w / h;
            double hmm = w >= h ? longest * h / w : longest;
            var img = new ImageShape
            {
                Name = Path.GetFileName(path),
                X = at?.X - wmm / 2 ?? 10, Y = at?.Y - hmm / 2 ?? 10, WidthMm = wmm, HeightMm = hmm,
                PixelWidth = w, PixelHeight = h, Gray = gray,
            };
            ExitSimulation();
            Surface.AddShape(img);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load image:\n{ex.Message}", "Import image", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSvg_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "SVG files (*.svg)|*.svg" };
        if (dlg.ShowDialog(this) != true) return;
        ImportSvg(dlg.FileName, null);
    }

    private void ImportSvg(string path, Vec2? at)
    {
        try
        {
            var paths = SvgImporter.Import(File.ReadAllText(path));
            if (paths.Count == 0)
            {
                MessageBox.Show(this, "No drawable paths found. If the SVG contains text, convert it to paths first.", "Import SVG");
                return;
            }
            var shape = new VectorShape(paths, Path.GetFileName(path));
            var bed = Bounds.FromRect(0, 0, _project.Machine.BedWidthMm, _project.Machine.BedHeightMm);
            if (at is { } p)
            {
                var b = shape.GetBounds();
                shape.MoveTo(p.X - b.Width / 2, p.Y - b.Height / 2);
            }
            else if (!bed.Contains(shape.GetBounds())) shape.MoveTo(10, 10);
            ExitSimulation();
            Surface.AddShape(shape);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not import SVG:\n{ex.Message}", "Import SVG", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnFilesDropped(string[] files, Vec2 at)
    {
        foreach (var file in files)
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ProjectSerializer.FileExtension)
            {
                if (ConfirmDiscard()) OpenProject(file);
                return;
            }
            if (ext == ".svg") ImportSvg(file, at);
            else if (ImageExtensions.Contains(ext)) ImportImage(file, at);
        }
    }

    private async void ExportGcode_Click(object sender, RoutedEventArgs e)
    {
        var prepared = await PrepareJobAsync();
        if (prepared is not { } p) return;
        var confirm = new JobConfirmWindow(this, p.Job, p.Issues, Subtitle(p.Job), startText: "Export G-code", safetyNote: false, offerSimulate: true);
        if (confirm.ShowDialog() != true) return;
        if (confirm.Result == JobConfirmResult.Simulate) { await EnterSimulationAsync(); return; }

        var dlg = new SaveFileDialog
        {
            Filter = GcodeFilter,
            DefaultExt = ".gcode",
            FileName = Path.GetFileNameWithoutExtension(_filePath ?? "job") + ".gcode",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, p.Job.Text);
            ShowToast($"Exported {p.Job.Lines.Count:N0} lines · est. {FormatTime(p.Job.EstimatedTime)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not write the file:\n{ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
