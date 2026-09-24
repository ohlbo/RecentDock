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
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    /// <summary>Key for the shared folder icon.</summary>
    private const string FolderKey = "<dir>";

    /// <summary>Key for the generic file icon, used for unknown or absent targets.</summary>
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
    /// <param name="targetExists">
    /// When false the generic icon is used: asking the shell for the icon of a
    /// path that no longer exists would make it resolve a nonexistent item.
    /// </param>
    public static BitmapSource? GetIcon(string extension, bool isDirectory, bool targetExists)
    {
        string key;
        uint attributes;

        if (isDirectory)
        {
            key = FolderKey;
            attributes = FILE_ATTRIBUTE_DIRECTORY;
        }
        else if (!targetExists || string.IsNullOrEmpty(extension))
        {
            key = UnknownFileKey;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }
        else
        {
            key = extension;
            attributes = FILE_ATTRIBUTE_NORMAL;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out BitmapSource? cached))
            {
                return cached;
            }

            BitmapSource? bitmap = Load(extension, attributes);
            Cache[key] = bitmap;
            return bitmap;
        }
    }

    private static BitmapSource? Load(string extension, uint attributes)
    {
        string probeName = attributes == FILE_ATTRIBUTE_DIRECTORY ? "folder" : "file" + extension;

        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(
            probeName,
            attributes,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

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
