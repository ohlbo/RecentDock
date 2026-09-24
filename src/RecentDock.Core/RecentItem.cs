namespace RecentDock.Core;

/// <summary>How trustworthy a record's target is.</summary>
public enum ItemValidity
{
    /// <summary>Target exists on disk.</summary>
    Valid,

    /// <summary>Target path resolved, but nothing is there any more.</summary>
    Missing,

    /// <summary>
    /// Not probed: the target is on a UNC share or a drive letter that is not
    /// currently mounted. Probing these blocks for seconds, so they are reported
    /// as unreachable without touching the filesystem.
    /// </summary>
    Unreachable,

    /// <summary>The .lnk could not be parsed at all.</summary>
    Unparsable,
}

/// <summary>Which source(s) produced a record.</summary>
[Flags]
public enum ItemSource
{
    None = 0,

    /// <summary>Parsed from a .lnk in the Recent folder. The only source of a real target path.</summary>
    RecentLnk = 1,

    /// <summary>
    /// Present in the RecentDocs registry key. Contributes ordering and the
    /// folder/file classification, but NOT a target path: the PIDL in that key
    /// resolves to the .lnk file itself, never to the document.
    /// </summary>
    Registry = 2,

    Both = RecentLnk | Registry,
}

/// <summary>
/// One entry in the panel.
/// </summary>
public sealed record RecentItem
{
    /// <summary>File name shown in the list, e.g. "report.pdf".</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Full path of the document or folder. Always sourced from the .lnk, because
    /// the RecentDocs PIDL resolves to the shortcut rather than to the target.
    /// </summary>
    public required string TargetPath { get; init; }

    public bool IsDirectory { get; init; }

    /// <summary>
    /// Approximate access time, taken from the .lnk file's LastWriteTime.
    /// Used only when no registry rank is available.
    /// </summary>
    public DateTimeOffset LastAccessTime { get; init; }

    /// <summary>
    /// Rank in the RecentDocs MRUListEx chain: 1 is most recent. Null when the
    /// record has no registry presence.
    ///
    /// This is authoritative for ordering. It beats LastAccessTime, and it removes
    /// the ordering jitter when several files are opened within the same second.
    /// </summary>
    public int? MruRank { get; init; }

    public ItemValidity Validity { get; init; } = ItemValidity.Valid;

    public ItemSource Source { get; init; } = ItemSource.RecentLnk;

    /// <summary>True when the record came from the RecentDocs 'Folder' bucket.</summary>
    public bool? RegistrySaysDirectory { get; init; }
}
