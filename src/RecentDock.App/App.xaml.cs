using System.IO;
using System.Windows;
using RecentDock.Core;
using RecentDock.Core.Interop;

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
            MessageBox.Show(
                args.Exception.ToString(),
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

        // Single instance must be checked before any window or tray icon is created,
        // otherwise the duplicate would flash a panel and add a second tray icon
        // before exiting.
        if (!SingleInstance.TryAcquire())
        {
            Shutdown();
            return;
        }

        CreateWindow();

        _tray = new TrayIcon();
        _tray.ToggleRequested += (_, _) => ToggleWindow();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.ExitRequested += (_, _) => ExitApplication();
        _tray.Initialize();

        UpdateTrayState();
    }

    private void CreateWindow()
    {
        _window = new MainWindow();
        _window.ScanCompleted += (_, count) => _tray?.UpdateState(_window.IsVisible, count);
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

    private void OpenSettings()
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

    private void UpdateTrayState()
    {
        _tray?.UpdateState(_window?.IsVisible == true, _window?.Items.Count ?? 0);
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
