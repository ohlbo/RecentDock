using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecentDock.Core.Storage;

/// <summary>What is remembered about one target.</summary>
public sealed record OpenTimeEntry
{
    /// <summary>MRU position seen at <see cref="LastSeenAt"/>. Null when the target has no registry record.</summary>
    public int? Rank { get; init; }

    /// <summary>
    /// Best known last-open time, or null when only the record's own timestamp is
    /// available.
    /// </summary>
    public DateTimeOffset? OpenedAt { get; init; }

    /// <summary>When this entry was last matched by a scan. Used only for pruning.</summary>
    public DateTimeOffset LastSeenAt { get; init; }
}

/// <summary>Persisted tracker state.</summary>
public sealed record OpenTimeState
{
    /// <summary>When the previous scan ran. Null on the first run.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>Registry record count seen by the previous scan.</summary>
    public int? RecordCount { get; init; }

    public IReadOnlyDictionary<string, OpenTimeEntry> Entries { get; init; } =
        new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Reads and writes <see cref="OpenTimeState"/>. Failure tolerant, like every other
/// store here: losing this file only costs precision, never correctness.
/// </summary>
public static class OpenTimeStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static OpenTimeState Load()
    {
        try
        {
            string path = AppPaths.OpenTimesFile;
            if (!File.Exists(path))
            {
                return new OpenTimeState();
            }

            OpenTimeState? state = JsonSerializer.Deserialize<OpenTimeState>(File.ReadAllText(path), Options);
            if (state is null)
            {
                return new OpenTimeState();
            }

            // The deserializer hands back a case-SENSITIVE dictionary. Windows paths
            // are not, and the whole point of this file is matching paths across runs.
            return state with
            {
                Entries = new Dictionary<string, OpenTimeEntry>(state.Entries, StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new OpenTimeState();
        }
    }

    public static bool Save(OpenTimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!AppPaths.TryEnsureDirectory())
        {
            return false;
        }

        return AtomicFile.WriteJson(AppPaths.OpenTimesFile, state, Options);
    }
}

/// <summary>
/// Derives "when was this last opened" from movement in the RecentDocs MRU chain.
///
/// Why this is needed at all: neither source stores an open timestamp. A RecentDocs
/// record is a name plus a PIDL with no time field (verified byte-wise: no plausible
/// FILETIME anywhere in the buffer), and the .lnk in the Recent folder is written
/// ONCE - measured on a live profile, not one of 79 document shortcuts had a write
/// time later than its creation time. So the .lnk timestamp the panel used before is
/// really "when the record was first created", which is exactly the complaint that
/// reopening a file did not update the displayed time.
///
/// What Windows does guarantee is ordering: opening a file moves its record to the
/// front of MRUListEx. A target whose rank IMPROVED since the previous scan was
/// therefore opened since then, and its open time is now.
///
/// Known limits, documented rather than papered over:
///
///   * Reopening the item that is already first among its peers does not change its
///     rank, so it cannot be detected from the record alone. The panel's own clicks
///     are not affected: those are marked directly.
///   * Deleting a record promotes everything below it. That is a rank improvement
///     that means the opposite of "opened", so a scan that sees the record count
///     shrink attributes nothing.
///   * If the app was not running, the promotion is dated to the last observation
///     rather than to "now", which can be hours off in the pessimistic direction but
///     is never a fabricated "just now".
/// </summary>
public static class OpenTimeTracker
{
    /// <summary>Beyond this gap the promotion is dated to the previous scan.</summary>
    private static readonly TimeSpan AttributionWindow = TimeSpan.FromMinutes(10);

    /// <summary>Entries pruned after this long without being seen.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromDays(120);

    /// <summary>Hard cap, so a machine with huge history cannot grow the file forever.</summary>
    private const int MaxEntries = 400;

    /// <summary>
    /// Load previous state, apply it, and persist the result. Used by the scanner.
    /// </summary>
    public static IReadOnlyList<RecentItem> Apply(
        IReadOnlyList<RecentItem> items, int recordCount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);

        OpenTimeState previous = OpenTimeStore.Load();
        (IReadOnlyList<RecentItem> updated, OpenTimeState state) = Observe(items, recordCount, previous, now);
        OpenTimeStore.Save(state);
        return updated;
    }

    /// <summary>
    /// Pure core of <see cref="Apply"/>, so the rules can be tested without disk IO.
    /// </summary>
    public static (IReadOnlyList<RecentItem> Items, OpenTimeState State) Observe(
        IReadOnlyList<RecentItem> items, int recordCount, OpenTimeState previous, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(previous);

        var entries = new Dictionary<string, OpenTimeEntry>(previous.Entries, StringComparer.OrdinalIgnoreCase);

        bool deletions = previous.RecordCount is int before && recordCount < before;

        // Dating a promotion: if the previous scan was long ago, the open could have
        // happened any time since. Claiming "just now" then would be inventing data.
        DateTimeOffset promotionTime =
            previous.ObservedAt is { } observedAt && now - observedAt > AttributionWindow
                ? observedAt
                : now;

        var result = new List<RecentItem>(items.Count);

        foreach (RecentItem item in items)
        {
            entries.TryGetValue(item.TargetPath, out OpenTimeEntry? prior);
            DateTimeOffset? openedAt = prior?.OpenedAt;

            bool promoted =
                !deletions
                && prior?.Rank is int previousRank
                && item.MruRank is int currentRank
                && currentRank < previousRank;

            if (promoted)
            {
                openedAt = promotionTime;
            }

            DateTimeOffset effective = item.LastAccessTime;
            if (openedAt is { } opened && opened > effective)
            {
                effective = opened;
            }

            entries[item.TargetPath] = new OpenTimeEntry
            {
                Rank = item.MruRank,
                OpenedAt = openedAt,
                LastSeenAt = now,
            };

            result.Add(item with { LastAccessTime = effective });
        }

        return (result, new OpenTimeState
        {
            ObservedAt = now,
            RecordCount = recordCount,
            Entries = Prune(entries, now),
        });
    }

    private static Dictionary<string, OpenTimeEntry> Prune(
        Dictionary<string, OpenTimeEntry> entries, DateTimeOffset now)
    {
        IEnumerable<KeyValuePair<string, OpenTimeEntry>> kept = entries
            .Where(entry => now - entry.Value.LastSeenAt <= Retention)
            .OrderByDescending(entry => entry.Value.LastSeenAt)
            .Take(MaxEntries);

        return new Dictionary<string, OpenTimeEntry>(kept, StringComparer.OrdinalIgnoreCase);
    }
}
