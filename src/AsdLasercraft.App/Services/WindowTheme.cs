using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AsdLasercraft.App.Services;

/// <summary>
/// Makes the native title bar match the dark theme: immersive dark mode on Windows 10 20H1+ and a graphite
/// caption colour on Windows 11. Silently does nothing on older systems.
/// </summary>
public static class WindowTheme
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyDark(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, sizeof(int));
            int caption = ColorRef(0x14, 0x17, 0x1C), border = ColorRef(0x28, 0x2D, 0x37), text = ColorRef(0xE8, 0xEB, 0xF1);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
        }
        catch (Exception)
        {
            // dwmapi missing or attribute unsupported: keep the default title bar.
        }
    }

    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
