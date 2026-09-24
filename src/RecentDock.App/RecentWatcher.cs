using System.IO;
using System.Windows.Threading;
using RecentDock.Core;

namespace RecentDock.App;

/// <summary>
/// Watches for changes that should trigger a panel refresh.
///
/// Three separate triggers, because no single one covers the ground:
///
///   1. FileSystemWatcher on the Recent folder, for new/updated/deleted .lnk files.
///   2. A buffer-overflow fallback: FileSystemWatcher silently stops delivering
///      events after its buffer overflows, so the Error event must be handled or
///      the panel freezes on stale data forever with no visible symptom.
///   3. A slow poll, because the RecentDocs registry key cannot be watched at all.
///      This is also what keeps tracking-state changes (the user flipping the
///      Windows setting) reflected without a restart.
///
/// All events are collapsed through one debounce timer: a batch operation such as
/// unzipping or a multi-file save produces dozens of raw events, and each one would
/// otherwise start a full rescan.
/// </summary>
public sealed class RecentWatcher : IDisposable
{
    /// <summary>
    /// 500 ms, as specified in DESIGN.md M5. Long enough to coalesce a burst of
    /// writes from one operation, short enough to feel immediate.
    /// </summary>
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Poll period. The registry cannot be watched, so this is the only way to
    /// notice RecentDocs changes and tracking-state flips. Kept slow because each
    /// tick costs a full scan.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 64 KB, the documented maximum. The 8 KB default overflows during bulk
    /// operations. It comes from non-paged pool, so it is not raised further.
    /// </summary>
    private const int WatcherBufferBytes = 64 * 1024;

    private readonly DispatcherTimer _debounceTimer;
    private readonly DispatcherTimer _pollTimer;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised on the UI thread when the panel should rescan.</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>Raised when polling had to take over; surfaced in the status bar.</summary>
    public event EventHandler<string>? Degraded;

    /// <summary>
    /// Raised for every raw filesystem event, before debouncing. Diagnostics only:
    /// it exists so a test can tell "no events arrived" apart from "the debounce
    /// swallowed them", which are very different failures.
    /// </summary>
    public event EventHandler<string>? RawEventObserved;

    /// <summary>Raised when the watcher had to be rebuilt. Diagnostics only.</summary>
    public event EventHandler<string>? WatcherRebuilt;

    public RecentWatcher()
    {
        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = DebounceInterval,
        };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        };

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PollInterval,
        };
        _pollTimer.Tick += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Start()
    {
        StartWatcher();
        _pollTimer.Start();
    }

    private void StartWatcher()
    {
        try
        {
            string recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

            if (!Directory.Exists(recentDirectory))
            {
                // No Recent folder yet. Polling still covers the registry, and the
                // next successful scan will be triggered by the poll timer.
                Degraded?.Invoke(this, "未找到 Recent 目录，已切换为定时刷新");
                return;
            }

            _watcher = new FileSystemWatcher(recentDirectory)
            {
                // Only .lnk matters. Subdirectories hold jump lists
                // (AutomaticDestinations / CustomDestinations), which are irrelevant
                // here and would generate pointless churn.
                Filter = "*.lnk",
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName
                               | NotifyFilters.LastWrite
                               | NotifyFilters.CreationTime,
                InternalBufferSize = WatcherBufferBytes,
            };

            _watcher.Created += OnFileSystemEvent;
            _watcher.Changed += OnFileSystemEvent;
            _watcher.Deleted += OnFileSystemEvent;
            _watcher.Renamed += OnFileSystemEvent;

            // Mandatory. Without this, the first buffer overflow kills the watcher
            // silently and the panel never updates again while looking healthy.
            // Measured behaviour per the .NET docs: the component "loses track of
            // changes in the directory" and only reports a blanket notification.
            _watcher.Error += OnWatcherError;

            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // A watcher failure must not take the panel down; polling continues.
            Degraded?.Invoke(this, $"文件监视不可用（{ex.GetType().Name}），已切换为定时刷新");
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        RawEventObserved?.Invoke(this, $"{e.ChangeType} {e.Name}");
        ScheduleRefresh();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Almost always InternalBufferOverflowException. Rebuild the watcher from
        // scratch and do a full rescan: the events lost during the overflow mean
        // the current view cannot be trusted.
        Exception? error = e.GetException();

        _debounceTimer.Dispatcher.Invoke(() =>
        {
            WatcherRebuilt?.Invoke(this, error?.GetType().Name ?? "unknown");

            Degraded?.Invoke(
                this,
                error is InternalBufferOverflowException
                    ? "变更过多导致监视缓冲溢出，已重新同步"
                    : "文件监视出错，已重新同步");

            RestartWatcher();
            ScheduleRefresh();
        });
    }

    private void RestartWatcher()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Error -= OnWatcherError;
                _watcher.Created -= OnFileSystemEvent;
                _watcher.Changed -= OnFileSystemEvent;
                _watcher.Deleted -= OnFileSystemEvent;
                _watcher.Renamed -= OnFileSystemEvent;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch (Exception)
        {
            // Disposal failure is not worth surfacing; a new watcher is created next.
        }

        StartWatcher();
    }

    /// <summary>
    /// Collapse a burst of events into one refresh. Restarting the timer on every
    /// event is what makes this a debounce rather than a throttle.
    /// </summary>
    private void ScheduleRefresh()
    {
        if (_disposed)
        {
            return;
        }

        // Events arrive on a thread-pool thread; the timer must be touched on the
        // dispatcher that owns it.
        if (!_debounceTimer.Dispatcher.CheckAccess())
        {
            _debounceTimer.Dispatcher.BeginInvoke(ScheduleRefresh);
            return;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer.Stop();
        _pollTimer.Stop();

        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch (Exception)
        {
            // Nothing useful to do while shutting down.
        }
    }
}
