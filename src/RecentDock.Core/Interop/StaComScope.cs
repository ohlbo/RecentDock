using System.Runtime.InteropServices;

namespace RecentDock.Core.Interop;

/// <summary>
/// Initialises COM on the current thread as a single-threaded apartment.
///
/// Scanning the Recent folder has to happen off the UI thread, but IShellLink
/// requires an STA. A raw thread-pool thread has no apartment, so calling COM
/// from one without this scope fails in ways that surface only as unparsable
/// records. Use it like:
///
///     await StaComScope.RunAsync(() => scan(), cancellationToken);
/// </summary>
public static class StaComScope
{
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint COINIT_DISABLE_OLE1DDE = 0x4;

    [ThreadStatic]
    private static bool _initializedOnThisThread;

    /// <summary>
    /// True when the current thread has completed COM initialisation through this
    /// scope. Used by <see cref="LinkResolver"/> to fail loudly rather than
    /// silently returning nothing.
    /// </summary>
    public static bool IsCurrentThreadInitialized => _initializedOnThisThread;

    /// <summary>
    /// Initialise COM on the calling thread if it is not already usable, so that
    /// callers do not have to know their apartment state.
    ///
    /// Why this exists: requiring every caller to arrange an STA is a contract that
    /// is easy to violate and fails at runtime. It was violated for real by the
    /// "remove from list" path, which runs after an await and therefore resumes on a
    /// thread-pool thread with no apartment, so the removal threw instead of working.
    /// With this, <see cref="LinkResolver"/> works from any thread: the UI thread
    /// (already STA), a dedicated STA scan thread, or a pool thread that initialises
    /// COM on first use.
    ///
    /// Returns true when COM is usable. False only for RPC_E_CHANGED_MODE, i.e. the
    /// thread was already in an MTA, which cannot be changed after the fact. COM
    /// still functions in that case - it is simply marshalled - so callers treat it
    /// as usable too.
    /// </summary>
    public static bool EnsureInitialized()
    {
        if (_initializedOnThisThread)
        {
            return true;
        }

        int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);

        // S_OK (0)           : initialised here; balance with CoUninitialize at exit.
        // S_FALSE (1)        : already initialised on this thread; nothing to balance.
        // RPC_E_CHANGED_MODE : already in an MTA. Usable via marshalling.
        // Anything negative  : genuinely unusable.
        bool usable = hr >= 0 || hr == RPC_E_CHANGED_MODE;

        if (usable)
        {
            _initializedOnThisThread = true;

            if (hr == 0)
            {
                // Balance the successful initialisation. The thread this runs on is
                // either the UI thread or a pool thread, neither of which we control
                // the lifetime of, so uninitialising is registered as a best-effort
                // process-exit action rather than done immediately: releasing it early
                // would tear down COM objects other code may still be using.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => CoUninitialize();
            }
        }

        return usable;
    }

    /// <summary>
    /// Run <paramref name="work"/> on a dedicated STA thread.
    ///
    /// A dedicated thread is used rather than the thread pool because a pool
    /// thread's apartment mode is not ours to choose and is not reset reliably
    /// between work items.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);

            // S_OK (0) and S_FALSE (1, already initialised) both mean COM is usable.
            // RPC_E_CHANGED_MODE means someone already put this thread in an MTA,
            // which we cannot fix here, so report rather than pretend success.
            bool usable = hr >= 0;
            bool weOwnIt = hr == 0;

            if (!usable && hr != RPC_E_CHANGED_MODE)
            {
                tcs.TrySetException(Marshal.GetExceptionForHR(hr)
                                    ?? new InvalidOperationException("CoInitializeEx failed: 0x" + hr.ToString("X8")));
                return;
            }

            _initializedOnThisThread = usable;

            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }

                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                _initializedOnThisThread = false;
                if (weOwnIt)
                {
                    CoUninitialize();
                }
            }
        });

        thread.IsBackground = true;
        thread.Name = "RecentDock.Scan";
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern void CoUninitialize();
}
