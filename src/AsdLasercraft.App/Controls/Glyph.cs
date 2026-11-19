using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AsdLasercraft.App.Controls;

/// <summary>
/// A line icon (24 × 24 geometry from Themes/Icons.xaml) with optional label. The stroke follows the
/// control's Foreground, which it inherits from the button it sits in, so hover/disabled colours just work.
/// Usage: &lt;c:Glyph Data="{StaticResource I.Play}" Text="Start" /&gt;
/// </summary>
public sealed class Glyph : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Glyph), new PropertyMetadata(null));

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(Glyph), new PropertyMetadata(null));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Glyph), new PropertyMetadata(18.0));

    public static readonly DependencyProperty StrokeWidthProperty =
        DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(Glyph), new PropertyMetadata(2.0));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Rendered icon size in device-independent pixels.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Stroke thickness on the 24-unit grid (2 = default weight).</summary>
    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public Glyph()
    {
        Focusable = false;
        IsTabStop = false;
    }
}
