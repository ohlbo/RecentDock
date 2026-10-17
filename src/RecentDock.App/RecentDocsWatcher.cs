using System.Runtime.InteropServices;

namespace RecentDock.App;

/// <summary>
/// Watches the RecentDocs registry key for changes.
///
/// Why this exists: the key is where Windows records that a document was opened. It
/// is also invisible to <see cref="System.IO.FileSystemWatcher"/>, which was the
/// panel's only prompt trigger, so an open that did not also rewrite a .lnk in the
/// Recent folder was noticed only by the 30-second poll. Practically that meant the
/// open time of a file the user had just reopened in Explorer could sit at its old
/// value for half a minute.
///
/// .NET has no registry watcher, but Win32 does: RegNotifyChangeKeyValue blocks on an
/// event and signals it whenever the key or any of its subkeys changes. Used
/// asynchronously (bAsync=true) it needs no dedicated thread, so the notification is
/// wired into the thread pool and re-armed from the callback.
///
/// This is an enhancement, never a requirement: if it cannot start, the existing
/// poll timer still covers the same key, just slower.
/// </summary>
public sealed class RecentDocsWatcher : IDisposable
{
    private const string SubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs";

    private const int HKEY_CURRENT_USER = unchecked((int)0x80000001);

    /// <summary>KEY_READ, which already includes KEY_NOTIFY.</summary>
    private const uint KEY_READ = 0x20019;

    /// <summary>Fires on any value write, including a new numbered record.</summary>
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    private readonly object _gate = new();
    private IntPtr _key = IntPtr.Zero;
    private EventWaitHandle? _signal;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    /// <summary>Raised on a thread-pool thread when the key changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Start watching. Returns false when the key cannot be opened or a notification
    /// cannot be armed, in which case the caller keeps polling.
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_key != IntPtr.Zero)
            {
                return true;
            }

            int error = RegOpenKeyEx(HKEY_CURRENT_USER, SubKey, 0, KEY_READ, out IntPtr key);
            if (error != 0 || key == IntPtr.Zero)
            {
                return false;
            }

            _key = key;

            // ManualReset is not needed: every notification is consumed by one wait and
            // then explicitly reset before the key is re-armed.
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset);

            if (!Arm())
            {
                CloseHandleAndSignal();
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Re-arm the one-shot notification.
    ///
    /// RegNotifyChangeKeyValue is not a subscription: it reports one change and must be
    /// called again after each. Arming is repeated from the callback, never from the
    /// caller's thread, because the key handle has to outlive every pending
    /// notification.
    /// </summary>
    private bool Arm()
    {
        if (_key == IntPtr.Zero || _signal is null)
        {
            return false;
        }

        int error = RegNotifyChangeKeyValue(
            _key,
            true,               // bWatchSubtree: the per-extension subkeys too
            REG_NOTIFY_CHANGE_LAST_SET,
            _signal.SafeWaitHandle.DangerousGetHandle(),
            true);              // fAsynchronous: signal an event, do not block

        if (error != 0)
        {
            return false;
        }

        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            OnSignal,
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        return true;
    }

    private void OnSignal(object? state, bool timedOut)
    {
        // The registration with executeOnlyOnce has already been removed, so the field
        // must not be unregistered again.
        _registration = null;

        if (timedOut)
        {
            return;
        }

        try
        {
            _signal?.Reset();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Raised outside the lock: subscribers marshal to the dispatcher, and a
        // subscriber that synchronously touched this watcher would otherwise deadlock
        // against the re-arm below.
        Changed?.Invoke(this, EventArgs.Empty);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Arm();
        }
    }

    private void CloseHandleAndSignal()
    {
        try
        {
            _registration?.Unregister(null);
        }
        catch (Exception)
        {
            // Shutting down; nothing useful to do.
        }

        _registration = null;

        if (_key != IntPtr.Zero)
        {
            RegCloseKey(_key);
            _key = IntPtr.Zero;
        }

        _signal?.Dispose();
        _signal = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseHandleAndSignal();
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int RegOpenKeyEx(
        int hKey, string lpSubKey, int ulOptions, uint samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern int RegNotifyChangeKeyValue(
        IntPtr hKey, bool bWatchSubtree, uint dwNotifyFilter, IntPtr hEvent, bool fAsynchronous);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern int RegCloseKey(IntPtr hKey);
}
