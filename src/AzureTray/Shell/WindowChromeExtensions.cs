using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AzureTray.Shell;

// Switches the OS-rendered non-client area to match our WPF dark theme.
// Beyond just dark mode, this pins the caption colour, border colour, and
// caption text colour to the exact tokens our theme uses — without that,
// Windows 11 paints the title bar with its own "Mica"/system dark shade and
// adds a thin white active-window border at the very top edge, both of which
// stand out as foreign next to our #181818 surface.
internal static class WindowChromeExtensions
{
    // DWMWINDOWATTRIBUTE values. The older "dark mode" attribute (20 / pre-20H1
    // alias 19) just flips the title bar to dark. The 34/35/36 attributes let
    // us pin specific colours so the title bar is *the same* dark as the rest
    // of the window — Windows 11 build 22000+.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    // Hex values from Resources/Theme.xaml. Kept in sync manually because the
    // DWM API needs COLORREFs (0x00BBGGRR), not WPF Color objects.
    private const int SurfaceBaseColorRef    = 0x00181818; // matches Color.Surface.Base
    private const int TextPrimaryColorRef    = 0x00F0F0F0; // matches Color.Text.Primary
    private const int BorderSubtleColorRef   = 0x002C2C2C; // matches Color.Border.Subtle

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void EnableDarkTitleBar(this Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            Apply(helper.Handle);
        }
        else
        {
            window.SourceInitialized += OnSourceInitialized;
        }
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) Apply(hwnd);
    }

    private static void Apply(IntPtr hwnd)
    {
        // Step 1: dark mode (older systems and a sensible fallback if the
        // colour-pinning attributes below aren't available).
        int useDark = 1;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        if (hr != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));
        }

        // Step 2: pin caption / border / text colours so they match our theme
        // exactly. On older OS builds these DWM attributes return non-zero
        // and we silently leave Windows' defaults in place.
        int caption = SurfaceBaseColorRef;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));

        int border = BorderSubtleColorRef;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        int text = TextPrimaryColorRef;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
    }
}
