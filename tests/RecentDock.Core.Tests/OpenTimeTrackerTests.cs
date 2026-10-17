using RecentDock.Core;
using RecentDock.Core.Storage;
using Xunit;

namespace RecentDock.Core.Tests;

/// <summary>
/// Rules for deriving "last opened" from MRU movement.
///
/// These are pure-logic tests on purpose: the real inputs (a live RecentDocs key that
/// only Windows writes) cannot be produced in a test, so the decision table is pinned
/// here and the reader of the code can see exactly what is and is not inferred.
/// </summary>
public sealed class OpenTimeTrackerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.FromHours(8));

    private static RecentItem Item(string path, int? rank, DateTimeOffset recordTime)
        => new()
        {
            DisplayName = Path.GetFileName(path),
            TargetPath = path,
            LastAccessTime = recordTime,
            MruRank = rank,
            Validity = ItemValidity.Valid,
            Source = ItemSource.Both,
        };

    // ------------------------------------------------------------------ basics

    [Fact]
    public void FirstScan_RecordsRanksWithoutInventingAnOpenTime()
    {
        RecentItem item = Item(@"C:\docs\a.pdf", rank: 5, recordTime: Now.AddDays(-3));

        (IReadOnlyList<RecentItem> result, OpenTimeState state) = OpenTimeTracker.Observe(
            new[] { item }, recordCount: 100, previous: new OpenTimeState(), now: Now);

        Assert.Equal(Now.AddDays(-3), result[0].LastAccessTime);
        Assert.Equal(5, state.Entries[@"C:\docs\a.pdf"].Rank);
        Assert.Null(state.Entries[@"C:\docs\a.pdf"].OpenedAt);
    }

    [Fact]
    public void RankImprovement_IsAttributedToNow()
    {
        OpenTimeState previous = new OpenTimeState
        {
            ObservedAt = Now.AddMinutes(-1),
            RecordCount = 100,
            Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\docs\a.pdf"] = new() { Rank = 20, OpenedAt = null, LastSeenAt = Now.AddMinutes(-1) },
            },
        };

        (IReadOnlyList<RecentItem> result, _) = OpenTimeTracker.Observe(
            new[] { Item(@"C:\docs\a.pdf", rank: 5, recordTime: Now.AddDays(-3)) },
            recordCount: 100,
            previous,
            now: Now);

        Assert.Equal(Now, result[0].LastAccessTime);
    }

    [Fact]
    public void MissingRegistryRecord_StillImprovesWhenItComesBack()
    {
        OpenTimeState previous = new OpenTimeState
        {
            ObservedAt = Now.AddMinutes(-1),
            RecordCount = 100,
            Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\docs\a.pdf"] = new() { Rank = null, OpenedAt = null, LastSeenAt = Now.AddMinutes(-1) },
            },
        };

        (IReadOnlyList<RecentItem> result, _) = OpenTimeTracker.Observe(
            new[] { Item(@"C:\docs\a.pdf", rank: 3, recordTime: Now.AddDays(-3)) },
            recordCount: 100,
            previous,
            now: Now);

        // A record that reappears with a rank is a new MRU entry, but "no previous
        // rank" is not evidence of a promotion, so the record time stands.
        Assert.Equal(Now.AddDays(-3), result[0].LastAccessTime);
    }

    [Fact]
    public void UnchangedRank_KeepsTheTimeItAlreadyHad()
    {
        DateTimeOffset earlier = Now.AddHours(-2);
        OpenTimeState previous = new OpenTimeState
        {
            ObservedAt = Now.AddMinutes(-1),
            RecordCount = 100,
            Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\docs\a.pdf"] = new() { Rank = 5, OpenedAt = earlier, LastSeenAt = Now.AddMinutes(-1) },
            },
        };

        (IReadOnlyList<RecentItem> result, OpenTimeState state) = OpenTimeTracker.Observe(
            new[] { Item(@"C:\docs\a.pdf", rank: 5, recordTime: Now.AddDays(-3)) },
            recordCount: 100,
            previous,
            now: Now);

        Assert.Equal(earlier, result[0].LastAccessTime);
        Assert.Equal(earlier, state.Entries[@"C:\docs\a.pdf"].OpenedAt);
    }

    // ------------------------------------------------------- false positives

    [Fact]
    public void ShrinkingRecordCount_DoesNotTurnDeletionsIntoOpens()
    {
        // Deleting one record promotes everything below it. Attributing that to "was
        // just opened" would relabel half the panel after a cleanup.
        OpenTimeState previous = new OpenTimeState
        {
            ObservedAt = Now.AddMinutes(-1),
            RecordCount = 100,
            Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\docs\a.pdf"] = new() { Rank = 20, OpenedAt = null, LastSeenAt = Now.AddMinutes(-1) },
            },
        };

        (IReadOnlyList<RecentItem> result, OpenTimeState state) = OpenTimeTracker.Observe(
            new[] { Item(@"C:\docs\a.pdf", rank: 19, recordTime: Now.AddDays(-3)) },
            recordCount: 99,
            previous,
            now: Now);

        Assert.Equal(Now.AddDays(-3), result[0].LastAccessTime);
        Assert.Equal(19, state.Entries[@"C:\docs\a.pdf"].Rank);
    }

    [Fact]
    public void LongGap_DatesThePromotionToThePreviousScan()
    {
        // The app was closed for a day; the file may have been opened at any point
        // since. "Just now" would be fabrication, so the observation time is used.
        DateTimeOffset lastScan = Now.AddDays(-1);
        OpenTimeState previous = new OpenTimeState
        {
            ObservedAt = lastScan,
            RecordCount = 100,
            Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\docs\a.pdf"] = new() { Rank = 20, OpenedAt = null, LastSeenAt = lastScan },
            },
        };

        (IReadOnlyList<RecentItem> result, _) = OpenTimeTracker.Observe(
            new[] { Item(@"C:\docs\a.pdf", rank: 1, recordTime: Now.AddDays(-9)) },
            recordCount: 100,
            previous,
            now: Now);

        Assert.Equal(lastScan, result[0].LastAccessTime);
    }

    [Fact]
    public void RecordTimestampWins_WhenItIsNewerThanTheTrackedOpen()
    {
        // A record that was deleted and recreated carries a fresh .lnk time; the
        // tracker must not drag it back to the remembered value.
        OpenTimeState previous = new OpenTimeState
        {
            ObservedAt = Now.AddMinutes(-1),
            RecordCount = 100,
            Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\docs\a.pdf"] = new() { Rank = 5, OpenedAt = Now.AddDays(-4), LastSeenAt = Now.AddMinutes(-1) },
            },
        };

        (IReadOnlyList<RecentItem> result, _) = OpenTimeTracker.Observe(
            new[] { Item(@"C:\docs\a.pdf", rank: 5, recordTime: Now.AddHours(-1)) },
            recordCount: 100,
            previous,
            now: Now);

        Assert.Equal(Now.AddHours(-1), result[0].LastAccessTime);
    }

    // ---------------------------------------------------------- persistence

    [Fact]
    public void Store_RoundTripsAndMatchesPathsCaseInsensitively()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RecentDock-opentime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? original = AppPaths.StateDirectoryOverride;

        try
        {
            AppPaths.StateDirectoryOverride = directory;

            var state = new OpenTimeState
            {
                ObservedAt = Now,
                RecordCount = 7,
                Entries = new Dictionary<string, OpenTimeEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    [@"C:\Docs\A.PDF"] = new() { Rank = 2, OpenedAt = Now, LastSeenAt = Now },
                },
            };

            Assert.True(OpenTimeStore.Save(state));

            OpenTimeState loaded = OpenTimeStore.Load();
            Assert.Equal(7, loaded.RecordCount);
            Assert.Equal(Now, loaded.ObservedAt);

            // Windows paths are case-insensitive; a dictionary that is not would
            // silently lose the match after a restart.
            Assert.True(loaded.Entries.TryGetValue(@"c:\docs\a.pdf", out OpenTimeEntry? entry));
            Assert.Equal(2, entry!.Rank);
        }
        finally
        {
            AppPaths.StateDirectoryOverride = original;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // Best effort cleanup.
            }
        }
    }

    [Fact]
    public void CorruptFile_IsIgnoredRatherThanThrown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "RecentDock-opentime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? original = AppPaths.StateDirectoryOverride;

        try
        {
            AppPaths.StateDirectoryOverride = directory;
            File.WriteAllText(Path.Combine(directory, "open-times.json"), "{ not json");

            OpenTimeState state = OpenTimeStore.Load();

            Assert.NotNull(state);
            Assert.Empty(state.Entries);
        }
        finally
        {
            AppPaths.StateDirectoryOverride = original;

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // Best effort cleanup.
            }
        }
    }
}
