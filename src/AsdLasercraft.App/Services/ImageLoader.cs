using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AsdLasercraft.App.Services;

public static class ImageLoader
{
    public const string FileFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*";

    /// <summary>Loads any WPF-supported bitmap as 8-bit greyscale, compositing transparency onto white.</summary>
    public static (byte[] Gray, int Width, int Height) LoadGray(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        var bgra = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);

        var gray = new byte[w * h];
        for (int i = 0; i < w * h; i++)
        {
            double b = px[i * 4], g = px[i * 4 + 1], r = px[i * 4 + 2], a = px[i * 4 + 3] / 255.0;
            double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;     // Rec. 709 luma
            gray[i] = (byte)Math.Clamp(Math.Round(lum * a + 255 * (1 - a)), 0, 255);
        }
        return (gray, w, h);
    }

    public static BitmapSource ToBitmap(byte[] gray, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Gray8, null, gray, w);
        bmp.Freeze();
        return bmp;
    }
}
