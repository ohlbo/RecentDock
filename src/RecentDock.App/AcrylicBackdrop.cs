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

    /// <summary>DWMWA_BORDER_COLOR (Windows 11).</summary>
    private const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_NCRENDERING_POLICY (Windows Vista+).</summary>
    private const int DwmwaNcRenderingPolicy = 2;

    /// <summary>
    /// DWMNCRP_DISABLED. Stops DWM rendering the non-client area at all.
    ///
    /// Setting DWMWA_BORDER_COLOR to "none" only removes the border COLOUR; DWM still
    /// renders the rest of the non-client frame, which on Windows 11 includes a 1px
    /// frame and a soft shadow around the whole window rect. That is the second, larger
    /// box that remains visible around a chrome-less window even after the border
    /// colour is suppressed.
    /// </summary>
    private const int DwmNcRenderingDisabled = 1;

    /// <summary>
    /// DWMWA_COLOR_NONE. Tells DWM not to draw its own border at all.
    ///
    /// Windows 11 draws a 1px frame plus an outer outline around every top-level
    /// window, including chrome-less ones. It is visible as a second, slightly larger
    /// box around the panel - which reads as "a Windows box behind my panel" and
    /// ruins the glass edge, since the panel's own rounded corner is drawn inside it.
    /// </summary>
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    private const int DwmWindowCornerPreferenceRound = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW: the acrylic-like material suited to panels.</summary>
    private const int DwmsbtTransientWindow = 3;

    /// <summary>
    /// Diagnose why a backdrop is or is not visible.
    ///
    /// Three independent things must all hold, and a failure in any one produces the
    /// same symptom - a panel that looks like it is painted on black:
    ///
    ///   1. The window's client area must actually be transparent. WPF falls back to
    ///      software rendering in some environments, and a software-rendered window is
    ///      composited with an opaque black client area, so the material behind it can
    ///      never show.
    ///   2. DWMSBT_* support must be present (Windows 11 22H2+).
    ///   3. DwmSetWindowAttribute must actually succeed - the call returns a failure
    ///      HRESULT rather than throwing.
    ///
    /// Reporting all three at once is what turns "it looks black" into an actionable
    /// cause.
    /// </summary>
    public static string Diagnose(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var parts = new List<string>();

        try
        {
            parts.Add($"renderTier={System.Windows.Media.RenderCapability.Tier >> 16}");
        }
        catch (Exception)
        {
            parts.Add("renderTier=?");
        }

        parts.Add($"hwAccel={System.Windows.Media.RenderCapability.IsPixelShaderVersionSupported(2, 0)}");
        parts.Add($"dwmComposition={IsCompositionEnabled()}");
        parts.Add($"win11_22H2+={IsSystemBackdropSupported()}");
        parts.Add($"windowBg={(window.Background is null ? "null" : window.Background.ToString())}");

        IntPtr handle = new WindowInteropHelper(window).Handle;
        parts.Add($"handle={handle != IntPtr.Zero}");

        if (handle != IntPtr.Zero)
        {
            parts.Add($"backdropAttr={TrySetBackdrop(handle, DwmsbtTransientWindow)}");
            parts.Add($"cornerAttr={SetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerPreferenceRound)}");
            parts.Add($"borderColorAttr={SetAttribute(handle, DwmwaBorderColor, DwmwaColorNone)}");
            parts.Add($"ncRenderingAttr={SetAttribute(handle, DwmwaNcRenderingPolicy, DwmNcRenderingDisabled)}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Test a specific backdrop material, for A/B comparison.</summary>
    public static bool TrySetBackdrop(IntPtr handle, int backdropType)
        => handle != IntPtr.Zero && SetAttribute(handle, DwmwaSystemBackdropType, backdropType);

    /// <summary>Expose the material constants so a test can try each one.</summary>
    public const int BackdropMica = 2;
    public const int BackdropAcrylic = 3;

    /// <summary>True when DWM composition is on; without it no material can render.</summary>
    public static bool IsCompositionEnabled()
    {
        try
        {
            return DwmIsCompositionEnabled(out bool enabled) == 0 && enabled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", SetLastError = false)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    /// <summary>
    /// Report the window styles that make Windows treat a window as framed.
    ///
    /// WS_CAPTION and WS_THICKFRAME are the two bits that matter. Even with
    /// WindowStyle="None", WPF adds WS_THICKFRAME for a resizable window, and DWM uses
    /// those bits to decide whether to render a frame - so they can explain a border
    /// that survives every DWMWA_* setting.
    /// </summary>
    public static string DescribeWindowStyles(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return "no handle";
        }

        long style = GetWindowLongPtr(handle, GWL_STYLE).ToInt64();
        long exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();

        const long WS_CAPTION = 0x00C00000;
        const long WS_THICKFRAME = 0x00040000;
        const long WS_BORDER = 0x00800000;
        const long WS_EX_CLIENTEDGE = 0x00000200;
        const long WS_EX_WINDOWEDGE = 0x00000100;

        return $"CAPTION={(style & WS_CAPTION) != 0}"
            + $" THICKFRAME={(style & WS_THICKFRAME) != 0}"
            + $" BORDER={(style & WS_BORDER) != 0}"
            + $" EX_CLIENTEDGE={(exStyle & WS_EX_CLIENTEDGE) != 0}"
            + $" EX_WINDOWEDGE={(exStyle & WS_EX_WINDOWEDGE) != 0}";
    }

    /// <summary>
    /// Strip the window styles that make Windows treat the window as framed.
    ///
    /// This is the piece that actually removes the system border, and it took a
    /// measurement to find: every DWMWA_* attribute can be set successfully and the
    /// frame still survives, because DWM decides whether to draw a frame from the
    /// window STYLE bits, not from those attributes.
    ///
    /// Measured on a chrome-less WPF window: CAPTION=False, THICKFRAME=True,
    /// EX_WINDOWEDGE=True. WS_THICKFRAME is added by WPF for a resizable window even
    /// with WindowStyle="None", and WS_EX_WINDOWEDGE adds a raised edge on top.
    /// WindowChrome would normally clear them, and this window deliberately does not
    /// use WindowChrome, so nothing did.
    ///
    /// Resizing is unaffected: it is implemented by WindowResizer through
    /// WM_NCHITTEST, which does not depend on these styles.
    /// </summary>
    public static void RemoveFrameStyles(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const long WS_THICKFRAME = 0x00040000;
        const long WS_EX_WINDOWEDGE = 0x00000100;

        long style = GetWindowLongPtr(handle, GWL_STYLE).ToInt64();
        long exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();

        long newStyle = style & ~WS_THICKFRAME;
        long newExStyle = exStyle & ~WS_EX_WINDOWEDGE;

        if (newStyle == style && newExStyle == exStyle)
        {
            return;
        }

        SetWindowLongPtr(handle, GWL_STYLE, new IntPtr(newStyle));
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(newExStyle));

        // Style changes only take effect after the frame has been recalculated;
        // without this the window can keep painting the old frame until it is resized.
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = false)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = false)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

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

        // Remove the system frame. Two attributes are needed, because they suppress
        // different parts of it:
        //
        //   DWMWA_NCRENDERING_POLICY = DISABLED  stops DWM rendering the non-client
        //                                        area, which is what draws the 1px
        //                                        frame plus the soft window shadow.
        //   DWMWA_BORDER_COLOR = NONE            removes the border colour on top of
        //                                        that.
        //
        // Only setting the border colour was not enough: the frame and shadow stayed.
        // The panel draws its own hairline (GlassBorderBrush), so nothing is lost.
        SetAttribute(handle, DwmwaNcRenderingPolicy, DwmNcRenderingDisabled);
        SetAttribute(handle, DwmwaBorderColor, DwmwaColorNone);

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
