using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using RecentDock.Core;
using RecentDock.Core.Interop;
using RecentDock.Core.Storage;

namespace RecentDock.App;

/// <summary>
/// Application entry point.
///
/// ShutdownMode is OnExplicitShutdown because this is a tray-resident utility:
/// with the default OnLastWindowClose, hiding or closing the panel would terminate
/// the process and take the tray icon with it.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private UiSettings _settings = new();

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// Declare per-monitor DPI awareness v2 before any window exists.
    ///
    /// This lives here rather than in app.manifest because the WinForms analyzer
    /// (WFAC010) rejects dpiAware entries in the manifest when UseWindowsForms is
    /// enabled, and WinForms is present only for NotifyIcon. The call must happen
    /// before the first window is created, which is why it is in the constructor
    /// rather than in OnStartup after other work.
    /// </summary>
    private static void DeclareDpiAwareness()
    {
        const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        try
        {
            // Fails harmlessly if awareness was already fixed by policy or by a
            // compatibility shim, which is why the result is ignored.
            SetProcessDpiAwarenessContext(new IntPtr(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));
        }
        catch (Exception)
        {
            // Not fatal: the panel still renders, just less sharply on mixed DPI.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DeclareDpiAwareness();

        DispatcherUnhandledException += (_, args) =>
        {
            // Log before showing anything. A dialog is easy to dismiss and its text
            // cannot be read back programmatically, which makes a crash report
            // impossible to act on; the file survives.
            string logPath = WriteCrashLog(args.Exception);

            MessageBox.Show(
                args.Exception.ToString() + $"\n\n已记录到：\n{logPath}",
                "RecentDock 发生未处理异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            RunSelfTest();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--watchtest", StringComparison.OrdinalIgnoreCase)))
        {
            RunWatchTest();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--removecomtest", StringComparison.OrdinalIgnoreCase)))
        {
            RunRemoveComTest();
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--uitest", StringComparison.OrdinalIgnoreCase)))
        {
            RunUiSmokeTest();
            return;
        }

        // Single instance must be checked before any window or tray icon is created,
        // otherwise the duplicate would flash a panel and add a second tray icon
        // before exiting.
        if (!SingleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        // Appearance must be applied before the first window is created, so the panel
        // is never painted with the default palette and then corrected.
        _settings = ConfigStore.Load().Sanitized(
            (int)SystemParameters.VirtualScreenLeft,
            (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth,
            (int)SystemParameters.VirtualScreenHeight);
        ThemeManager.Apply(_settings);

        CreateWindow();

        _tray = new TrayIcon();
        _tray.ToggleRequested += (_, _) => ToggleWindow();
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.WindowsSettingsRequested += (_, _) => OpenWindowsSettings();
        _tray.ExitRequested += (_, _) => ExitApplication();
        _tray.Initialize();

        UpdateTrayState();
    }

    private void CreateWindow()
    {
        _window = new MainWindow();
        _window.ScanCompleted += (_, count) => _tray?.UpdateState(_window.IsVisible, count);
        _window.AppearanceSettingsRequested += (_, _) => ShowSettings();
        _window.WindowsSettingsRequested += (_, _) => OpenWindowsSettings();
        _window.Closing += OnWindowClosing;
        _window.IsVisibleChanged += (_, _) => UpdateTrayState();

        // The window message that a second launch posts to reveal the panel. The
        // hook must be attached after the handle exists, which is why this lives
        // here rather than in OnStartup.
        var source = (System.Windows.Interop.HwndSource?)PresentationSource.FromVisual(_window);
        source?.AddHook(WindowMessageHook);

        // StartupUri is deliberately not used, so show the window explicitly.
        _window.Show();
        SingleInstance.RegisterMainWindow(_window);
    }

    /// <summary>
    /// Reveal the panel when a second launch asks for it. Everything else is passed
    /// through untouched.
    /// </summary>
    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == SingleInstance.ShowWindowMessage)
        {
            ShowWindowFromTray();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Closing the panel hides it instead of exiting: this is a tray utility, and a
    /// user who clicks the X expects the tray icon to remain. Exit is available from
    /// the tray menu.
    /// </summary>
    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        if (_window is not null)
        {
            // Save before hiding: the window still has valid bounds here, and this may
            // be the last moment we get if the process is later terminated without a
            // clean exit.
            _window.SaveState();
            _window.Hide();
        }

        UpdateTrayState();
    }

    private bool _exiting;

    private void ExitApplication()
    {
        _exiting = true;

        // Persist while the window still exists and has bounds.
        _window?.SaveState();

        // Dispose the tray icon before the process goes away, otherwise the icon
        // lingers in the notification area until the user hovers over it.
        _tray?.Dispose();
        _tray = null;

        SingleInstance.UnregisterMainWindow();
        Shutdown();
    }

    private void ShowWindowFromTray()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
        UpdateTrayState();
    }

    private void ToggleWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            // Persist before hiding: this is the moment the geometry is final, and it
            // may be the last chance we get if the process is terminated later without
            // a clean exit.
            _window.SaveState();
            _window.Hide();
        }
        else
        {
            ShowWindowFromTray();
        }

        UpdateTrayState();
    }

    /// <summary>Open the Windows page that carries the recent-items toggle.</summary>
    private void OpenWindowsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = EnvironmentProbe.GetSettingsUri(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法打开设置：" + ex.Message,
                "RecentDock",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Show the appearance settings dialog.
    ///
    /// Only one instance is kept: reusing the window preserves focus and avoids
    /// stacking duplicate dialogs from repeated tray clicks.
    /// </summary>
    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings);

        // Persist on every change so a crash or a kill cannot lose the adjustment, and
        // keep the in-memory copy in step for the next time the dialog opens.
        _settingsWindow.SettingsChanged += (_, updated) =>
        {
            _settings = updated;
            _window?.ApplyAppearanceSettings(updated);
            ConfigStore.Save(_settings);
        };

        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;

            // Geometry is unaffected by appearance edits, but the panel needs a nudge
            // so row styles are re-read from the newly written resources.
            _window?.RefreshRowStyles();
        };

        // Owned by the panel when it is visible, so the dialog cannot end up behind it.
        if (_window is { IsVisible: true })
        {
            _settingsWindow.Owner = _window;
        }

        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void UpdateTrayState()
    {
        _tray?.UpdateState(_window?.IsVisible == true, _window?.Items.Count ?? 0);
    }

    /// <summary>
    /// Construct every window once, off-screen, and report any failure.
    ///
    /// Why this exists: a XAML error is a RUNTIME failure, not a compile error. The
    /// "BasedOn cannot take a DynamicResource" mistake built cleanly, shipped, and
    /// only surfaced as an unhandled-exception dialog when the panel tried to render.
    /// A build that succeeds says nothing about whether the XAML parses.
    ///
    /// Windows are measured rather than shown, so this needs no interactive session
    /// and leaves nothing on screen.
    ///
    /// Usage: RecentDock.exe --uitest
    /// </summary>
    private void RunUiSmokeTest()
    {
        int exitCode = 0;
        var log = new System.Text.StringBuilder();

        void Log(string text)
        {
            Console.WriteLine(text);
            log.AppendLine(text);
        }

        Log("RecentDock UI smoke test");

        UiSettings settings = ConfigStore.Load().Sanitized(
            (int)SystemParameters.VirtualScreenLeft,
            (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth,
            (int)SystemParameters.VirtualScreenHeight);

        try
        {
            ThemeManager.Apply(settings);
            Log($"  theme applied      : opacity={settings.PanelOpacity:F2} font={settings.FontSize:F0} icon={settings.IconSize:F0}");

            // Report whether the palette brushes are actually mutable. WPF freezes
            // Freezables declared in a ResourceDictionary, and a frozen brush cannot
            // be recoloured - which is exactly how the opacity slider ended up doing
            // nothing while the font and icon sliders worked.
            Log($"  brush mutability   : {ThemeManager.DescribeBrushState()}");

            Log($"  surface alphas     : {ThemeManager.DescribeSurfaceAlphas()}");
        }
        catch (Exception ex)
        {
            Log($"  FAIL applying theme: {ex.GetType().Name}: {ex.Message}");
            exitCode = 1;
        }

        try
        {
            var panel = new MainWindow();

            // Force the template and the item containers to materialise. Constructing
            // the window alone does not parse the row DataTemplate - that happens on
            // first render - so a layout pass is required to catch template errors.
            Layout(panel, 720, 560);

            // Exercise the path the settings dialog triggers, which rewrites the style
            // resources and then rebuilds the containers.
            panel.RefreshRowStyles();
            Layout(panel, 720, 560);

            Log("  MainWindow         : constructed, measured, row styles refreshed");

            // Backdrop diagnosis. Without this, "the panel looks black" gives no clue
            // whether the window is transparent, whether DWM supports the material, or
            // whether the attribute call failed - all three look identical on screen.
            try
            {
                panel.WindowStartupLocation = WindowStartupLocation.Manual;

                // Show it ON SCREEN, near the top-left.
                //
                // An off-screen window (Left = -32000) is not composited, so the DWM
                // material never contributes to what gets captured. That produced a
                // completely misleading result: four different material configurations
                // all sampled as identical pixels, because none of them were actually
                // being rendered. Sampling only means something while the window is
                // visible.
                panel.Left = 40;
                panel.Top = 40;
                panel.Show();
                panel.Activate();
                panel.UpdateLayout();

                // The window itself must remain opaque so foreground content stays
                // crisp. Only the XAML surface brushes change alpha.
                panel.ApplyAppearanceSettings(settings with { PanelOpacity = 0.3 });
                Log($"  window opacity     : {panel.Opacity:F2}");
                Log($"  surface at 0.30    : {ThemeManager.DescribeSurfaceAlphas()}");
                if (Math.Abs(panel.Opacity - 1.0) > 0.001)
                {
                    Log("  FAIL foreground would be faded with the window");
                    exitCode = 1;
                }

                panel.ApplyAppearanceSettings(settings with { PanelOpacity = 1.0 });
                Log($"  surface at 1.00    : {ThemeManager.DescribeSurfaceAlphas()}");

                panel.ApplyAppearanceSettings(settings with { AlwaysOnTop = false });
                Log($"  topmost off        : {panel.Topmost}");
                if (panel.Topmost)
                {
                    Log("  FAIL panel remained topmost after disabling it");
                    exitCode = 1;
                }

                panel.ApplyAppearanceSettings(settings with { AlwaysOnTop = true });
                Log($"  topmost on         : {panel.Topmost}");
                if (!panel.Topmost)
                {
                    Log("  FAIL panel did not become topmost");
                    exitCode = 1;
                }

                panel.ApplyAppearanceSettings(settings);

                Log($"  backdrop diagnosis : {AcrylicBackdrop.Diagnose(panel)}");
                Log($"  window styles      : {AcrylicBackdrop.DescribeWindowStyles(panel)}");

                AcrylicBackdrop.RemoveFrameStyles(panel);
                panel.UpdateLayout();
                Log($"  styles cleared     : {AcrylicBackdrop.DescribeWindowStyles(panel)}");

                // Pixel sampling was REMOVED here on purpose.
                //
                // Two capture paths were tried - PrintWindow(PW_RENDERFULLCONTENT) and
                // BitBlt from the screen DC - and study both proved unreliable here:
                // they reported identical values for eight different material
                // configurations, and a sanity check that repainted the panel solid blue
                // and then solid white also reported no change. A probe that cannot see a
                // deliberate colour change is not evidence of anything, and leaving it in
                // invites conclusions drawn from noise. It also sent this investigation
                // down two wrong paths.
                //
                // What remains is only what can be trusted: whether each DWM attribute
                // call succeeded, and the window style bits.

                // Try each material so the right one can be pinned down empirically
                // rather than guessed at.
                foreach ((string name, int type) in new[]
                {
                    ("mica   ", AcrylicBackdrop.BackdropMica),
                    ("acrylic", AcrylicBackdrop.BackdropAcrylic),
                })
                {
                    bool ok = AcrylicBackdrop.TrySetBackdrop(
                        new System.Windows.Interop.WindowInteropHelper(panel).Handle,
                        type);
                    Log($"  backdrop {name}     : set={ok}");
                }

                panel.Hide();
            }
            catch (Exception ex)
            {
                Log($"  note: could not realise the panel for backdrop checks: {ex.GetType().Name}");
            }

            panel.Close();
        }
        catch (Exception ex)
        {
            Log($"  FAIL MainWindow    : {ex.GetType().Name}: {ex.Message}");
            Log(ex.ToString());
            exitCode = 1;
        }

        try
        {
            var dialog = new SettingsWindow(settings);

            // Show it far off-screen rather than only measuring it.
            //
            // Measure/Arrange is not enough for a Window: without an HwndSource the
            // visual tree is never realised, so every control reports a size of 0x0 and
            // a hit test is meaningless. An earlier version of this check did exactly
            // that and produced three bogus "BLOCKED zero size" results - a false
            // failure caused by the test, not by the UI.
            //
            // Off-screen at -32000 keeps it invisible while still giving it a real
            // window and a real layout pass.
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -32000;
            dialog.Top = -32000;
            dialog.ShowInTaskbar = false;

            try
            {
                dialog.Show();
                dialog.UpdateLayout();
            }
            catch (Exception ex)
            {
                Log($"  note: could not realise the dialog ({ex.GetType().Name}); geometry checks skipped");
            }

            // Structural check, which is what actually catches the bug class.
            //
            // The opacity slider was undraggable because its hint label sat in
            // Grid.Row="2" while the Grid declared only two RowDefinitions. WPF does
            // not reject an out-of-range index - it silently places the element in the
            // last defined row - so the mistake is invisible at compile time and the
            // elements simply overlap.
            //
            // A hit test at the control's centre is NOT sufficient to detect this: an
            // overlapping label usually sits at the bottom of the row and misses the
            // centre, so the probe reports "fine" on broken markup. Verified by
            // reintroducing the bug, which the hit test failed to catch. The row index
            // check below does catch it.
            List<string> layoutErrors = FindOutOfRangeGridRows(dialog);
            if (layoutErrors.Count == 0)
            {
                Log("  grid rows          : all children inside their Grid's declared rows");
            }
            else
            {
                foreach (string error in layoutErrors)
                {
                    Log($"  FAIL grid row      : {error}");
                }

                exitCode = 1;
            }

            // Keep the hit test as a secondary signal: it catches a different class of
            // problem, namely a control that is covered by a sibling.
            foreach ((string label, System.Windows.Controls.Slider slider) in new[]
            {
                ("opacity", dialog.OpacitySliderControl),
                ("font   ", dialog.FontSliderControl),
                ("icon   ", dialog.IconSliderControl),
            })
            {
                bool usable = CheckSliderUsable(slider, out string detail);
                Log($"  slider {label}       : {(usable ? "draggable" : "BLOCKED")}  {detail}");

                if (!usable)
                {
                    exitCode = 1;
                }
            }

            Log("  SettingsWindow     : constructed, realised, layout checked");
            dialog.Close();
        }
        catch (Exception ex)
        {
            Log($"  FAIL SettingsWindow: {ex.GetType().Name}: {ex.Message}");
            Log(ex.ToString());
            exitCode = 1;
        }

        try
        {
            // The tray icon is the other runtime-only component; creating it proves the
            // icon can be extracted from our own executable.
            using var tray = new TrayIcon();
            tray.Initialize();
            Log("  TrayIcon           : initialised");
        }
        catch (Exception ex)
        {
            Log($"  FAIL TrayIcon      : {ex.GetType().Name}: {ex.Message}");
            exitCode = 1;
        }

        Log("");
        Log(exitCode == 0 ? "OK" : "FAILED");

        string logPath = Path.Combine(Path.GetTempPath(), "RecentDock-uitest.txt");
        try
        {
            File.WriteAllText(logPath, log.ToString(), new System.Text.UTF8Encoding(true));
            Console.WriteLine($"log: {logPath}");
        }
        catch (Exception)
        {
            // Diagnostics only.
        }

        Environment.Exit(exitCode);
    }

    /// <summary>
    /// Run a layout pass without showing the window, so template errors surface.
    /// </summary>
    private static void Layout(Window window, double width, double height)
    {
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
    }

    /// <summary>
    /// Find Grid children placed in a row the Grid does not declare.
    ///
    /// WPF does not treat an out-of-range Grid.Row as an error: the element is placed
    /// in the last defined row instead, so it silently overlaps whatever is already
    /// there. That is how the opacity slider ended up covered by its own hint label.
    /// Walking the realised tree and comparing the attached index against the Grid's
    /// row count is the only reliable way to catch it.
    /// </summary>
    private static List<string> FindOutOfRangeGridRows(DependencyObject root)
    {
        var problems = new List<string>();

        if (root is not System.Windows.Media.Visual and not System.Windows.Media.Media3D.Visual3D)
        {
            return problems;
        }

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (root is System.Windows.Controls.Grid grid)
            {
                int declared = grid.RowDefinitions.Count;
                if (declared > 0)
                {
                    // Grid.GetRow takes a UIElement; every visual child of a Grid is one.
                    if (child is UIElement element)
                    {
                        int row = System.Windows.Controls.Grid.GetRow(element);
                        if (row >= declared)
                        {
                            problems.Add(
                                $"{child.GetType().Name} uses Grid.Row={row} but its Grid declares {declared} row(s)");
                        }
                    }
                }
            }

            problems.AddRange(FindOutOfRangeGridRows(child));
        }

        return problems;
    }

    /// <summary>
    /// Capture the window and read a few pixels, to establish what is actually on
    /// screen rather than what the application believes.
    ///
    /// Two capture paths, because they answer different questions:
    ///
    ///   PrintWindow        captures the application's own drawing. It does NOT include
    ///                      the DWM material, which the compositor draws - verified by
    ///                      the fact that four different material configurations all
    ///                      sampled identically. Useful only for finding opaque layers
    ///                      laid down by the app itself.
    ///   screen-to-client   reads straight from the screen DC at the window's client
    ///                      origin, so it includes the material and whatever is behind
    ///                      the window. This is the one that can prove the glass works.
    /// </summary>
    private static string SampleWindowPixels(Window window)
    {
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out RECT rect))
        {
            return "no window rect";
        }

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return $"empty rect {width}x{height}";
        }

        IntPtr hdcScreen = CreateDC("DISPLAY", null, null, IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
        {
            return "no screen dc";
        }

        IntPtr hdcMemory = CreateCompatibleDC(hdcScreen);
        IntPtr bitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr previous = SelectObject(hdcMemory, bitmap);

        try
        {
            // PW_RENDERFULLCONTENT (2) is required; without it a composed window
            // captures as blank, which would look like a false failure.
            string ownDrawing;
            if (PrintWindow(handle, hdcMemory, 2))
            {
                ownDrawing = $"appDraw[corner={ToColor(GetPixel(hdcMemory, 1, 1)):X6}"
                    + $" content={ToColor(GetPixel(hdcMemory, width / 2, height / 2)):X6}]";
            }
            else
            {
                ownDrawing = "appDraw[failed]";
            }

            // Now read the real screen. A point well inside the panel avoids the
            // border and the rounded corner.
            var clientOrigin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(handle, ref clientOrigin))
            {
                return ownDrawing + " screenPaint[ClientToScreen failed]";
            }

            int insideX = clientOrigin.X + (width / 2);
            int insideY = clientOrigin.Y + (height / 2);
            int outsideX = clientOrigin.X - 12;
            int outsideY = insideY;

            // BLT from the screen DC into the memory bitmap, then sample.
            BitBlt(hdcMemory, 0, 0, width, height, hdcScreen, clientOrigin.X, clientOrigin.Y, SRCCOPY);
            int paintedContent = ToColor(GetPixel(hdcMemory, width / 2, height / 2));

            BitBlt(hdcMemory, 0, 0, width, height, hdcScreen, outsideX, clientOrigin.Y, SRCCOPY);
            int desktopBeside = ToColor(GetPixel(hdcMemory, 12, height / 2));

            return ownDrawing
                + $" screen[panel={paintedContent:X6} desktopBeside={desktopBeside:X6}"
                + $" same={paintedContent == desktopBeside}]";
        }
        finally
        {
            SelectObject(hdcMemory, previous);
            DeleteObject(bitmap);
            DeleteDC(hdcMemory);
            DeleteDC(hdcScreen);
        }
    }

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    private static int ToColor(uint bgr) => (int)(bgr & 0x00FFFFFF);

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

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr CreateDC(string? driver, string? device, string? output, IntPtr initData);

    [DllImport("gdi32.dll", SetLastError = false)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = false)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll", SetLastError = false)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = false)]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    /// <summary>
    /// Confirm a slider can be grabbed: it must have a usable range, and a hit test at
    /// its own centre must land on it (or inside it) rather than on something layered
    /// over it.
    /// </summary>
    private static bool CheckSliderUsable(System.Windows.Controls.Slider slider, out string detail)
    {
        if (slider.ActualWidth <= 0 || slider.ActualHeight <= 0)
        {
            detail = $"zero size ({slider.ActualWidth:F0}x{slider.ActualHeight:F0})";
            return false;
        }

        if (slider.Maximum <= slider.Minimum)
        {
            detail = "empty range";
            return false;
        }

        // Centre of the track, in the slider's own coordinates.
        var probe = new Point(slider.ActualWidth / 2, slider.ActualHeight / 2);
        IInputElement? hit = slider.InputHitTest(probe);

        if (hit is null)
        {
            detail = "hit test found nothing";
            return false;
        }

        // Walk up from whatever was hit: being inside the slider's visual tree is fine,
        // being outside it means something is covering the control.
        DependencyObject? node = hit as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, slider))
            {
                detail = $"{slider.ActualWidth:F0}x{slider.ActualHeight:F0}, range {slider.Minimum:F2}-{slider.Maximum:F2}";
                return true;
            }

            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : System.Windows.LogicalTreeHelper.GetParent(node);
        }

        detail = $"covered by {hit.GetType().Name}";
        return false;
    }

    /// <summary>
    /// Write an unhandled exception to a file and return its path.
    ///
    /// The dialog alone is not enough: its text cannot be read back, so a crash
    /// report has to be transcribed by hand. The file survives the dialog and works
    /// even when the dialog cannot be shown at all.
    /// </summary>
    private static string WriteCrashLog(Exception exception)
    {
        string path = Path.Combine(Path.GetTempPath(), "RecentDock-crash.txt");

        try
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"time      : {DateTimeOffset.Now:O}");
            report.AppendLine($"version   : {typeof(App).Assembly.GetName().Version}");

            try
            {
                report.AppendLine($"os        : {Environment.OSVersion.Version}");
                report.AppendLine($"theme     : {ConfigStore.Load().UseDarkTheme switch { true => "dark", false => "light" }}");
            }
            catch (Exception)
            {
                // Diagnostics only.
            }

            report.AppendLine();
            report.AppendLine(exception.ToString());

            File.WriteAllText(path, report.ToString(), new System.Text.UTF8Encoding(true));
            return path;
        }
        catch (Exception)
        {
            return "(无法写入日志)";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Final safety net. SaveState is idempotent, so calling it here as well as on
        // hide costs one small write and guarantees the state is on disk no matter
        // which exit path was taken.
        try
        {
            _window?.SaveState();
        }
        catch (Exception)
        {
            // Never let persistence trouble block shutdown.
        }

        _tray?.Dispose();
        _tray = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Headless verification: run one scan against the live machine, print what it
    /// found, and exit. Exists so the pipeline can be checked without a visible
    /// window, and so a failure produces a readable message plus a non-zero exit
    /// code instead of a silent crash on startup.
    /// </summary>
    private void RunSelfTest()
    {
        int exitCode = 0;

        // The console output of a WinExe is unreliable to capture: it has no stdout of
        // its own, and the AttachConsole fallback writes to the console device rather
        // than to a caller's pipe or file. A mirrored log file makes the diagnostics
        // readable regardless of how the process was started.
        var log = new System.Text.StringBuilder();
        string logPath = Path.Combine(Path.GetTempPath(), "RecentDock-selftest.txt");

        // Declared outside the try so the catch block can use it too.
        void Write(string text)
        {
            Console.WriteLine(text);
            log.AppendLine(text);
        }

        try
        {
            AttachParentConsole();

            var scanner = new RecentScanner();
            RecentScanResult result = scanner.ScanAsync().GetAwaiter().GetResult();
            TrackingDiagnostics diag = EnvironmentProbe.GetDiagnostics();

            Write("RecentDock self-test");
            Write($"  version            : {typeof(App).Assembly.GetName().Version}");
            Write($"  OS / backdrop      : {AcrylicBackdrop.DescribeSupport()}");

            // Theme is reported because it has two independent halves - the XAML
            // palette and the DWM material variant - and a mismatch between them is
            // exactly what made a "light" build render black.
            RecentDock.Core.Storage.UiSettings ui = RecentDock.Core.Storage.ConfigStore.Load();
            Write($"  theme              : {(ui.UseDarkTheme ? "dark" : "light (white glass)")}, acrylic {(ui.UseAcrylicBackdrop ? "on" : "off")}");
            Write($"  state directory    : {RecentDock.Core.Storage.AppPaths.StateDirectory}");
            Write($"  settings link      : {EnvironmentProbe.GetSettingsUri()}");
            Write($"  autostart entry    : {(AutoStart.IsEnabled() ? "present" : "absent")}");
            Write($"  tracking state     : {result.Tracking}");
            Write($"  panel state        : {result.Panel}");
            Write($"  Start_TrackDocs    : {Format(diag.StartTrackDocs)}");
            Write($"  Start_TrackProgs   : {Format(diag.StartTrackProgs)}");
            Write($"  NoRecentDocsHistory: {Format(diag.NoRecentDocsHistory)}");
            Write($"  .lnk files found   : {result.LinkFileCount}");
            Write($"  unresolved .lnk    : {result.UnresolvedLinkCount}");
            Write($"  registry records   : {result.RegistryRecordCount}");
            Write($"  items to display   : {result.Items.Count}");
            Write("");

            int index = 0;
            foreach (RecentItem item in result.Items)
            {
                index++;
                string kind = item.IsDirectory ? "DIR " : "FILE";
                string rank = item.MruRank?.ToString() ?? "-";
                Write($"  {index,3}. [rank {rank,-3}] [{kind}] {item.DisplayName}");
                Write($"       {item.TargetPath}   ({item.Validity})");
            }

            if (result.Items.Count == 0)
            {
                Write("  (nothing to display)");
            }

            // Show the live panel and sample its pixels. This is the one line that
            // answers "is the glass actually transparent", as opposed to reporting
            // settings that all look correct while the user sees something else.
            try
            {
                var probe = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000,
                    Top = -32000,
                    ShowInTaskbar = false,
                };

                probe.Show();
                probe.UpdateLayout();
                probe.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Threading.Thread.Sleep(500);

                Write($"\n  live pixels        : {SampleWindowPixels(probe)}");
                Write("    (corner is the rounded edge outside the panel; content is the list area)");
                Write("    corner near 000000 means the material is NOT compositing;");

                probe.Close();
            }
            catch (Exception ex)
            {
                Write($"  live pixels        : unavailable ({ex.GetType().Name})");
            }

            // -------------------------------------------------- snapshot overlay
            // Exercise the startup interleaving without a window: paint the cached
            // snapshot first, then apply the real scan through the same row-merging
            // path the panel uses, and report the validity each row ENDS UP with.
            //
            // This exists because "snapshot rows keep claiming Unreachable" was a real
            // bug: the merge reused row objects for their cached icons but never
            // refreshed validity, so after a restart every entry displayed as
            // unreachable. Comparing before/after here catches any regression.
            Write("");
            Write("--- snapshot overlay check (mirrors startup order) ---");

            RecentDock.Core.Storage.ScanSnapshot? cached = RecentDock.Core.Storage.SnapshotStore.Load();
            if (cached is null)
            {
                Write("  no snapshot on disk; run the app once to create one.");
            }
            else
            {
                IReadOnlyList<RecentItem> cachedItems = RecentDock.Core.Storage.SnapshotStore.ToItems(cached);

                int beforeUnreachable = cachedItems.Count(i => i.Validity == ItemValidity.Unreachable);
                Write($"  snapshot rows              : {cachedItems.Count} (all Unreachable: {beforeUnreachable})");

                // Model what ApplyItems actually does, which is a REPLACEMENT, not a
                // union: rows present only in the snapshot are dropped, and rows present
                // in both take their validity from the fresh scan.
                //
                // An earlier version of this check built a union, and therefore reported
                // a phantom failure for snapshot-only rows that the panel would simply
                // have removed. The diagnostic was wrong, not the code.
                var displayed = new Dictionary<string, ItemValidity>(StringComparer.OrdinalIgnoreCase);
                foreach (RecentItem fresh in result.Items)
                {
                    displayed[fresh.TargetPath] = fresh.Validity;
                }

                int dropped = cachedItems.Count(c => !displayed.ContainsKey(c.TargetPath));
                int stillUnreachable = displayed.Values.Count(v => v == ItemValidity.Unreachable);
                int expectedUnreachable = result.Items.Count(i => i.Validity == ItemValidity.Unreachable);

                Write($"  after real scan            : {displayed.Count} row(s) shown, {dropped} snapshot-only row(s) dropped");

                // Only a row the FRESH SCAN calls unreachable may display as such. Any
                // excess means the snapshot's placeholder survived the merge, which is
                // the bug this check exists to catch.
                if (stillUnreachable != expectedUnreachable)
                {
                    Write($"  FAIL: {stillUnreachable - expectedUnreachable} row(s) kept the snapshot placeholder validity.");
                    exitCode = 1;
                }
                else
                {
                    Write($"  OK: all validity came from the live scan ({expectedUnreachable} genuinely unreachable).");
                }
            }

            Write("");
            Write(exitCode == 0 ? "OK" : "FAILED");
        }
        catch (Exception ex)
        {
            Write("SELF-TEST FAILED");
            Write(ex.ToString());
            exitCode = 1;
        }

        // Mirror to a file. The console output of a WinExe is unreliable to capture,
        // so the log is the dependable way to read the result.
        try
        {
            // UTF-8 WITH a BOM: file names here are Chinese, and a BOM is what makes
            // Get-Content and other readers detect the encoding instead of falling back
            // to the ANSI code page and producing mojibake.
            File.WriteAllText(logPath, log.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"log: {logPath}");
        }
        catch (Exception)
        {
            // Diagnostics only.
        }

        Environment.Exit(exitCode);
    }

    /// <summary>Render a nullable registry DWORD for diagnostics.</summary>
    private static string Format(int? value) => value?.ToString() ?? "<absent>";

    private void RunWatchTest()
    {
        int exitCode = 0;
        const int BurstCount = 6;

        // A DispatcherFrame, not Dispatcher.Run.
        //
        // Dispatcher.InvokeShutdown is irreversible: after phase 1 shut the
        // dispatcher down, phase 2's Dispatcher.Run threw and the failure was
        // swallowed, which looked exactly like a broken debounce. A frame can be
        // exited and re-entered freely, so both phases use the same pattern.
        System.Windows.Threading.DispatcherFrame? frame = null;

        try
        {
            AttachParentConsole();
            Console.WriteLine("RecentDock watcher test");

            string recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            Console.WriteLine($"  watching      : {recentDirectory}");

            int refreshRequests = 0;
            int rawEvents = 0;
            bool running = true;

            void PumpUntilEvent()
            {
                frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }

            void SignalEvent()
            {
                running = false;
                if (frame is not null)
                {
                    frame.Continue = false;
                    frame = null;
                }
            }

            using var watcher = new RecentWatcher();
            watcher.RefreshRequested += (_, _) =>
            {
                if (!running)
                {
                    return;
                }

                refreshRequests++;
                Console.WriteLine($"  refresh requested (#{refreshRequests})");
                SignalEvent();
            };

            watcher.RawEventObserved += (_, description) =>
            {
                rawEvents++;
                Console.WriteLine($"    raw event #{rawEvents}: {description}");
            };

            watcher.WatcherRebuilt += (_, reason) =>
                Console.WriteLine($"  WATCHER REBUILT (reason: {reason})");

            watcher.Degraded += (_, message) => Console.WriteLine($"  DEGRADED: {message}");
            watcher.Start();

            Thread.Sleep(2000);

            string? template = Directory
                .GetFiles(recentDirectory, "*.lnk", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (template is null)
            {
                Console.WriteLine("  SKIP: no existing .lnk to use as a probe template.");
                Environment.Exit(2);
                return;
            }

            Console.WriteLine($"  probe template: {Path.GetFileName(template)}");

            Console.Write("  phase 1: single change ... ");
            try
            {
                File.Copy(template, Path.Combine(recentDirectory, "RecentDock-watchtest.lnk"), overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Console.WriteLine($"  FAIL: cannot create the probe file: {ex.Message}");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine("written");

            long phase1Start = Environment.TickCount64;
            PumpUntilEvent();
            long phase1Elapsed = Environment.TickCount64 - phase1Start;
            Console.WriteLine($"  phase 1: {refreshRequests} refresh(es) after {phase1Elapsed} ms");

            if (refreshRequests == 0)
            {
                Console.WriteLine("  FAIL: the watcher never fired for a new file.");
                exitCode = 1;
            }
            else
            {
                running = true;
                int before = refreshRequests;
                int rawBefore = rawEvents;

                Console.Write($"  phase 2: {BurstCount} rapid changes ... ");
                for (int i = 0; i < BurstCount; i++)
                {
                    try
                    {
                        File.Copy(
                            template,
                            Path.Combine(recentDirectory, $"RecentDock-watchtest-{i}.lnk"),
                            overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"  burst write {i} failed: {ex.Message}");
                        break;
                    }

                    Thread.Sleep(40);
                }

                Console.WriteLine("written");
                PumpUntilEvent();

                int extra = refreshRequests - before;
                int rawExtra = rawEvents - rawBefore;
                Console.WriteLine($"  phase 2: {rawExtra} raw event(s), {extra} refresh(es) for {BurstCount} changes");

                if (extra != 1)
                {
                    Console.WriteLine(rawExtra == 0
                        ? "  FAIL: no filesystem events arrived at all for the burst."
                        : $"  FAIL: debounce did not coalesce; expected 1, got {extra}.");
                    exitCode = 1;
                }
                else
                {
                    Console.WriteLine("  OK: burst coalesced into a single refresh.");
                }
            }

            for (int i = 0; i < BurstCount; i++)
            {
                TryDelete(Path.Combine(recentDirectory, $"RecentDock-watchtest-{i}.lnk"));
            }

            TryDelete(Path.Combine(recentDirectory, "RecentDock-watchtest.lnk"));

            Console.WriteLine(exitCode == 0 ? "OK" : "FAILED");
        }
        catch (Exception ex)
        {
            Console.WriteLine("WATCH TEST FAILED");
            Console.WriteLine(ex);
            exitCode = 1;
        }

        Environment.Exit(exitCode);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort only.
        }
    }

    /// <summary>
    /// Regression test for the "remove from list" COM failure.
    ///
    /// The bug: RemoveByTargetPath ran after an await, so it resumed on a
    /// thread-pool thread with no COM apartment, and LinkResolver threw
    /// "requires a COM-initialized STA thread". This reproduces that exact
    /// condition - a thread-pool thread - and asserts the call no longer throws.
    ///
    /// It is deliberately non-destructive: the target path passed in matches no
    /// record, so every link is resolved (exercising the COM path fully) but nothing
    /// is deleted.
    ///
    /// Usage: RecentDock.exe --removecomtest
    /// </summary>
    private void RunRemoveComTest()
    {
        int exitCode = 0;

        try
        {
            AttachParentConsole();
            Console.WriteLine("RecentDock remove/COM regression test");

            string sentinel = Path.Combine(
                Path.GetTempPath(),
                "recentdock-should-never-match-" + Guid.NewGuid().ToString("N"));

            bool onPoolThread = false;

            // Task.Run guarantees a thread-pool thread, which is exactly the
            // condition that used to fail.
            int removed = Task.Run(() =>
            {
                onPoolThread = true;
                return RecentEntryCleaner.RemoveByTargetPath(sentinel);
            }).GetAwaiter().GetResult();

            Console.WriteLine($"  ran on a thread-pool thread : {onPoolThread}");
            Console.WriteLine($"  records removed             : {removed}  (expected 0)");
            Console.WriteLine();

            if (removed != 0)
            {
                Console.WriteLine("  FAIL: a non-existent path removed records; the match logic is wrong.");
                exitCode = 1;
            }
            else
            {
                Console.WriteLine("  OK: COM was acquired on demand; no exception, nothing deleted.");
            }

            // ------------------------------------------------- second phase
            // Round trip the MATCHING logic without touching the user's real records:
            // copy an existing .lnk to a probe name, resolve the probe's target, then
            // remove by that target and confirm exactly the probe was deleted.
            Console.WriteLine();
            Console.WriteLine("  --- matching round trip ---");

            string recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

            // Clean up any probe left by an earlier interrupted run first.
            foreach (string stale in Directory.GetFiles(recentDirectory, "RecentDock-removetest*.lnk"))
            {
                TryDelete(stale);
            }

            string? template = Directory
                .GetFiles(recentDirectory, "*.lnk", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("RecentDock-", StringComparison.OrdinalIgnoreCase));

            if (template is null)
            {
                Console.WriteLine("  SKIP: no existing .lnk to base the probe on.");
            }
            else
            {
                string probe = Path.Combine(recentDirectory, "RecentDock-removetest.lnk");
                string templateName = Path.GetFileName(template);
                int before = Directory.GetFiles(recentDirectory, "*.lnk").Length;

                // Keep the template's bytes so the original record can be restored.
                // The probe shares the template's target, and RemoveByTargetPath clears
                // every record for a target (that is the intended contract), so the
                // original WILL be removed along with the probe. A test must not
                // silently destroy one of the user's records, so it is put back after.
                byte[] templateBytes = File.ReadAllBytes(template);

                File.Copy(template, probe, overwrite: true);

                int afterCopy = Directory.GetFiles(recentDirectory, "*.lnk").Length;
                Console.WriteLine($"  probe created               : {Path.GetFileName(probe)}  ({before} -> {afterCopy} records)");

                string? probeTarget = LinkResolver.TryGetTargetPath(probe);
                if (string.IsNullOrWhiteSpace(probeTarget))
                {
                    Console.WriteLine("  FAIL: the probe's target could not be resolved.");
                    TryDelete(probe);
                    exitCode = 1;
                }
                else
                {
                    Console.WriteLine($"  probe target                : {probeTarget}");

                    int matched = Task.Run(() => RecentEntryCleaner.RemoveByTargetPath(probeTarget))
                        .GetAwaiter().GetResult();

                    int afterRemove = Directory.GetFiles(recentDirectory, "*.lnk").Length;
                    Console.WriteLine($"  records removed by match    : {matched}  ({afterCopy} -> {afterRemove} records)");

                    bool probeGone = !File.Exists(probe);
                    Console.WriteLine($"  probe deleted               : {probeGone}");

                    // Restore the original record if the match took it too.
                    string templatePath = Path.Combine(recentDirectory, templateName);
                    bool restored = false;
                    if (!File.Exists(templatePath))
                    {
                        try
                        {
                            File.WriteAllBytes(templatePath, templateBytes);
                            restored = true;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  WARNING: could not restore {templateName}: {ex.Message}");
                        }
                    }

                    int afterRestore = Directory.GetFiles(recentDirectory, "*.lnk").Length;
                    Console.WriteLine($"  original restored           : {restored}  ({afterRestore} records, started at {before})");

                    if (!probeGone)
                    {
                        Console.WriteLine("  FAIL: the probe survived, so matching did not work.");
                        TryDelete(probe);
                        exitCode = 1;
                    }
                    else if (afterRestore != before)
                    {
                        Console.WriteLine("  FAIL: record count did not return to its starting value.");
                        exitCode = 1;
                    }
                    else
                    {
                        Console.WriteLine("  OK: matching removed the record(s); original record restored.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL: the call threw, which is the bug this test guards against.");
            Console.WriteLine(ex);
            exitCode = 1;
        }

        Environment.Exit(exitCode);
    }

    /// <summary>
    /// A WinExe has no console of its own, so Console.WriteLine from the diagnostic
    /// switches would go nowhere. Attaching to the parent process's console makes
    /// the output appear in the calling PowerShell window.
    /// </summary>
    private static void AttachParentConsole()
    {
        const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                return;
            }

            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
        }
        catch (Exception)
        {
            // Diagnostics only; never let this break the self-test.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}
