using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecentDock.Core.Storage;

/// <summary>One entry as stored in the snapshot.</summary>
public sealed record SnapshotItem
{
    public required string DisplayName { get; init; }

    public required string TargetPath { get; init; }

    public bool IsDirectory { get; init; }

    public DateTimeOffset LastAccessTime { get; init; }

    public int? MruRank { get; init; }
}

/// <summary>
/// The previous scan, cached so the panel can paint immediately on startup instead
/// of showing an empty list while the first scan runs.
///
/// Only the fields needed to render a row are stored. Icons are rebuilt from the
/// extension on load and are already cached by extension, so nothing visual is lost
/// and the file stays small.
/// </summary>
public sealed record ScanSnapshot
{
    /// <summary>When the snapshot was written, so stale caches are recognisable.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    public IReadOnlyList<SnapshotItem> Items { get; init; } = Array.Empty<SnapshotItem>();
}

/// <summary>
/// Reads and writes <see cref="ScanSnapshot"/>.
///
/// Failure-tolerant by design: a snapshot is an optimisation, never a source of
/// truth. Anything unreadable is discarded and the caller simply waits for the real
/// scan.
/// </summary>
public static class SnapshotStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Maximum rows persisted. The panel shows at most a few hundred, so there is no
    /// reason to write an unbounded file.
    /// </summary>
    private const int MaxItems = 200;

    /// <summary>
    /// Load the cached snapshot, or null when absent or unreadable.
    /// </summary>
    public static ScanSnapshot? Load()
    {
        try
        {
            string path = AppPaths.SnapshotFile;
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ScanSnapshot>(json, Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Persist a scan result. Returns false when nothing was written.
    ///
    /// An empty result is deliberately NOT written: caching "no items" would make
    /// the next start paint an empty panel before the real scan completes, which is
    /// exactly the impression the snapshot exists to avoid. Leaving the previous
    /// snapshot in place is better, since the correction arrives within a second.
    /// </summary>
    public static bool Save(IReadOnlyList<RecentItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (items.Count == 0)
        {
            return false;
        }

        if (!AppPaths.TryEnsureDirectory())
        {
            return false;
        }

        var snapshot = new ScanSnapshot
        {
            CapturedAt = DateTimeOffset.Now,
            Items = items
                .Take(MaxItems)
                .Select(i => new SnapshotItem
                {
                    DisplayName = i.DisplayName,
                    TargetPath = i.TargetPath,
                    IsDirectory = i.IsDirectory,
                    LastAccessTime = i.LastAccessTime,
                    MruRank = i.MruRank,
                })
                .ToList(),
        };

        return AtomicFile.WriteJson(AppPaths.SnapshotFile, snapshot, Options);
    }

    /// <summary>
    /// Convert snapshot rows back into scan items for display.
    ///
    /// Validity is reported as Unreachable rather than Valid: the snapshot cannot
    /// know whether a target still exists, and claiming Valid would briefly present
    /// stale rows as live, clickable entries. Unreachable keeps them visible without
    /// promising they work.
    /// </summary>
    public static IReadOnlyList<RecentItem> ToItems(ScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Items
            .Select(s => new RecentItem
            {
                DisplayName = s.DisplayName,
                TargetPath = s.TargetPath,
                IsDirectory = s.IsDirectory,
                LastAccessTime = s.LastAccessTime,
                MruRank = s.MruRank,
                Validity = ItemValidity.Unreachable,
                Source = ItemSource.None,
            })
            .ToList();
    }
}
