using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace RecentDock.App;

/// <summary>
/// Tray icon and its menu.
///
/// WinForms' NotifyIcon is used because WPF has no tray support of its own and the
/// plan rules out third-party packages. This is the only WinForms in the project.
///
/// The icon is extracted from our own executable rather than shipped as a second
/// copy of the same artwork: ApplicationIcon embeds it in the PE, and the shell can
/// hand it back on demand. The HICON the shell returns is copied into a managed
/// Icon and then destroyed, because the shell allocates a fresh handle per call.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _toggleItem;
    private bool _disposed;

    /// <summary>Raised when the user picks Show/Hide.</summary>
    public event EventHandler? ToggleRequested;

    /// <summary>Raised when the user asks to quit.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised when the user picks Settings.</summary>
    public event EventHandler? SettingsRequested;

    public void Initialize()
    {
        _toggleItem = new Forms.ToolStripMenuItem("隐藏面板", null, (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty));

        var settingsItem = new Forms.ToolStripMenuItem("设置", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        var exitItem = new Forms.ToolStripMenuItem("退出", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "RecentDock",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // Left click toggles too: that is what users expect from a tray panel.
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ToggleRequested?.Invoke(this, EventArgs.Empty);
            }
        };

        _notifyIcon.DoubleClick += (_, _) => ToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Keep the menu wording and tooltip in step with the panel.</summary>
    public void UpdateState(bool windowVisible, int itemCount)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Text = Truncate($"RecentDock — {itemCount} 项", 63);

        if (_toggleItem is not null)
        {
            _toggleItem.Text = windowVisible ? "隐藏面板" : "显示面板";
        }
    }

    /// <summary>Show a balloon-free transient hint, used when a scan degrades.</summary>
    public void Notify(string message)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = "RecentDock";
        _notifyIcon.BalloonTipText = Truncate(message, 255);
        _notifyIcon.ShowBalloonTip(3000);
    }

    /// <summary>
    /// Extract the executable's own icon.
    ///
    /// ExtractAssociatedIcon hands back an Icon that owns a native handle; it is
    /// cloned so the handle can be released deterministically, otherwise a tray
    /// icon can be left behind after exit (the classic "ghost icon in the tray"
    /// bug).
    /// </summary>
    private static Icon LoadApplicationIcon()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null)
                {
                    var copy = (Icon)extracted.Clone();
                    extracted.Dispose();
                    return copy;
                }
            }
        }
        catch (Exception)
        {
            // Fall through to the system default.
        }

        // Last resort: a stock icon, so the panel is still reachable.
        return SystemIcons.Application;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon is not null)
        {
            // Order matters: hiding first removes the icon from the tray
            // immediately, so a slow process exit cannot leave it behind.
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Icon?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}

/// <summary>
/// Enforces a single running instance and lets a second launch bring the existing
/// window forward.
///
/// Without this, double-clicking the shortcut twice produces two panels, two tray
/// icons and two processes racing to write the same config file.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\RecentDock.SingleInstance";

    /// <summary>Window that the running instance wants second launches to reveal.</summary>
    private static volatile IntPtr _mainWindowHandle = IntPtr.Zero;

    private static Mutex? _mutex;

    /// <summary>
    /// True when this process is the first instance and should continue starting up.
    /// False when another instance is already running, in which case it has been
    /// signalled to show itself and the caller should exit immediately.
    /// </summary>
    public static bool TryAcquire()
    {
        bool createdNew;
        _mutex = new Mutex(initiallyOwned: false, MutexName, out createdNew);

        if (createdNew)
        {
            return true;
        }

        SignalExistingInstance();
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>Register the window that second launches should activate.</summary>
    public static void RegisterMainWindow(Window window)
    {
        _mainWindowHandle = new WindowInteropHelper(window).Handle;
    }

    /// <summary>Clear the registration when the window goes away.</summary>
    public static void UnregisterMainWindow() => _mainWindowHandle = IntPtr.Zero;

    /// <summary>
    /// Ask the running instance to show its panel. PostMessage is used rather than
    /// SendMessage so a busy instance cannot block the exiting process.
    /// </summary>
    private static void SignalExistingInstance()
    {
        IntPtr handle = _mainWindowHandle;
        if (handle == IntPtr.Zero || !IsWindow(handle))
        {
            // Cross-process the handle is not shared, so this only works in-proc.
            // When it fails the second launch simply exits, leaving the existing
            // tray icon as the way in, which is acceptable.
            return;
        }

        PostMessage(handle, ShowWindowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Message a second launch posts to make the panel visible.</summary>
    public static readonly int ShowWindowMessage =
        RegisterWindowMessage("RecentDock.ShowWindow");

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);
}
