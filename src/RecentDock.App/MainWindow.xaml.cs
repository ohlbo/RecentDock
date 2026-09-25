using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RecentDock.Core;
using RecentDock.Core.Interop;
using RecentDock.Core.Storage;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace RecentDock.App;

/// <summary>
/// One row in the panel.
///
/// Only the relative-time text raises change notification, so the periodic tick
/// does not rebuild the list. Replacing ItemsSource on every refresh would reset
/// the scroll position and make the panel flicker.
/// </summary>
public sealed class RecentItemViewModel : INotifyPropertyChanged
{
    private readonly DateTimeOffset _lastAccess;

    public RecentItemViewModel(RecentItem item)
    {
        DisplayName = item.DisplayName;
        TargetPath = item.TargetPath;
        IsDirectory = item.IsDirectory;
        MruRank = item.MruRank;
        Validity = item.Validity;
        _lastAccess = item.LastAccessTime;

        string? parent = null;
        try
        {
            parent = System.IO.Path.GetDirectoryName(item.TargetPath);
        }
        catch (Exception)
        {
            // Malformed path: leave the folder line empty rather than failing.
        }

        FolderPath = parent ?? string.Empty;

        string extension = string.Empty;
        if (!item.IsDirectory)
        {
            try
            {
                extension = System.IO.Path.GetExtension(item.TargetPath);
            }
            catch (Exception)
            {
                extension = string.Empty;
            }
        }

        Icon = ShellIconProvider.GetIcon(extension, item.IsDirectory, item.Validity == ItemValidity.Valid);

        TypeLabel = item.IsDirectory ? "文件夹" : "文件";
    }

    public string DisplayName { get; }

    public string TargetPath { get; }

    public string FolderPath { get; }

    public bool IsDirectory { get; }

    public int? MruRank { get; }

    public ItemValidity Validity { get; private set; }

    public string TypeLabel { get; }

    public ImageSource? Icon { get; }

    /// <summary>
    /// Update validity in place.
    ///
    /// Rows restored from the snapshot are cached as Unreachable, because a snapshot
    /// cannot know whether a target still exists. When the real scan replaces them the
    /// row is reused for its icon and bindings, so this must be called or the row keeps
    /// claiming Unreachable forever - which is exactly the bug where every entry showed
    /// as unreachable after a restart.
    /// </summary>
    public void UpdateValidity(ItemValidity validity)
    {
        if (Validity == validity)
        {
            return;
        }

        Validity = validity;

        // IsMissing and ValidityLabel are computed from Validity, so they must be
        // re-announced or the row's text and strikethrough stay stale.
        OnPropertyChanged(nameof(Validity));
        OnPropertyChanged(nameof(IsMissing));
        OnPropertyChanged(nameof(ValidityLabel));
    }

    /// <summary>Access time behind <see cref="RelativeTime"/>, kept for snapshots.</summary>
    public DateTimeOffset LastAccessTime => _lastAccess;

    public string RelativeTime => FormatRelative(_lastAccess);

    /// <summary>True when the target is gone; the row is dimmed and struck through.</summary>
    public bool IsMissing => Validity != ItemValidity.Valid;

    public string ValidityLabel => Validity switch
    {
        ItemValidity.Valid => string.Empty,
        ItemValidity.Missing => "已失效",

        // Unreachable is overloaded: it covers both a genuinely offline network
        // target and a row painted from the snapshot before the real scan lands. The
        // wording stays neutral enough to be true in both cases, and these rows last
        // well under a second in practice.
        ItemValidity.Unreachable => "上次结果",
        ItemValidity.Unparsable => "无法解析",
        _ => string.Empty,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>Re-raise RelativeTime so the periodic tick refreshes the wording.</summary>
    public void RefreshRelativeTime() => OnPropertyChanged(nameof(RelativeTime));

    private static string FormatRelative(DateTimeOffset value)
    {
        TimeSpan delta = DateTimeOffset.Now - value;

        if (delta <= TimeSpan.Zero || delta.TotalSeconds < 60)
        {
            return "刚刚";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes} 分钟前";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours} 小时前";
        }

        if (delta.TotalDays < 30)
        {
            return $"{(int)delta.TotalDays} 天前";
        }

        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// The panel.
///
/// Deliberately a normal top-level window with WindowChrome rather than the
/// SetParent-to-Progman approach the original plan proposed: that approach dies
/// when Explorer restarts, inverts the meaning of Win+D, and misbehaves across
/// mixed-DPI monitors. See DESIGN.md M6.
/// </summary>
public partial class MainWindow : Window
{
    [Flags]
    private enum ResizeEdge
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    private const double ResizeGrip = 7;

    private readonly RecentScanner _scanner = new();
    private readonly DispatcherTimer _relativeTimeTimer;
    private RecentWatcher? _watcher;
    private bool _busy;
    private ResizeEdge _activeResizeEdge;
    private Point _resizeStartPointer;
    private Rect _resizeStartBounds;

    /// <summary>Settings as loaded, updated on close.</summary>
    private UiSettings _settings = new();

    /// <summary>Rows whose target no longer exists, used to drive the cleanup affordance.</summary>
    private int _missingCount;

    /// <summary>Raised after each successful scan, with the number of rows shown.</summary>
    public event EventHandler<int>? ScanCompleted;

    /// <summary>Raised when the user asks for the appearance settings dialog.</summary>
    public event EventHandler? AppearanceSettingsRequested;

    /// <summary>Raised when the user asks for the Windows recent-items setting.</summary>
    public event EventHandler? WindowsSettingsRequested;

    /// <summary>
    /// Force the list to rebuild its row containers so they re-read the theme styles.
    ///
    /// Needed because these styles are applied with StaticResource, which resolves
    /// once per container. ThemeManager rewrites the style objects, but an existing
    /// container keeps the styles it captured at creation - so a text-size change
    /// would only take effect on rows created afterwards.
    ///
    /// Rebuilding the ItemsSource is what actually forces regeneration. The row view
    /// models are reused, so icons stay cached and scroll position is preserved as
    /// closely as WPF allows.
    /// </summary>
    public void RefreshRowStyles()
    {
        IReadOnlyList<RecentItemViewModel> snapshot = Items.ToList();

        ItemList.ItemsSource = null;
        Items.Clear();

        foreach (RecentItemViewModel row in snapshot)
        {
            Items.Add(row);
        }

        ItemList.ItemsSource = Items;
    }

    /// <summary>
    /// Apply settings edited in the live appearance window.
    ///
    /// ThemeManager updates WPF resources, while this method reapplies the native
    /// backdrop. Both layers contribute to the final opacity, so omitting the second
    /// half makes the slider move without producing a visible change.
    /// </summary>
    public void ApplyAppearanceSettings(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _settings = settings;
        ThemeManager.Apply(settings);
        Topmost = settings.AlwaysOnTop;
        // The window itself stays fully opaque so text and icons remain crisp. Its
        // background is per-pixel transparent; ThemeManager changes only the white
        // panel surfaces drawn inside it.
        Opacity = 1.0;
        ApplyDropShadow(settings.ShowDropShadow);

        // AllowsTransparency uses a layered HWND. A native Accent/DWM backdrop behind
        // that HWND becomes an opaque white or black backing surface, defeating the
        // per-pixel transparency. The transparent XAML surface is the backdrop here.
        DegradedText.Text = string.Empty;

        RefreshRowStyles();
    }

    public MainWindow()
    {
        InitializeComponent();

        // Relative wording goes stale while the panel sits open overnight.
        _relativeTimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _relativeTimeTimer.Tick += (_, _) =>
        {
            foreach (RecentItemViewModel item in Items)
            {
                item.RefreshRelativeTime();
            }
        };

        Loaded += OnLoaded;
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonUp += OnWindowPreviewMouseLeftButtonUp;
        LostMouseCapture += OnWindowLostMouseCapture;
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_activeResizeEdge != ResizeEdge.None)
        {
            ResizeFromPointer(GetPointerInScreenDips(e));
            e.Handled = true;
            return;
        }

        Cursor = CursorFor(HitTestResizeEdge(e.GetPosition(this)));
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResizeEdge edge = HitTestResizeEdge(e.GetPosition(this));
        if (edge == ResizeEdge.None)
        {
            return;
        }

        _activeResizeEdge = edge;
        _resizeStartPointer = GetPointerInScreenDips(e);
        _resizeStartBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        CaptureMouse();
        e.Handled = true;
    }

    private void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_activeResizeEdge == ResizeEdge.None)
        {
            return;
        }

        EndManualResize();
        e.Handled = true;
    }

    private void OnWindowLostMouseCapture(object sender, MouseEventArgs e)
        => _activeResizeEdge = ResizeEdge.None;

    private ResizeEdge HitTestResizeEdge(Point point)
    {
        ResizeEdge edge = ResizeEdge.None;

        if (point.X <= ResizeGrip)
        {
            edge |= ResizeEdge.Left;
        }
        else if (point.X >= ActualWidth - ResizeGrip)
        {
            edge |= ResizeEdge.Right;
        }

        if (point.Y <= ResizeGrip)
        {
            edge |= ResizeEdge.Top;
        }
        else if (point.Y >= ActualHeight - ResizeGrip)
        {
            edge |= ResizeEdge.Bottom;
        }

        return edge;
    }

    private static Cursor CursorFor(ResizeEdge edge) => edge switch
    {
        ResizeEdge.Left or ResizeEdge.Right => Cursors.SizeWE,
        ResizeEdge.Top or ResizeEdge.Bottom => Cursors.SizeNS,
        ResizeEdge.Left | ResizeEdge.Top or ResizeEdge.Right | ResizeEdge.Bottom => Cursors.SizeNWSE,
        ResizeEdge.Right | ResizeEdge.Top or ResizeEdge.Left | ResizeEdge.Bottom => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    private Point GetPointerInScreenDips(MouseEventArgs e)
    {
        Point devicePoint = PointToScreen(e.GetPosition(this));
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            return target.TransformFromDevice.Transform(devicePoint);
        }

        return devicePoint;
    }

    private void ResizeFromPointer(Point pointer)
    {
        double dx = pointer.X - _resizeStartPointer.X;
        double dy = pointer.Y - _resizeStartPointer.Y;

        double left = _resizeStartBounds.Left;
        double top = _resizeStartBounds.Top;
        double width = _resizeStartBounds.Width;
        double height = _resizeStartBounds.Height;

        if (_activeResizeEdge.HasFlag(ResizeEdge.Left))
        {
            width = Math.Max(MinWidth, _resizeStartBounds.Width - dx);
            left = _resizeStartBounds.Right - width;
        }
        else if (_activeResizeEdge.HasFlag(ResizeEdge.Right))
        {
            width = Math.Max(MinWidth, _resizeStartBounds.Width + dx);
        }

        if (_activeResizeEdge.HasFlag(ResizeEdge.Top))
        {
            height = Math.Max(MinHeight, _resizeStartBounds.Height - dy);
            top = _resizeStartBounds.Bottom - height;
        }
        else if (_activeResizeEdge.HasFlag(ResizeEdge.Bottom))
        {
            height = Math.Max(MinHeight, _resizeStartBounds.Height + dy);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void EndManualResize()
    {
        _activeResizeEdge = ResizeEdge.None;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Cursor = Cursors.Arrow;
    }

    /// <summary>
    /// Apply the stored geometry, install the resize hit-test hook and paint the
    /// system backdrop. Called once the window has a handle.
    /// </summary>
    private void ApplyAppearance()
    {
        ApplySettings();
        Opacity = 1.0;
        Topmost = _settings.AlwaysOnTop;

        // Accent acrylic works on a popup-style window and does not need the standard
        // resize frame. Strip it unconditionally on that path: retaining
        // WS_THICKFRAME / WS_EX_WINDOWEDGE is the visible second rectangle reported by
        // users. The DWM system-backdrop path may still retain those styles for
        // diagnostics because some Windows builds refuse to paint the material
        // without them.
        if (_settings.BackdropMethod == BackdropMethodKind.Accent
            || !_settings.KeepFrameStylesForBackdrop)
        {
            AcrylicBackdrop.RemoveFrameStyles(this);
        }

        ApplyDropShadow(_settings.ShowDropShadow);

        // Diagnostic: replace the translucent glass with an opaque fill, so the panel
        // can be judged with the DWM material taken out of the picture entirely.
        if (_settings.OpaquePanel)
        {
            FrameBorder.Background = new SolidColorBrush(
                _settings.UseDarkTheme
                    ? System.Windows.Media.Color.FromRgb(0x1A, 0x1B, 0x1F)
                    : System.Windows.Media.Colors.White);
        }

        // Do not apply a native backdrop to an AllowsTransparency window. It would
        // supply the opaque backing colour that the user is trying to remove.
        DegradedText.Text = string.Empty;
    }

    /// <summary>
    /// Add or remove the drop shadow.
    ///
    /// Deliberately not in the XAML. A Border.Effect makes WPF render the border into
    /// an intermediate surface, and in a chrome-less window that depends on the DWM
    /// material showing through, that surface composites as an opaque black
    /// rectangle - the material never appears, so the panel looks painted on black
    /// and lowering opacity only reveals more black. Applying it from code keeps it
    /// switchable per machine.
    /// </summary>
    private void ApplyDropShadow(bool enabled)
    {
        if (!enabled)
        {
            FrameBorder.Effect = null;
            return;
        }

        FrameBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 0,
            Opacity = 0.28,
            Color = System.Windows.Media.Colors.Black,
        };
    }

    /// <summary>Rows currently displayed.</summary>
    public ObservableCollection<RecentItemViewModel> Items { get; } = new();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ItemList.ItemsSource = Items;
        _relativeTimeTimer.Start();

        // Geometry, resize hook and backdrop all need a window handle, so they are
        // applied here rather than in the constructor. The window has not been painted
        // yet at this point, so the geometry is applied without a visible jump.
        ApplyAppearance();

        // Paint the previous scan immediately, so the panel is not empty for the
        // fraction of a second the first real scan takes. Rows are shown as
        // Unreachable because the snapshot cannot know whether targets still exist.
        IReadOnlyList<RecentItem>? cached = LoadSnapshot();
        if (cached is { Count: > 0 })
        {
            ApplyItems(cached);
            StatusText.Text = $"正在刷新…（显示上次的 {cached.Count} 项）";
            EmptyState.Visibility = Visibility.Collapsed;
        }

        // Automatic refresh. The watcher collapses bursts internally, so a single
        // refresh per batch operation is enough; no extra debounce here.
        _watcher = new RecentWatcher();
        _watcher.RefreshRequested += (_, _) => _ = RefreshAsync();
        _watcher.Degraded += (_, message) => DegradedText.Text = message;
        _watcher.Start();

        _ = RefreshAsync();
    }

    /// <summary>Load the cached snapshot, tolerating any failure.</summary>
    private static IReadOnlyList<RecentItem>? LoadSnapshot()
    {
        try
        {
            ScanSnapshot? snapshot = SnapshotStore.Load();
            if (snapshot is null)
            {
                return null;
            }

            // A snapshot is only worth showing while it is plausibly still relevant.
            // Beyond a week the list is more misleading than an empty panel for the
            // fraction of a second the real scan takes.
            TimeSpan age = DateTimeOffset.Now - snapshot.CapturedAt;
            if (age > TimeSpan.FromDays(7) || age < TimeSpan.Zero)
            {
                return null;
            }

            return SnapshotStore.ToItems(snapshot);
        }
        catch (Exception)
        {
            // A snapshot is an optimisation; never let it block startup.
            return null;
        }
    }

    /// <summary>
    /// Persist settings and the current list.
    ///
    /// Invoked by the host when the panel is hidden, and again on close. It cannot
    /// hang off OnClosed alone: the close button hides the window rather than closing
    /// it, so a user who never opens the tray menu - and whose process is simply
    /// terminated at shutdown - would lose their window geometry and the startup
    /// snapshot entirely.
    ///
    /// Runs on hide rather than on every change because position and size settle only
    /// once the user stops dragging.
    /// </summary>
    public void SaveState()
    {
        try
        {
            // RestoreBounds is used because a maximised or hidden window reports
            // meaningless Left/Top; RestoreBounds is where it will come back to.
            Rect bounds = RestoreBounds;

            _settings = _settings with
            {
                WindowLeft = bounds.Left,
                WindowTop = bounds.Top,
                WindowWidth = bounds.Width,
                WindowHeight = bounds.Height,
            };

            ConfigStore.Save(_settings);
        }
        catch (Exception)
        {
            // Losing a window position is not worth interrupting shutdown for.
        }

        try
        {
            // Remember what was on screen so the next start paints instantly. The
            // original timestamps are carried through, otherwise every row would show
            // as "just now" on the next launch.
            SnapshotStore.Save(Items
                .Select(vm => new RecentItem
                {
                    DisplayName = vm.DisplayName,
                    TargetPath = vm.TargetPath,
                    IsDirectory = vm.IsDirectory,
                    LastAccessTime = vm.LastAccessTime,
                    MruRank = vm.MruRank,
                })
                .ToList());
        }
        catch (Exception)
        {
            // Same reasoning.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveState();

        // Stop watching before the window goes away, so a late event cannot try to
        // touch a disposed dispatcher.
        _watcher?.Dispose();
        _relativeTimeTimer.Stop();
        EndManualResize();
        base.OnClosed(e);
    }

    /// <summary>
    /// Apply saved geometry, then the backdrop.
    ///
    /// Ordering matters: the backdrop needs a window handle, and the geometry should
    /// be in place before the first paint so the panel does not visibly jump.
    /// </summary>
    private void ApplySettings()
    {
        _settings = ConfigStore.Load().Sanitized(
            (int)SystemParameters.VirtualScreenLeft,
            (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth,
            (int)SystemParameters.VirtualScreenHeight);

        if (_settings.WindowWidth is { } width && _settings.WindowHeight is { } height)
        {
            Width = width;
            Height = height;
        }

        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            // A scan is already running. The watcher will fire again if anything
            // else changed, so dropping this request loses nothing, and queueing it
            // would risk an unbounded backlog during heavy activity.
            return;
        }

        _busy = true;
        StatusText.Text = "正在扫描…";

        try
        {
            RecentScanResult result = await _scanner.ScanAsync();
            ApplyItems(result.Items);
            ApplyPanelState(result);
        }
        catch (Exception ex)
        {
            StatusText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Update the list in place instead of clearing and rebuilding it.
    ///
    /// Replacing the whole ItemsSource resets the scroll position and makes the
    /// list flicker on every automatic refresh - unacceptable once refreshes happen
    /// without the user asking. Matching is done on TargetPath, which is stable and
    /// unique after de-duplication.
    /// </summary>
    private void ApplyItems(IReadOnlyList<RecentItem> incoming)
    {
        var existing = new Dictionary<string, RecentItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (RecentItemViewModel row in Items)
        {
            existing[row.TargetPath] = row;
        }

        var desired = new List<RecentItemViewModel>(incoming.Count);
        foreach (RecentItem item in incoming)
        {
            if (existing.TryGetValue(item.TargetPath, out RecentItemViewModel? row))
            {
                // Reuse the row for its cached icon and live bindings, but refresh what
                // the fresh scan knows better. Skipping this left snapshot rows claiming
                // Unreachable forever, so every entry showed as unreachable after a
                // restart.
                row.UpdateValidity(item.Validity);
                desired.Add(row);
            }
            else
            {
                desired.Add(new RecentItemViewModel(item));
            }
        }

        // Remove rows that disappeared, then place the rest in the new order.
        var stillPresent = new HashSet<string>(incoming.Select(i => i.TargetPath), StringComparer.OrdinalIgnoreCase);
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (!stillPresent.Contains(Items[i].TargetPath))
            {
                Items.RemoveAt(i);
            }
        }

        for (int target = 0; target < desired.Count; target++)
        {
            RecentItemViewModel row = desired[target];
            int current = Items.IndexOf(row);
            if (current < 0)
            {
                Items.Insert(target, row);
            }
            else if (current != target)
            {
                Items.Move(current, target);
            }
        }
    }

    private void ApplyPanelState(RecentScanResult result)
    {
        bool hasItems = Items.Count > 0;

        EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        switch (result.Panel)
        {
            case PanelState.ClosedEmpty:
                EmptyTitle.Text = "系统未记录最近项目";
                EmptyDetail.Text =
                    "Windows 的「在开始菜单、跳转列表和文件资源管理器中显示最近打开的项目」已关闭，"
                    + "且没有留存的历史记录。RecentDock 无法补全从未被记录的内容。";
                EmptyActionButton.Visibility = Visibility.Visible;
                break;

            case PanelState.ActiveEmpty:
                EmptyTitle.Text = "还没有记录";
                EmptyDetail.Text = "打开任意文档或文件夹后，这里会自动出现。";
                EmptyActionButton.Visibility = Visibility.Collapsed;
                break;

            case PanelState.ClosedWithHistory:
                EmptyTitle.Text = "记录功能已关闭";
                EmptyDetail.Text = "上面展示的是关闭之前留存的历史记录，不会再有新增。";
                EmptyActionButton.Visibility = Visibility.Visible;
                break;

            default:
                EmptyTitle.Text = string.Empty;
                EmptyDetail.Text = string.Empty;
                EmptyActionButton.Visibility = Visibility.Collapsed;
                break;
        }

        // The notice strip appears whenever tracking is off, because "why is
        // nothing new showing up" is the first question a user will ask.
        bool trackingOff = result.Tracking != TrackingState.Enabled;
        NoticeBar.Visibility = trackingOff ? Visibility.Visible : Visibility.Collapsed;

        if (trackingOff)
        {
            NoticeText.Text = result.Tracking == TrackingState.DisabledByPolicy
                ? "最近项目记录已被组策略禁用"
                : "最近项目记录已关闭，正在展示已有历史";
        }

        StatusText.Text = $"共 {Items.Count} 项"
            + (result.UnresolvedLinkCount > 0
                ? $"（跳过 {result.UnresolvedLinkCount} 条无法解析的记录）"
                : string.Empty);

        // The cleanup affordance only appears when there is something to clean, so
        // it never invites a destructive action that would do nothing.
        _missingCount = Items.Count(i => i.IsMissing);
        CleanupButton.Visibility = _missingCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_missingCount > 0)
        {
            CleanupButton.Content = $"清理失效 ({_missingCount})";
            CleanupButton.ToolTip = $"删除 {_missingCount} 条目标已不存在的记录（不影响文件本身）";
        }

        ScanCompleted?.Invoke(this, Items.Count);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        _ = RefreshAsync();
        e.Handled = true;
    }

    /// <summary>
    /// Open the appearance settings dialog. Emitted rather than handled here so the
    /// window stays free of window-management concerns.
    /// </summary>
    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        AppearanceSettingsRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// Open the Windows page carrying the recent-items toggle. Kept separate from the
    /// appearance dialog: this one is about data recording, the other about looks, and
    /// mixing them would be confusing.
    /// </summary>
    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        WindowsSettingsRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// Select on the first click, open on the double click.
    ///
    /// This lives on the ListView's PreviewMouseLeftButtonDown rather than on the
    /// row template, and it deliberately does NOT set e.Handled except when opening.
    ///
    /// Why: an earlier version handled MouseLeftButtonDown on the row Grid and set
    /// e.Handled = true on every activation. That suppressed the event before the
    /// ListViewItem could see it, so single clicks never moved the selection - a
    /// user-visible bug where the highlight only followed a double click. Handling
    /// the preview event and leaving it unhandled lets selection proceed normally.
    ///
    /// Selection is also set explicitly, because a click on a child element (the
    /// name TextBlock, say) does not always reach the item container's own logic.
    /// </summary>
    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        RecentItemViewModel? item = FindRowAt(e.OriginalSource);
        if (item is null)
        {
            // Clicked empty space: let the ListView clear the selection itself.
            return;
        }

        if (!ReferenceEquals(ItemList.SelectedItem, item))
        {
            ItemList.SelectedItem = item;
        }

        if (e.ClickCount == 2)
        {
            OpenTarget(item);

            // Mark handled only for the activation, so the second click of a
            // double-click does not also toggle selection state.
            e.Handled = true;
        }
    }

    /// <summary>
    /// Walk up from the element under the cursor to the row it belongs to.
    ///
    /// OriginalSource is used rather than Source because the event starts at the
    /// deepest visual, and ItemsControl.ContainerFromElement is what understands the
    /// ListView's container hierarchy (including with virtualization enabled).
    /// </summary>
    private RecentItemViewModel? FindRowAt(object? source)
    {
        if (source is not DependencyObject element)
        {
            return null;
        }

        var container = ItemsControl.ContainerFromElement(ItemList, element) as ListViewItem;
        return container?.DataContext as RecentItemViewModel;
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (ResolveContextMenuItem(sender) is { } item)
        {
            OpenTarget(item);
        }
    }

    /// <summary>
    /// Delete this entry's record from the Recent folder.
    ///
    /// This writes to the user's profile, so it asks first and says exactly what it
    /// will do: the target file is never touched, only the record of it.
    /// </summary>
    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (ResolveContextMenuItem(sender) is not { } item)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"从最近列表中移除这条记录？\n\n{item.DisplayName}\n\n"
            + "会删除该文件在 Windows 最近列表中的记录（若存在多条则一并删除）。"
            + "文件本身不会被删除。",
            "RecentDock",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            int removed = RecentEntryCleaner.RemoveByTargetPath(item.TargetPath);
            StatusText.Text = removed > 0 ? "已移除 1 条记录" : "未找到对应记录，可能已被清理";
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "移除失败：" + ex.Message;
        }
    }

    /// <summary>Delete every record whose target no longer exists.</summary>
    private void OnCleanupClick(object sender, RoutedEventArgs e)
    {
        int missing = _missingCount;
        if (missing == 0)
        {
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"删除 {missing} 条目标已不存在的记录？\n\n"
            + "只会删除 Windows 的最近访问记录，不会影响任何现有文件。",
            "RecentDock",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            int removed = RecentEntryCleaner.RemoveMissingTargets();
            StatusText.Text = $"已清理 {removed} 条失效记录";
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = "清理失败：" + ex.Message;
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ResolveContextMenuItem(sender) is not { } item)
        {
            return;
        }

        if (item.IsMissing)
        {
            StatusText.Text = "目标已不存在，无法定位。";
            return;
        }

        try
        {
            string arguments = item.IsDirectory
                ? $"\"{item.TargetPath}\""
                : $"/select,\"{item.TargetPath}\"";

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = arguments,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法打开所在位置：" + ex.Message;
        }
    }

    /// <summary>Walk up from a context-menu item to the row it belongs to.</summary>
    private static RecentItemViewModel? ResolveContextMenuItem(object sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }

        // MenuItem -> ContextMenu -> the row element that owns it.
        if (menuItem.Parent is ContextMenu contextMenu
            && contextMenu.PlacementTarget is FrameworkElement { DataContext: RecentItemViewModel item })
        {
            return item;
        }

        return null;
    }

    private void OpenTarget(RecentItemViewModel item)
    {
        if (item.IsMissing)
        {
            StatusText.Text = $"目标已不存在：{item.TargetPath}";
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.TargetPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            // Most common cause: no application is associated with the extension.
            StatusText.Text = "无法打开：" + ex.Message;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void OnDragAreaMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-clicking the header toggles between compact and tall.
            Height = Height > 400 ? 320 : 560;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws if the button was already released; harmless.
        }
    }
}
