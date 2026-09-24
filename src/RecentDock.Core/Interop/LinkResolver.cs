using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RecentDock.Core.Interop;

/// <summary>
/// Resolves the target of a Windows shell link (.lnk) file.
///
/// Guidance encoded here, all of it load-bearing:
///
///   * Use IShellLinkW, never IShellLinkA. The ANSI variant mangles non-ASCII
///     paths, which is fatal on a Chinese-locale machine: every measured sample
///     in the verification run had CJK file names.
///
///   * Never call IShellLink.Resolve. It makes the shell hunt for the target,
///     including network resolution, and blocks for seconds on an offline share
///     or an unmounted mapped drive. GetPath with SLGP_RAWPATH is enough.
///
///   * Check the HRESULT on GetPath explicitly. A miss can come back as S_FALSE
///     rather than a throwing HRESULT, so relying on exceptions alone silently
///     produces empty paths.
///
///   * Every call must run on an STA thread with COM initialised. The WPF UI
///     thread qualifies, but scanning happens on a background thread, which must
///     call CoInitializeEx(APARTMENTTHREADED) itself. See StaComScope.
/// </summary>
public static class LinkResolver
{
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const uint SLGP_RAWPATH = 0x00000004;

    private const int MaxPathChars = 260;

    /// <summary>CLSID_ShellLink.</summary>
    private static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>
    /// Resolve a .lnk to its target path.
    /// </summary>
    /// <returns>
    /// The target path, or null when the link cannot be read or carries no path.
    /// The path is NOT expanded or validated; callers decide how to probe it.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No longer thrown. COM is initialised on demand; see the comment below.
    /// </exception>
    public static string? TryGetTargetPath(string linkPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkPath);

        // COM is initialised on demand rather than demanded of the caller.
        //
        // An earlier version threw when the thread had no apartment. That broke the
        // "remove from list" path for real: it runs after an await, so it resumes on
        // a thread-pool thread with no apartment, and removal failed with
        // "LinkResolver requires a COM-initialized STA thread". Failing at runtime
        // for a condition the callee can fix itself is the wrong trade.
        if (!StaComScope.EnsureInitialized())
        {
            return null;
        }

        object? shellLinkObject = null;
        try
        {
            Type? comType = Type.GetTypeFromCLSID(ShellLinkClsid);
            if (comType is null)
            {
                return null;
            }

            shellLinkObject = Activator.CreateInstance(comType);
            if (shellLinkObject is not IShellLinkW shellLink)
            {
                return null;
            }

            if (shellLinkObject is not IPersistFile persistFile)
            {
                return null;
            }

            // STGM_READ. IPersistFile.Load parses the .lnk; it does not trigger
            // target resolution, unlike IShellLink.Resolve.
            persistFile.Load(linkPath, 0);

            var buffer = new StringBuilder(MaxPathChars);
            int hr = shellLink.GetPath(buffer, buffer.Capacity, IntPtr.Zero, SLGP_RAWPATH);

            // Both S_OK and S_FALSE can carry a usable path; anything negative is a
            // real failure.
            if (hr < 0)
            {
                return null;
            }

            string path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (COMException)
        {
            // Malformed or unreadable link. Skipping one record must never abort
            // the whole scan.
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        finally
        {
            if (shellLinkObject is not null && Marshal.IsComObject(shellLinkObject))
            {
                // Release eagerly: a full scan touches hundreds of links and must
                // not accumulate shell objects.
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        // [PreserveSig] is mandatory. Without it the CLR treats the returned
        // HRESULT as the method's error channel, swallows it, and declares the
        // method void - so the S_FALSE case this code inspects is unreachable.
        // The compiler reports it as CS0029 (cannot convert void to int).
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
    }
}
