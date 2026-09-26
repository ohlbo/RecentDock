using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using RecentDock.Core.Storage;

namespace RecentDock.App;

/// <summary>
/// Applies a translucent backdrop to the panel.
///
/// Two independent implementations are kept because they do not work equally well on
/// every machine, and only one of them could be verified from the outside:
///
///   Dwm          DWMWA_SYSTEMBACKDROP_TYPE. The modern, documented route. On the
///                machine this was developed against it reports success for every
///                attribute and produces no visible material at all, so "returns
///                S_OK" is not evidence that anything rendered.
///   Accent       SetWindowCompositionAttribute with ACCENT_ENABLE_ACRYLICBLURBEHIND.
///                The undocumented but widely used route, and what most WPF acrylic
///                implementations actually rely on. It also carries its own tint, so
///                it does not depend on DWM picking a light or dark variant.
///
/// Because the failure mode is identical either way - a panel that looks painted on
/// black - the choice is exposed as a setting rather than guessed at.
/// </summary>
public static class AcrylicBackdrop
{
    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE (19 on current builds, 20 on 20H1).</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11).</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWA_BORDER_COLOR (Windows 11).</summary>
    private const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE (Windows 11 22H2+).</summary>
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>DWMWA_NCRENDERING_POLICY (Windows Vista+).</summary>
    private const int DwmwaNcRenderingPolicy = 2;

    /// <summary>DWMNCRP_DISABLED; stops DWM rendering the non-client frame.</summary>
    private const int DwmNcRenderingDisabled = 1;

    /// <summary>
    /// DWMWA_COLOR_NONE. Tells DWM not to draw its own border colour.
    /// </summary>
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    private const int DwmWindowCornerPreferenceRound = 2;

    /// <summary>DWMSBT_MAINWINDOW: mica, the material used by normal app windows.</summary>
    public const int BackdropMica = 2;

    /// <summary>DWMSBT_TRANSIENTWINDOW: acrylic, more translucent.</summary>
    public const int BackdropAcrylic = 3;

    /// <summary>
    /// Windows 11 22H2. DWMWA_SYSTEMBACKDROP_TYPE shipped with it; earlier builds are
    /// treated as unsupported and fall back to the accent route.
    /// </summary>
    private const int Windows11_22H2Build = 22621;

    /// <summary>Apply the configured backdrop method.</summary>
    public static bool Apply(Window window, UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.UseAcrylicBackdrop)
        {
            return false;
        }

        // Background="Transparent" in XAML is not sufficient for an HWND-backed
        // WPF window. Unless the HwndSource composition target is transparent too,
        // alpha in the visual tree is blended against an opaque black client area.
        // That is the exact failure mode where the opacity slider reveals more black
        // instead of more desktop.
        PrepareTransparentClient(window, settings.BackdropMethod == BackdropMethodKind.Dwm);

        return settings.BackdropMethod switch
        {
            BackdropMethodKind.Accent => ApplyAccent(window, settings),
            _ => ApplyDwm(window, settings),
        };
    }

    private static void PrepareTransparentClient(Window window, bool extendDwmFrame)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }

        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        if (!extendDwmFrame)
        {
            return;
        }

        // A negative margin extends the DWM surface through the whole client area.
        // Without it the backdrop can be accepted by DWM but remain confined to the
        // (now hidden) non-client frame, leaving WPF to composite over black.
        var margins = new Margins(-1);
        try
        {
            DwmExtendFrameIntoClientArea(source.Handle, ref margins);
        }
        catch (Exception)
        {
            // The Accent path remains available on machines where this API fails.
        }
    }

    /// <summary>
    /// Modern route: ask DWM for a system backdrop material.
    /// </summary>
    private static bool ApplyDwm(Window window, UiSettings settings)
    {
        if (!IsSystemBackdropSupported())
        {
            return false;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        SetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerPreferenceRound);

        // The frame and the material pull in opposite directions: DWM appears to need
        // the framish window styles to composite a backdrop at all, so the frame is
        // suppressed through attributes instead of by clearing styles.
        SetAttribute(handle, DwmwaBorderColor, DwmwaColorNone);

        if (settings.SuppressNonClientFrame)
        {
            SetAttribute(handle, DwmwaNcRenderingPolicy, DwmNcRenderingDisabled);
        }

        SetAttribute(handle, DwmwaUseImmersiveDarkMode, settings.UseDarkTheme ? 1 : 0);

        int material = settings.BackdropMaterial switch
        {
            BackdropMaterialKind.Mica => BackdropMica,
            _ => BackdropAcrylic,
        };

        return SetAttribute(handle, DwmwaSystemBackdropType, material);
    }

    /// <summary>
    /// Legacy route: SetWindowCompositionAttribute with an acrylic blur.
    ///
    /// Carries its own tint colour, which is the useful difference - the material's
    /// lightness no longer depends on DWM resolving the app's theme.
    /// </summary>
    private static bool ApplyAccent(Window window, UiSettings settings)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        SetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerPreferenceRound);
        SetAttribute(handle, DwmwaBorderColor, DwmwaColorNone);

        if (settings.SuppressNonClientFrame)
        {
            SetAttribute(handle, DwmwaNcRenderingPolicy, DwmNcRenderingDisabled);
        }

        // ABGR, and the alpha controls the material's tint strength. This stays
        // independent from the user-facing panel opacity: MainWindow.Opacity is the
        // final compositor-level control and therefore produces a predictable result
        // on every supported backdrop implementation.
        byte tintAlpha = (byte)Math.Clamp(settings.AccentTintOpacity * 255.0, 0, 255);

        uint gradientColor = settings.UseDarkTheme
            ? (uint)((tintAlpha << 24) | (0x1F << 16) | (0x1B << 8) | 0x1A)   // AABBGGRR
            : (uint)((tintAlpha << 24) | (0xFF << 16) | (0xFF << 8) | 0xFF);

        var accent = new AccentPolicy
        {
            AccentState = AccentState.EnableAcrylicBlurBehind,
            AccentFlags = 2,
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(accent, buffer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.AccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };

            return SetWindowCompositionAttribute(handle, ref data);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Report why a backdrop is or is not visible.
    ///
    /// Three independent things must hold, and a failure in any one looks identical on
    /// screen:
    ///
    ///   1. The window's client area must be transparent. A software-rendered window is
    ///      composited with an opaque black client area, so no material can show.
    ///   2. DWMSBT_* support must be present.
    ///   3. The attribute calls must succeed.
    ///
    /// Note the limit of this: all three can pass while nothing renders, which is
    /// exactly what happened here. Treat it as a filter, not as proof.
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

        parts.Add($"dwmComposition={IsCompositionEnabled()}");
        parts.Add($"win11_22H2+={IsSystemBackdropSupported()}");
        parts.Add($"windowBg={(window.Background is null ? "null" : window.Background.ToString())}");

        IntPtr handle = new WindowInteropHelper(window).Handle;
        parts.Add($"handle={handle != IntPtr.Zero}");

        if (handle != IntPtr.Zero)
        {
            parts.Add($"backdropAttr={TrySetBackdrop(handle, BackdropAcrylic)}");
            parts.Add($"cornerAttr={SetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerPreferenceRound)}");
            parts.Add($"borderColorAttr={SetAttribute(handle, DwmwaBorderColor, DwmwaColorNone)}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>Test a specific backdrop material, for A/B comparison.</summary>
    public static bool TrySetBackdrop(IntPtr handle, int backdropType)
        => handle != IntPtr.Zero && SetAttribute(handle, DwmwaSystemBackdropType, backdropType);

    /// <summary>Force the material's light or dark variant, for A/B comparison.</summary>
    public static bool TrySetDarkMode(IntPtr handle, bool dark)
        => handle != IntPtr.Zero && SetAttribute(handle, DwmwaUseImmersiveDarkMode, dark ? 1 : 0);

    /// <summary>Turn non-client rendering on or off, for A/B comparison.</summary>
    public static bool TrySetNcRendering(IntPtr handle, bool enabled)
        => handle != IntPtr.Zero
           && SetAttribute(handle, DwmwaNcRenderingPolicy, enabled ? 2 : DwmNcRenderingDisabled);

    /// <summary>
    /// Report the window styles that decide whether Windows treats the window as
    /// framed.
    ///
    /// WS_CAPTION and WS_THICKFRAME are the two bits that matter. WPF adds
    /// WS_THICKFRAME for a resizable window even with WindowStyle="None", and DWM
    /// consults those bits when deciding whether to render a frame.
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
        const long WS_EX_TOOLWINDOW = 0x00000080;
        const long WS_EX_APPWINDOW = 0x00040000;

        return $"CAPTION={(style & WS_CAPTION) != 0}"
            + $" THICKFRAME={(style & WS_THICKFRAME) != 0}"
            + $" BORDER={(style & WS_BORDER) != 0}"
            + $" EX_CLIENTEDGE={(exStyle & WS_EX_CLIENTEDGE) != 0}"
            + $" EX_WINDOWEDGE={(exStyle & WS_EX_WINDOWEDGE) != 0}"
            + $" EX_TOOLWINDOW={(exStyle & WS_EX_TOOLWINDOW) != 0}"
            + $" EX_APPWINDOW={(exStyle & WS_EX_APPWINDOW) != 0}";
    }

    /// <summary>
    /// Keep the panel out of both the taskbar and the Alt+Tab application switcher.
    ///
    /// ShowInTaskbar=false normally applies WS_EX_TOOLWINDOW by itself, but the
    /// panel also rewrites native frame styles for its transparent borderless
    /// appearance. Reasserting the two relevant bits after those changes makes the
    /// tray-only behaviour explicit and prevents a later style rewrite from adding
    /// WS_EX_APPWINDOW back.
    /// </summary>
    public static void EnsureTrayUtilityStyles(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const long WS_EX_TOOLWINDOW = 0x00000080;
        const long WS_EX_APPWINDOW = 0x00040000;

        long exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        long newExStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        if (newExStyle == exStyle)
        {
            return;
        }

        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(newExStyle));
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Whether the native styles exclude this window from Alt+Tab.</summary>
    public static bool IsTrayUtilityWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        const long WS_EX_TOOLWINDOW = 0x00000080;
        const long WS_EX_APPWINDOW = 0x00040000;
        long exStyle = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        return (exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0;
    }

    /// <summary>
    /// Clear the frame-ish window styles.
    ///
    /// This removes the system frame, but it is OFF by default: clearing WS_THICKFRAME
    /// also appeared to stop DWM compositing any backdrop material, which is why the
    /// frame is normally suppressed through DWMWA_* attributes instead.
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

        // Style changes only take effect after the frame has been recalculated.
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

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

    /// <summary>True on Windows 11 22H2 or later.</summary>
    public static bool IsSystemBackdropSupported()
    {
        try
        {
            // Environment.OSVersion reports 10.0 for Windows 11, so the build number is
            // the only usable signal.
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
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public Margins(int value)
        {
            Left = value;
            Right = value;
            Top = value;
            Bottom = value;
        }

        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll", SetLastError = false)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", SetLastError = false)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("dwmapi.dll", SetLastError = false)]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = false)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = false)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
/// <summary>
/// Window dragging and edge resizing for a chrome-less window.
///
/// Needed because WindowChrome is not used (it made the client area opaque and hid the
/// backdrop material), so nothing provides resize borders. This restores them with the
/// standard WM_NCHITTEST contract, which is what Windows itself uses - and it keeps
/// working even when WS_THICKFRAME has been cleared, because the hit test result is
/// what Windows acts on.
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
