namespace RecentDock.Core;

/// <summary>
/// Whether Windows is currently recording recently-opened items.
///
/// Measured semantics (see DESIGN.md section 0): the user-facing toggle STOPS
/// TRACKING, it does not delete existing records. A machine with tracking
/// disabled can still hold a populated Recent folder and a populated RecentDocs
/// key, in which case the panel must still render that history.
/// </summary>
public enum TrackingState
{
    /// <summary>Tracking is on. New entries will appear as the user opens files.</summary>
    Enabled,

    /// <summary>
    /// Disabled by the per-user preference
    /// (HKCU\...\Explorer\Advanced\Start_TrackDocs = 0).
    /// This is what the Settings toggle writes.
    /// </summary>
    DisabledByUser,

    /// <summary>
    /// Disabled by group policy
    /// (HKCU\...\Policies\Explorer\NoRecentDocsHistory = 1).
    /// Policy wins over the per-user preference, and the user cannot change it
    /// from Settings, so the guidance shown to the user must differ.
    /// </summary>
    DisabledByPolicy,
}

/// <summary>
/// What the panel should actually show. Derived from <see cref="TrackingState"/>
/// plus how many records the two sources produced.
///
/// The split between ClosedWithHistory and ClosedEmpty matters: they need
/// completely different copy, and ClosedWithHistory is expected to be common
/// because turning the toggle off does not remove anything.
/// </summary>
public enum PanelState
{
    /// <summary>Records exist and will keep growing.</summary>
    ActiveWithRecords,

    /// <summary>Tracking on, but nothing recorded yet.</summary>
    ActiveEmpty,

    /// <summary>Tracking off, but historical records survive. Show them, plus a hint.</summary>
    ClosedWithHistory,

    /// <summary>Tracking off and both sources are empty. Show the guidance page.</summary>
    ClosedEmpty,
}
