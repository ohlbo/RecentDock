using Microsoft.Win32;

namespace RecentDock.App;

/// <summary>
/// "Start with Windows" support via the per-user Run key.
///
/// HKCU rather than HKLM deliberately: it needs no elevation, triggers no UAC
/// prompt, and affects only the current user. It is also the least alarming place
/// to write from a security software perspective, which matters for a utility that
/// already calls COM and sits resident.
///
/// The value stores the executable path quoted. The path can contain spaces
/// (C:\Program Files\...), and an unquoted path with spaces is a well-known
/// hijack vector that security tooling flags.
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RecentDock";

    /// <summary>True when the Run entry exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                      or UnauthorizedAccessException
                                      or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Enable or remove the Run entry. Returns false when the registry write was
    /// refused; the caller must not claim success in that case.
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                      or UnauthorizedAccessException
                                      or IOException)
        {
            return false;
        }
    }
}
