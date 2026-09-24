using System.Runtime.InteropServices;
using System.Text;

namespace RecentDock.Core.Interop;

/// <summary>
/// Resolves a shell ITEMIDLIST to a filesystem path.
/// </summary>
public static class PidlResolver
{
    /// <summary>
    /// MAX_PATH plus headroom. Both arguments must be supplied: passing only the
    /// capacity (new StringBuilder(600)) leaves MaxCapacity at the StringBuilder
    /// default of 16, the native write overruns the buffer, and the process dies
    /// with an AccessViolationException (exit code 0xC0000005) rather than a
    /// catchable exception. This was hit for real while reverse-engineering the
    /// record layout, so the two-argument form is mandatory here.
    /// </summary>
    private const int PathBufferLength = 260;

    /// <summary>
    /// Resolve a PIDL that starts at <paramref name="offset"/> inside
    /// <paramref name="buffer"/>.
    ///
    /// The caller is responsible for having found the correct offset; see
    /// RecentDocsRecordParser for how it is derived.
    /// </summary>
    /// <returns>
    /// The absolute path, or null when the item has no filesystem path (a virtual
    /// shell folder such as a UWP app entry) or the buffer is not a valid PIDL.
    /// </returns>
    public static string? Resolve(byte[] buffer, int offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (offset <= 0 || offset >= buffer.Length)
        {
            return null;
        }

        int spanLength = buffer.Length - offset;
        if (spanLength < 3)
        {
            return null;
        }

        IntPtr native = Marshal.AllocHGlobal(spanLength);
        try
        {
            Marshal.Copy(buffer, offset, native, spanLength);

            var path = new StringBuilder(PathBufferLength, PathBufferLength);
            if (!SHGetPathFromIDListW(native, path))
            {
                return null;
            }

            string result = path.ToString();
            return string.IsNullOrEmpty(result) ? null : result;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);
}
