using Microsoft.Win32;

namespace RecentDock.Core;

/// <summary>
/// Reads the Windows recently-opened-items configuration.
///
/// Two independent switches exist and policy wins over the per-user preference:
///
///   1. HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer
///        NoRecentDocsHistory (REG_DWORD)  == 1  -> disabled by policy
///   2. HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced
///        Start_TrackDocs      (REG_DWORD)  == 0  -> disabled by the user
///
/// An ABSENT value means enabled, so a missing key or value is the normal,
/// healthy case and must never be reported as an error.
///
/// This type only READS. RecentDock never writes the tracking setting on the
/// user's behalf; the UI offers a link to the Settings page instead.
/// </summary>
public static class EnvironmentProbe
{
    private const string AdvancedKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string PolicyKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    private const string TrackDocsValueName = "Start_TrackDocs";
    private const string NoRecentDocsHistoryValueName = "NoRecentDocsHistory";

    /// <summary>
    /// Current tracking state. Never throws: unreadable or absent registry data is
    /// treated as "enabled", which is the Windows default.
    /// </summary>
    public static TrackingState GetTrackingState()
    {
        // Policy first: it overrides the per-user preference.
        if (ReadDword(Registry.CurrentUser, PolicyKeyPath, NoRecentDocsHistoryValueName) == 1)
        {
            return TrackingState.DisabledByPolicy;
        }

        // Absent == enabled. Only an explicit 0 disables.
        if (ReadDword(Registry.CurrentUser, AdvancedKeyPath, TrackDocsValueName) == 0)
        {
            return TrackingState.DisabledByUser;
        }

        return TrackingState.Enabled;
    }

    /// <summary>
    /// Raw values behind <see cref="GetTrackingState"/>, for diagnostics.
    ///
    /// Exposed because the mapping from registry values to UI toggles is not
    /// one-to-one and cannot be read off the documentation: on the development
    /// machine BOTH Start_TrackDocs and Start_TrackProgs are 0, and setting
    /// Start_TrackDocs to 1 was measured to flip the panel to ActiveWithRecords.
    /// The two values govern different things ("recently opened items" vs "app
    /// launches"), so when a user reports "I turned it on and nothing appears",
    /// having both numbers visible is what makes the cause identifiable.
    /// </summary>
    public static TrackingDiagnostics GetDiagnostics()
    {
        return new TrackingDiagnostics(
            StartTrackDocs: ReadDword(Registry.CurrentUser, AdvancedKeyPath, TrackDocsValueName),
            StartTrackProgs: ReadDword(Registry.CurrentUser, AdvancedKeyPath, "Start_TrackProgs"),
            NoRecentDocsHistory: ReadDword(Registry.CurrentUser, PolicyKeyPath, NoRecentDocsHistoryValueName));
    }

    /// <summary>
    /// Decide what the panel should show. <paramref name="sourceACount"/> is the
    /// number of usable records from the Recent folder, <paramref name="sourceBCount"/>
    /// the number from the RecentDocs key.
    ///
    /// Note that "tracking disabled" and "no data" are independent: disabling the
    /// toggle stops new records but leaves existing ones in place, so a disabled
    /// machine with history must still render that history.
    /// </summary>
    public static PanelState GetPanelState(TrackingState tracking, int sourceACount, int sourceBCount)
    {
        int total = sourceACount + sourceBCount;
        bool trackingOn = tracking == TrackingState.Enabled;

        if (trackingOn)
        {
            return total > 0 ? PanelState.ActiveWithRecords : PanelState.ActiveEmpty;
        }

        return total > 0 ? PanelState.ClosedWithHistory : PanelState.ClosedEmpty;
    }

    /// <summary>
    /// The deep link that opens the relevant Settings page. Used by the empty-state
    /// and history-notice UI so the user has a one-click way forward.
    /// <summary>
    /// Deep link for the "show recently opened items" toggle.
    ///
    /// This is the Personalization &gt; Start page, NOT a privacy page.
    /// Verified two ways: SystemSettings.dll's full set of privacy-* page
    /// identifiers contains privacy-general, privacy-activityhistory and 37 others
    /// but NO privacy-recentdocs, so that URI does not exist and opens nothing;
    /// and Microsoft Q&amp;A threads consistently place the toggle under
    /// Personalization &gt; Start.
    ///
    /// Note the toggle is a plain setting, so a policy can grey it out. When that
    /// happens this link still opens, but the user cannot change anything.
    /// </summary>
    public static string GetSettingsUri() => "ms-settings:personalization-start";

    /// <summary>
    /// Secondary entry point: the classic Folder Options dialog
    /// (Control Panel &gt; File Explorer Options &gt; View), which carries the
    /// "show recently used files in Quick access" controls.
    ///
    /// This is a control.exe command rather than an ms-settings: URI because
    /// Folder Options is not a Settings page. Callers must launch it with
    /// UseShellExecute so the shell resolves the command.
    /// </summary>
    public static string GetFolderOptionsCommand() => "control.exe";

    /// <summary>Arguments for <see cref="GetFolderOptionsCommand"/>.</summary>
    public static string GetFolderOptionsArguments() => "folders";

    /// <summary>
    /// Read a REG_DWORD, returning null when the key or value is absent or the
    /// data is an unexpected type. Absence is normal and is not an error.
    /// </summary>
    private static int? ReadDword(RegistryKey root, string subKeyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                return null;
            }

            object? raw = key.GetValue(valueName, defaultValue: null, RegistryValueOptions.None);
            if (raw is null)
            {
                return null;
            }

            // Normally a REG_DWORD arrives as int, but a REG_SZ/"1" is possible in
            // hand-edited or migrated hives, so accept a string too.
            return raw switch
            {
                int i => i,
                long l => unchecked((int)l),
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                      or UnauthorizedAccessException
                                      or IOException)
        {
            // Security software and roaming-profile weirdness can block this. Treat
            // as "unknown", which maps to enabled, and never surface a crash.
            return null;
        }
    }
}

/// <summary>
/// Raw registry values behind the tracking decision. Null means "value absent",
/// which is the healthy default and means enabled.
/// </summary>
/// <param name="StartTrackDocs">
/// Explorer\Advanced\Start_TrackDocs. Measured to be the switch that governs the
/// Recent folder and RecentDocs recording that RecentDock reads.
/// </param>
/// <param name="StartTrackProgs">
/// Explorer\Advanced\Start_TrackProgs. Separate setting about tracking app
/// launches; off by default on many machines and NOT a prerequisite for
/// RecentDock to work.
/// </param>
/// <param name="NoRecentDocsHistory">
/// Policies\Explorer\NoRecentDocsHistory. Group policy; 1 disables recording
/// outright and takes precedence over the user setting.
/// </param>
public sealed record TrackingDiagnostics(
    int? StartTrackDocs,
    int? StartTrackProgs,
    int? NoRecentDocsHistory);
