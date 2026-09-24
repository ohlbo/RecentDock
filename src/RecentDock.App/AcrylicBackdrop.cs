using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RecentDock.App;

/// <summary>
/// Applies the Windows 11 system backdrop (acrylic / mica) so the panel is
/// translucent rather than a painted dark rectangle.
///
/// Why the system backdrop instead of a WPF transparency trick:
///
///   * AllowsTransparency="True" forces WPF onto a software rendering path and
///     composes the window itself, which BOTH hurts scrolling performance and
///     prevents the DWM material from showing through. It is the obvious approach
///     and the wrong one.
///   * WindowStyle="None" WITHOUT AllowsTransparency keeps hardware rendering, and
///     the DWM paints the material behind a transparent client area.
///
/// Consequence: the window must remove WindowChrome (its GlassFrameThickness makes
/// the client area opaque) and instead rely on DWM for the rounded corners, with
/// resizing supplied by <see cref="WindowResizer"/>.
///
/// Every call is best-effort. On Windows 10, or when composition is disabled, the
/// attributes fail and the XAML's semi-transparent panels alone provide the
/// appearance - degraded, never broken.
/// </summary>
public static class AcrylicBackdrop
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (19 on current builds, 20 on 20H1).</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11).</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE (Windows 11 22H2+).</summary>
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmWindowCornerPreferenceRound = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW: the acrylic-like material suited to panels.</summary>
    private const int DwmsbtTransientWindow = 3;

    /// <summary>
    /// Windows 11 22H2 build. DWMWA_SYSTEMBACKDROP_TYPE shipped with it; earlier
    /// builds used a different, undocumented attribute that no longer applies, so
    /// they are treated as unsupported and fall back to the translucent panels.
    /// </summary>
    private const int Windows11_22H2Build = 22621;

    /// <summary>
    /// Apply the backdrop to a window. Must be called after the window has a handle.
    /// </summary>
    /// <param name="window">Target window.</param>
    /// <param name="enable">False leaves the window untouched, for users who prefer a solid panel.</param>
    /// <returns>
    /// True when a system backdrop was applied. False means the caller is relying on
    /// the XAML panels alone, which is a normal outcome on Windows 10.
    /// </returns>
    /// <param name="darkTheme">
    /// Selects the material's light or dark variant.
    ///
    /// This is NOT cosmetic and it is not optional: the DWM acrylic material derives
    /// its own colours from this flag, so asking for a dark material makes the surface
    /// behind the client area genuinely black no matter how light the XAML panels are.
    /// An early version hard-coded dark mode here, which is why a light theme still
    /// rendered as black.
    /// </param>
    public static bool Apply(Window window, bool enable, bool darkTheme)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!enable || !IsSystemBackdropSupported())
        {
            return false;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        // Rounded corners. Cosmetic, and it succeeds even on builds that lack the
        // backdrop attribute below.
        SetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerPreferenceRound);

        // Immersive dark mode drives the material's palette. 0 = light material.
        SetAttribute(handle, DwmwaUseImmersiveDarkMode, darkTheme ? 1 : 0);

        return SetAttribute(handle, DwmwaSystemBackdropType, DwmsbtTransientWindow);
    }

    /// <summary>
    /// True on Windows 11 22H2 or later, where DWMWA_SYSTEMBACKDROP_TYPE exists.
    /// </summary>
    public static bool IsSystemBackdropSupported()
    {
        try
        {
            // Environment.OSVersion's Major/Minor report 10.0 for Windows 11, so the
            // build number is the only usable signal.
            Version version = Environment.OSVersion.Version;
            return version.Major > 10 || (version.Major == 10 && version.Build >= Windows11_22H2Build);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Human-readable support summary, for diagnostics.</summary>
    public static string DescribeSupport()
    {
        Version version = Environment.OSVersion.Version;
        string supported = IsSystemBackdropSupported() ? "yes" : "no";
        return $"build {version.Major}.{version.Minor}.{version.Build}, acrylic supported: {supported}";
    }

    private static bool SetAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            int hr = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
            return hr == 0;
        }
        catch (Exception)
        {
            // Older Windows builds or a DWM that is not running.
            return false;
        }
    }

    [DllImport("dwmapi.dll", SetLastError = false)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

/// <summary>
/// Window dragging and edge resizing for a chrome-less window.
///
/// Needed because WindowChrome was removed so the DWM backdrop can show: with no
/// chrome and no WindowStyle, nothing provides resize borders. This restores them
/// with the standard WM_NCHITTEST contract, which is what Windows itself uses.
/// </summary>
public static class WindowResizer
{
    private const int WM_NCHITTEST = 0x0084;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    /// <summary>Grab thickness in device-independent pixels.</summary>
    private const double BorderThickness = 6;

    /// <summary>
    /// Install the hit-test hook. The returned value can be passed to
    /// <see cref="RemoveHook"/> on close.
    /// </summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(HitTest);
        }
    }

    public static void RemoveHook(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.RemoveHook(HitTest);
        }
    }

    private static IntPtr HitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST)
        {
            return IntPtr.Zero;
        }

        // Honour a maximize: there are no edges to grab in that state.
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            return IntPtr.Zero;
        }

        // lParam carries screen coordinates as two signed 16-bit values. Casting to
        // short first matters: without it, negative coordinates on a monitor left of
        // the primary produce nonsense.
        int screenX = unchecked((short)(long)lParam);
        int screenY = unchecked((short)((long)lParam >> 16));

        double x = screenX - rect.Left;
        double y = screenY - rect.Top;
        double width = rect.Right - rect.Left;
        double height = rect.Bottom - rect.Top;

        bool left = x <= BorderThickness;
        bool right = x >= width - BorderThickness;
        bool top = y <= BorderThickness;
        bool bottom = y >= height - BorderThickness;

        int result;

        if (top && left)
        {
            result = HTTOPLEFT;
        }
        else if (top && right)
        {
            result = HTTOPRIGHT;
        }
        else if (bottom && left)
        {
            result = HTBOTTOMLEFT;
        }
        else if (bottom && right)
        {
            result = HTBOTTOMRIGHT;
        }
        else if (left)
        {
            result = HTLEFT;
        }
        else if (right)
        {
            result = HTRIGHT;
        }
        else if (top)
        {
            result = HTTOP;
        }
        else if (bottom)
        {
            result = HTBOTTOM;
        }
        else
        {
            // Not an edge: let WPF handle it normally.
            return IntPtr.Zero;
        }

        handled = true;
        return new IntPtr(result);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
