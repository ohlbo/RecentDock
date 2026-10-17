using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace RecentDock.Core.Interop;

/// <summary>
/// Extracts shell icons for files and folders.
///
/// Guidelines encoded here:
///
///   * Icons are CACHED BY EXTENSION. A list of a few hundred recent items has far
///     fewer distinct extensions, and rolling without a cache churns GDI objects
///     on every scroll.
///
///   * Every HICON returned by the shell MUST be destroyed with DestroyIcon. The
///     shell hands back a fresh icon handle per call; leaking them eventually
///     exhausts the GDI handle quota for the process.
///
///   * System.Drawing is deliberately avoided. It is restricted on modern .NET and
///     interoperates awkwardly with WPF; CreateBitmapSourceFromHIcon is the direct
///     route.
/// </summary>
public static class ShellIconProvider
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    /// <summary>Key for the shared folder icon.</summary>
    private const string FolderKey = "<dir>";

    /// <summary>Key for the generic file icon, used when the name carries no extension.</summary>
    private const string UnknownFileKey = "<file>";

    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>
    /// Icon for a recent item. Must be called on a thread with a Dispatcher
    /// (BitmapSource creation is not free-threaded), so call it during UI
    /// projection, not from the scan thread.
    /// </summary>
    /// <param name="extension">Extension including the dot, e.g. ".pdf".</param>
    /// <param name="isDirectory">True to force the folder icon.</param>
    public static BitmapSource? GetIcon(string extension, bool isDirectory)
    {
        // The cache key is the EXTENSION for every file, including rows whose target
        // is missing or unreachable.
        //
        // That is the whole point of this method, and it is not an optimisation
        // detail: an earlier version keyed such rows on a single shared "<file>"
        // slot while still resolving the icon from the ROW'S OWN extension. The first
        // row created cached its icon under that shared key, and every row of a
        // different type afterwards was served the first row's icon. Rows restored
        // from the startup snapshot are all created in that state, so after a restart
        // png, mp4, md and xopp entries showed each other's icons - the reported
        // "图标出现错误" bug. Keying on the extension makes the cache entry describe
        // exactly what is being cached.
        string key = isDirectory
            ? FolderKey
            : string.IsNullOrEmpty(extension) ? UnknownFileKey : extension;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out BitmapSource? cached))
            {
                return cached;
            }

            BitmapSource? bitmap = Load(extension, isDirectory);
            Cache[key] = bitmap;
            return bitmap;
        }
    }

    /// <summary>
    /// Ask the shell for one icon.
    ///
    /// SHGFI_USEFILEATTRIBUTES makes the shell resolve the icon from the extension
    /// alone, without touching the filesystem. That is deliberate: the panel runs on
    /// a background scan of paths that may sit on an offline share, and stat-ing each
    /// one to fetch an icon would stall the list. The association itself (including a
    /// per-user default app) is still honoured - it is keyed by extension either way.
    ///
    /// The LARGE (32 px) variant is requested rather than the 16 px one, because the
    /// row icon size is user-adjustable up to 48 px: starting from 32 keeps that
    /// scaling soft rather than blocky, and 32 downscaled to the 16 px default stays
    /// sharp.
    /// </summary>
    private static BitmapSource? Load(string extension, bool isDirectory)
    {
        string probeName = isDirectory ? "folder" : "file" + extension;

        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(
            probeName,
            isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze(); // allow cross-thread reuse from the cache
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            // Mandatory: the shell allocates a new icon per call.
            DestroyIcon(info.hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
