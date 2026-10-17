using System.Runtime.InteropServices;
using RecentDock.Core.Interop;

namespace RecentDock.App;

/// <summary>
/// Reveals a file in its folder.
///
/// Implemented with SHOpenFolderAndSelectItems rather than by starting
/// "explorer.exe /select,...". Spawning explorer.exe asks Windows to start a SECOND
/// shell process with our own token, and in a restricted context that process cannot
/// initialise its UI (the user sees explorer.exe exit with 0xc0000142 - STATUS_DLL_INIT_FAILED)
/// even though the running shell would have been perfectly happy to highlight the file.
/// The shell API instead hands the pidl to the shell that is already there.
///
/// The explorer.exe route is kept as a fallback: it is what works when the shell API
/// is unavailable, and it costs one line.
/// </summary>
internal static class ShellLocate
{
    /// <summary>
    /// Try to reveal <paramref name="path"/> in its parent folder. Returns an error
    /// message when the shell refused, or null on success.
    /// </summary>
    /// <remarks>
    /// Deliberately no explorer.exe fallback. Spawning one looks like a harmless
    /// second chance, but from a restricted token explorer.exe cannot initialise its
    /// UI at all: the user gets an "explorer.exe - 应用程序错误 0xc0000142" dialog, which
    /// is both frightening and useless. A clear message beats a second failure.
    /// </remarks>
    public static string? TryReveal(string path) => TryRevealWithShellApi(path);

    private static string? TryRevealWithShellApi(string path)
    {
        // SHParseDisplayName / SHOpenFolderAndSelectItems go through COM. The WPF UI
        // thread is STA but not necessarily COM-initialised, and this is also reached
        // from contexts where it is not, so initialisation is ensured locally.
        if (!StaComScope.EnsureInitialized())
        {
            return "无法初始化 COM";
        }

        IntPtr pidl = IntPtr.Zero;

        try
        {
            int result = SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (result != 0 || pidl == IntPtr.Zero)
            {
                return $"shell 无法解析该路径（0x{result:X8}）";
            }

            result = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
            return result == 0 ? null : $"shell 拒绝定位（0x{result:X8}）";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name + "：" + ex.Message;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", SetLastError = false)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);
}
