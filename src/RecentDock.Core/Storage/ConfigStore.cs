using System.Text.Json;
using System.Text.Json.Serialization;

namespace RecentDock.Core.Storage;

/// <summary>
/// Where RecentDock keeps its own state.
///
/// %APPDATA%\RecentDock, never the install directory: a framework-dependent
/// deployment may sit in a read-only location, and per-user state does not belong
/// next to the binaries.
/// </summary>
public static class AppPaths
{
    private static string? _overrideDirectory;

    /// <summary>
    /// Redirect the state directory. Intended for tests, which must not write into
    /// the real user profile; also useful for a portable build.
    /// Null restores the default.
    /// </summary>
    public static string? StateDirectoryOverride
    {
        get => _overrideDirectory;
        set => _overrideDirectory = value;
    }

    /// <summary>Directory holding config.json and snapshot.json. Created on demand.</summary>
    public static string StateDirectory
    {
        get
        {
            if (_overrideDirectory is not null)
            {
                return _overrideDirectory;
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(root, "RecentDock");
        }
    }

    public static string ConfigFile => Path.Combine(StateDirectory, "config.json");

    public static string SnapshotFile => Path.Combine(StateDirectory, "snapshot.json");

    /// <summary>
    /// Ensure the state directory exists. Returns false when it cannot be created,
    /// in which case callers degrade to in-memory defaults rather than failing.
    /// </summary>
    public static bool TryEnsureDirectory()
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>User-facing settings that survive a restart.</summary>
public sealed record UiSettings
{
    /// <summary>Saved window position. Null means "centre on screen".</summary>
    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    /// <summary>Saved window size. Null means use the built-in default.</summary>
    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    /// <summary>Maximum rows to display.</summary>
    public int MaxItems { get; init; } = 50;

    /// <summary>
    /// Show records whose target no longer exists. Off by default: they are noise
    /// unless the user is specifically cleaning up.
    /// </summary>
    public bool ShowMissingTargets { get; init; }

    /// <summary>
    /// Use the DWM system backdrop (acrylic/mica) for a translucent window. Falls
    /// back to a solid panel automatically when the OS or composition state does not
    /// support it, so this is safe to leave on.
    /// </summary>
    public bool UseAcrylicBackdrop { get; init; } = true;

    /// <summary>Start minimised to the tray.</summary>
    public bool StartHidden { get; init; }

    /// <summary>
    /// Clamp values that would put the panel somewhere unusable.
    ///
    /// A saved position can become off-screen when a monitor is unplugged, which
    /// would leave the user with an invisible panel and no way to recover it.
    ///
    /// The rule requires a margin of <see cref="MinVisible"/> pixels of the window
    /// to be on screen in BOTH axes. A sliver of a few pixels is rejected too: it is
    /// technically clickable but the header - which is the drag handle - would be
    /// off-screen, so the panel could not be moved back.
    /// </summary>
    public UiSettings Sanitized(int virtualScreenLeft, int virtualScreenTop, int virtualScreenWidth, int virtualScreenHeight)
    {
        const double MinVisible = 80;
        const double MinWidth = 420;
        const double MinHeight = 220;

        // The window's own size is needed to know where its far edge lands.
        double frameWidth = WindowWidth ?? 720;
        double frameHeight = WindowHeight ?? 560;

        double? left = WindowLeft;
        double? top = WindowTop;

        if (left.HasValue && top.HasValue)
        {
            double right = left.Value + frameWidth;
            double bottom = top.Value + frameHeight;

            bool horizontallyReachable =
                left.Value <= virtualScreenLeft + virtualScreenWidth - MinVisible &&
                right >= virtualScreenLeft + MinVisible;

            bool verticallyReachable =
                top.Value <= virtualScreenTop + virtualScreenHeight - MinVisible &&
                bottom >= virtualScreenTop + MinVisible;

            if (!horizontallyReachable || !verticallyReachable)
            {
                left = null;
                top = null;
            }
        }

        double? width = WindowWidth;
        double? height = WindowHeight;

        if (width is < MinWidth)
        {
            width = null;
        }

        if (height is < MinHeight)
        {
            height = null;
        }

        return this with
        {
            WindowLeft = left,
            WindowTop = top,
            WindowWidth = width,
            WindowHeight = height,
            MaxItems = Math.Clamp(MaxItems, 1, 500),
        };
    }
}

/// <summary>
/// Loads and saves <see cref="UiSettings"/>.
///
/// Every operation is failure-tolerant: a missing file yields defaults, and a
/// damaged file is set aside rather than allowed to crash startup. A utility that
/// refuses to launch because its own config is malformed is worse than one that
/// forgets a window position.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Load settings, or defaults when absent or unreadable.</summary>
    public static UiSettings Load()
    {
        try
        {
            string path = AppPaths.ConfigFile;
            if (!File.Exists(path))
            {
                return new UiSettings();
            }

            string json = File.ReadAllText(path);
            UiSettings? settings = JsonSerializer.Deserialize<UiSettings>(json, Options);
            return settings ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Quarantine();
            return new UiSettings();
        }
    }

    /// <summary>Persist settings atomically. Returns false when the write was refused.</summary>
    public static bool Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!AppPaths.TryEnsureDirectory())
        {
            return false;
        }

        return AtomicFile.WriteJson(AppPaths.ConfigFile, settings, Options);
    }

    /// <summary>
    /// Move an unreadable config aside so the next start is clean, while keeping the
    /// bad file for diagnosis.
    /// </summary>
    private static void Quarantine()
    {
        try
        {
            string path = AppPaths.ConfigFile;
            if (File.Exists(path))
            {
                File.Move(path, path + ".corrupt", overwrite: true);
            }
        }
        catch (Exception)
        {
            // Best effort. Failing to quarantine must not prevent startup.
        }
    }
}

/// <summary>
/// Writes files by writing a sibling temporary file and replacing the target, so a
/// crash or power loss mid-write cannot leave a half-written file behind.
/// </summary>
internal static class AtomicFile
{
    public static bool WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        string temp = path + ".tmp";

        try
        {
            string json = JsonSerializer.Serialize(value, options);
            File.WriteAllText(temp, json);

            if (File.Exists(path))
            {
                // Replace is atomic on NTFS and preserves the original until the new
                // content is fully in place.
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
