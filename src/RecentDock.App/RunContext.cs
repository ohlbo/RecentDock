using System.Runtime.InteropServices;

namespace RecentDock.App;

/// <summary>
/// What kind of token this process is running under.
///
/// Why this matters: RecentDock has now twice been started from a restricted
/// (low-integrity) context - the kind a sandbox or a shell helper hands out - and from
/// there the two things it spends its life doing are refused by Windows:
///
///   * ShellExecute on a path outside the sandbox fails with "Access is denied",
///     which surfaces as "无法打开：..." on a perfectly valid file;
///   * the shell rejects the notification-area icon, so the panel works but the tray
///     entry is missing, and there is no obvious reason why.
///
/// Neither failure is a bug in the panel, and neither is fixable from inside it. The
/// useful thing is to SAY so instead of leaving a raw Win32 message on the status bar.
/// This is measured rather than inferred: the token's integrity level is read directly.
/// </summary>
public static class RunContext
{
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    /// <summary>SECURITY_MANDATORY_MEDIUM_RID: the level of a normally started app.</summary>
    private const int MediumRid = 0x2000;

    /// <summary>
    /// True when the process token is below medium integrity, i.e. it was started by
    /// something that intentionally restricted it (a sandbox, a low-integrity helper).
    /// </summary>
    public static bool IsRestricted => IntegrityLevelRid is int rid && rid < MediumRid;

    /// <summary>The integrity RID of the current token, or null when it cannot be read.</summary>
    public static int? IntegrityLevelRid { get; private set; } = ReadIntegrityLevelRid();

    private static int? ReadIntegrityLevelRid()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token))
            {
                return null;
            }

            if (!GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int size))
            {
                // The first call is expected to fail; it reports the required size.
                if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */ || size <= 0)
                {
                    return null;
                }
            }

            buffer = Marshal.AllocHGlobal(size);

            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
            {
                return null;
            }

            // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES Label; }; the RID is the last
            // sub-authority of the integrity SID (S-1-16-<rid>).
            IntPtr sid = Marshal.ReadIntPtr(buffer);
            int subAuthorityCount = Marshal.ReadByte(sid, 1);
            if (subAuthorityCount == 0)
            {
                return null;
            }

            IntPtr ridPointer = GetSidSubAuthority(sid, (uint)(subAuthorityCount - 1));
            return ridPointer == IntPtr.Zero ? null : Marshal.ReadInt32(ridPointer);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    /// <summary>Human-readable level, for diagnostics.</summary>
    public static string Describe() => IntegrityLevelRid switch
    {
        null => "unknown",
        < 0x1000 => "untrusted",
        < 0x2000 => "low (restricted)",
        < 0x3000 => "medium",
        < 0x4000 => "high",
        _ => "system",
    };

    /// <summary>
    /// True when asking Windows for a better token would happen WITHOUT a UAC dialog.
    ///
    /// This is what makes automatic elevation acceptable rather than a nag. With the
    /// default UAC setting every start would raise a consent prompt, so the panel must
    /// not do it. Where administrators are set to "never notify"
    /// (<c>ConsentPromptBehaviorAdmin = 0</c>), elevation is granted silently and the
    /// panel can fix its own restricted start without interrupting anything.
    ///
    /// Note that this covers the UAC dialog only. SmartScreen's app-reputation check
    /// runs on the elevation path regardless, which is why the check has to be off for
    /// a truly silent start - see README.
    /// </summary>
    public static bool ElevationIsSilent => ConsentPromptBehaviorAdmin is 0;

    private static readonly int? ConsentPromptBehaviorAdmin = ReadConsentPromptBehaviorAdmin();

    private static int? ReadConsentPromptBehaviorAdmin()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");

            return key?.GetValue("ConsentPromptBehaviorAdmin") is int value ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
