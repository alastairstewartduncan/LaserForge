using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using AsdLasercraft.App.Services;
using AsdLasercraft.Core.Imaging;

namespace AsdLasercraft.App.Dialogs;

/// <summary>Photo → line art with a live preview. Runs on the PC; the preview uses a reduced copy for speed.</summary>
public sealed class LineArtWindow : Window
{
    private readonly byte[] _small;
    private readonly int _sw, _sh;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Slider _detail = MakeSlider(0.5), _strength = MakeSlider(0.5), _shading = MakeSlider(0);
    private readonly ToggleButton _before;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _version;
    private BitmapSourceCache? _lastPreview;

    public LineArtOptions Options => new() { Detail = _detail.Value, Strength = _strength.Value, Shading = _shading.Value };

    public LineArtWindow(Window owner, byte[] gray, int w, int h)
    {
        SetResourceReference(StyleProperty, "AppWindow");
        Title = "Photo to line art";
        Owner = owner;
        Icon = owner.Icon;
        Width = 900; Height = 620; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Preview on a copy no bigger than 900 px so slider changes feel instant.
        double s = Math.Min(1, 900.0 / Math.Max(w, h));
        _sw = Math.Max(1, (int)(w * s)); _sh = Math.Max(1, (int)(h * s));
        _small = s < 1 ? ImageProcessor.Resample(gray, w, h, _sw, _sh) : gray;

        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });

        var frame = new Border { CornerRadius = new CornerRadius(12), Background = Brushes.White, Padding = new Thickness(8), Margin = new Thickness(0, 0, 16, 0), Child = _image };
        root.Children.Add(frame);

        var side = new StackPanel();
        Grid.SetColumn(side, 1);
        root.Children.Add(side);
        side.Children.Add(Muted("Turns the photo into clean dark lines on white – these engrave much crisper than a dithered photo. Tip: remove the background first."));
        AddSlider(side, "DETAIL", _detail, "Higher keeps finer lines and texture.");
        AddSlider(side, "LINE STRENGTH", _strength, "Higher gives bolder lines and picks up fainter edges.");
        AddSlider(side, "SHADING", _shading, "Fills the darkest areas (hair, shadows) solid.");

        _before = new ToggleButton { Margin = new Thickness(2, 14, 2, 2), HorizontalAlignment = HorizontalAlignment.Left };
        _before.SetResourceReference(StyleProperty, "PillToggle");
        _before.Content = new TextBlock { Text = "Show original", FontSize = 12 };
        _before.Click += (_, _) => Refresh();
        side.Children.Add(_before);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 });
        var ok = new Button { Content = "Apply", IsDefault = true, MinWidth = 110 };
        ok.SetResourceReference(StyleProperty, "PrimaryButton");
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        side.Children.Add(buttons);

        Content = root;
        WindowTheme.ApplyDark(this);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        foreach (var sl in new[] { _detail, _strength, _shading }) sl.ValueChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        Loaded += (_, _) => Refresh();
    }

    private sealed record BitmapSourceCache(System.Windows.Media.Imaging.BitmapSource Bitmap);

    private static Slider MakeSlider(double value) => new() { Minimum = 0, Maximum = 1, Value = value, Margin = new Thickness(2, 2, 2, 0) };

    private static TextBlock Muted(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 8), FontSize = 12.5 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        return tb;
    }

    private static void AddSlider(Panel panel, string caption, Slider slider, string hint)
    {
        var c = new TextBlock { Text = caption };
        c.SetResourceReference(StyleProperty, "Caption");
        panel.Children.Add(c);
        panel.Children.Add(slider);
        var h = Muted(hint);
        h.FontSize = 11.5;
        panel.Children.Add(h);
    }

    private void Refresh()
    {
        if (_before.IsChecked == true)
        {
            _image.Source = ImageLoader.ToBitmap(_small, _sw, _sh);
            return;
        }
        int version = ++_version;
        var o = Options;
        Task.Run(() => LineArt.Apply(_small, _sw, _sh, o)).ContinueWith(t =>
        {
            if (t.IsFaulted || version != _version) return;
            _lastPreview = new BitmapSourceCache(ImageLoader.ToBitmap(t.Result, _sw, _sh));
            _image.Source = _lastPreview.Bitmap;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    internal void SmokeSet(double detail, double strength, double shading)
    {
        _detail.Value = detail; _strength.Value = strength; _shading.Value = shading;
        _image.Source = ImageLoader.ToBitmap(LineArt.Apply(_small, _sw, _sh, Options), _sw, _sh);
    }
}
