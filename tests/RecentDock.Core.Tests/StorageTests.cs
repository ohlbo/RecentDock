using RecentDock.Core;
using RecentDock.Core.Storage;
using Xunit;

namespace RecentDock.Core.Tests;

/// <summary>
/// Persistence tests.
///
/// These write to a temporary directory rather than the real profile: a test suite
/// that overwrites the user's own config would be worse than no tests.
/// </summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _stateDirectory;

    public StorageTests()
    {
        _stateDirectory = Path.Combine(Path.GetTempPath(), "RecentDock-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDirectory);
        AppPaths.StateDirectoryOverride = _stateDirectory;
    }

    public void Dispose()
    {
        AppPaths.StateDirectoryOverride = null;

        try
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
        catch (Exception)
        {
            // Best effort cleanup.
        }
    }

    // ------------------------------------------------------------ settings

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        UiSettings settings = ConfigStore.Load();

        Assert.Null(settings.WindowLeft);
        Assert.Null(settings.WindowTop);
        Assert.Equal(50, settings.MaxItems);
        Assert.False(settings.ShowMissingTargets);
        Assert.True(settings.ShowFilePath);
        Assert.True(settings.AlwaysOnTop);
        Assert.True(settings.UseAcrylicBackdrop);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var original = new UiSettings
        {
            WindowLeft = 120.5,
            WindowTop = 64.25,
            WindowWidth = 800,
            WindowHeight = 600,
            MaxItems = 25,
            ShowMissingTargets = true,
            ShowFilePath = false,
            AlwaysOnTop = false,
            UseAcrylicBackdrop = false,
            StartHidden = true,
        };

        Assert.True(ConfigStore.Save(original));
        UiSettings loaded = ConfigStore.Load();

        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaultsAndQuarantines()
    {
        File.WriteAllText(AppPaths.ConfigFile, "{ this is not json");

        UiSettings settings = ConfigStore.Load();

        Assert.Equal(50, settings.MaxItems);
        // The bad file is kept for diagnosis rather than deleted outright.
        Assert.True(File.Exists(AppPaths.ConfigFile + ".corrupt"));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTemporaryFile()
    {
        ConfigStore.Save(new UiSettings { MaxItems = 7 });
        ConfigStore.Save(new UiSettings { MaxItems = 8 });

        Assert.False(File.Exists(AppPaths.ConfigFile + ".tmp"));
        Assert.Equal(8, ConfigStore.Load().MaxItems);
    }

    // ------------------------------------------------------------ sanitising

    [Fact]
    public void Sanitize_PositionOnScreen_IsPreserved()
    {
        var settings = new UiSettings { WindowLeft = 200, WindowTop = 150 };

        UiSettings result = settings.Sanitized(0, 0, 1920, 1080);

        Assert.Equal(200, result.WindowLeft);
        Assert.Equal(150, result.WindowTop);
    }

    [Fact]
    public void Sanitize_PositionOffScreen_IsDiscarded()
    {
        // A monitor was unplugged: the saved position is now outside the desktop.
        // Keeping it would put the panel somewhere the user cannot reach it.
        var settings = new UiSettings { WindowLeft = 4000, WindowTop = 3000 };

        UiSettings result = settings.Sanitized(0, 0, 1920, 1080);

        Assert.Null(result.WindowLeft);
        Assert.Null(result.WindowTop);
    }

    [Fact]
    public void Sanitize_SliverOnScreen_IsDiscarded()
    {
        // Left edge far off to the left with a 720-wide window leaves only ~20px on
        // screen. That is technically clickable but the header, which is the drag
        // handle, sits off-screen, so the panel could never be moved back. It must be
        // treated as unreachable.
        var settings = new UiSettings { WindowLeft = -700, WindowTop = 100, WindowWidth = 720 };

        UiSettings result = settings.Sanitized(0, 0, 1920, 1080);

        Assert.Null(result.WindowLeft);
        Assert.Null(result.WindowTop);
    }

    [Fact]
    public void Sanitize_EnoughOnScreen_IsKept()
    {
        // 300px of a 720-wide window visible, header included: usable.
        var settings = new UiSettings { WindowLeft = -420, WindowTop = 100, WindowWidth = 720 };

        UiSettings result = settings.Sanitized(0, 0, 1920, 1080);

        Assert.Equal(-420, result.WindowLeft);
        Assert.Equal(100, result.WindowTop);
    }

    [Theory]
    [InlineData(100, 100, null, null)]      // too small: discarded
    [InlineData(420, 220, 420.0, 220.0)]    // exactly at the floor: kept
    [InlineData(1024, 768, 1024.0, 768.0)]  // normal: kept
    public void Sanitize_SizeBelowFloor_IsDiscarded(
        double width,
        double height,
        double? expectedWidth,
        double? expectedHeight)
    {
        var settings = new UiSettings { WindowWidth = width, WindowHeight = height };

        UiSettings result = settings.Sanitized(0, 0, 1920, 1080);

        Assert.Equal(expectedWidth, result.WindowWidth);
        Assert.Equal(expectedHeight, result.WindowHeight);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(10_000, 500)]
    public void Sanitize_MaxItems_IsClamped(int input, int expected)
    {
        var settings = new UiSettings { MaxItems = input };

        Assert.Equal(expected, settings.Sanitized(0, 0, 1920, 1080).MaxItems);
    }

    [Fact]
    public void Sanitize_NegativeVirtualScreenOrigin_IsHonoured()
    {
        // A monitor positioned left of the primary exposes negative coordinates, and
        // those must not be mistaken for "off screen". The window here sits mostly on
        // the left monitor and crosses onto the right one.
        var settings = new UiSettings { WindowLeft = -200, WindowTop = 50, WindowWidth = 720 };

        UiSettings result = settings.Sanitized(-1920, 0, 3840, 1080);

        Assert.Equal(-200, result.WindowLeft);
        Assert.Equal(50, result.WindowTop);
    }

    [Fact]
    public void Sanitize_LeftOfAllMonitors_IsDiscarded()
    {
        // Genuinely past the left edge of the virtual desktop.
        var settings = new UiSettings { WindowLeft = -2600, WindowTop = 50, WindowWidth = 720 };

        UiSettings result = settings.Sanitized(-1920, 0, 3840, 1080);

        Assert.Null(result.WindowLeft);
    }

    // ------------------------------------------------------------ snapshot

    private static RecentItem Item(string name, bool isDirectory = false) => new()
    {
        DisplayName = name,
        TargetPath = @"C:\somewhere\" + name,
        IsDirectory = isDirectory,
        LastAccessTime = new DateTimeOffset(2026, 9, 22, 21, 45, 56, TimeSpan.FromHours(8)),
        MruRank = 3,
    };

    [Fact]
    public void Snapshot_RoundTripsItems()
    {
        var items = new[] { Item("a.pdf"), Item("folder", isDirectory: true) };

        Assert.True(SnapshotStore.Save(items));
        ScanSnapshot? loaded = SnapshotStore.Load();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Items.Count);
        Assert.Equal("a.pdf", loaded.Items[0].DisplayName);
        Assert.True(loaded.Items[1].IsDirectory);
    }

    [Fact]
    public void Snapshot_PreservesTimestamps()
    {
        // Regression guard: an earlier version stamped every row with DateTime.Now on
        // save, so every item showed as "just now" after a restart.
        Assert.True(SnapshotStore.Save(new[] { Item("a.pdf") }));

        IReadOnlyList<RecentItem> restored = SnapshotStore.ToItems(SnapshotStore.Load()!);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 21, 45, 56, TimeSpan.FromHours(8)),
            restored[0].LastAccessTime);
    }

    [Fact]
    public void Snapshot_EmptyList_IsNotPersisted()
    {
        // Caching "nothing" would make the next start paint an empty panel before the
        // real scan completes, which is exactly the flicker the snapshot prevents.
        Assert.False(SnapshotStore.Save(Array.Empty<RecentItem>()));
        Assert.Null(SnapshotStore.Load());
    }

    [Fact]
    public void Snapshot_RestoredItems_AreNotClaimedValid()
    {
        // The snapshot cannot know whether a target still exists. Reporting Valid
        // would present stale rows as live, clickable entries for a moment.
        SnapshotStore.Save(new[] { Item("a.pdf") });

        IReadOnlyList<RecentItem> restored = SnapshotStore.ToItems(SnapshotStore.Load()!);

        Assert.Equal(ItemValidity.Unreachable, restored[0].Validity);
    }

    [Fact]
    public void Snapshot_LoadMissing_ReturnsNull()
    {
        Assert.Null(SnapshotStore.Load());
    }

    [Fact]
    public void Snapshot_CorruptFile_ReturnsNull()
    {
        File.WriteAllText(AppPaths.SnapshotFile, "not json at all");

        Assert.Null(SnapshotStore.Load());
    }
}
