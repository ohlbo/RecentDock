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

/// <summary>Which API provides the translucent background.</summary>
public enum BackdropMethodKind
{
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE. Modern and documented.</summary>
    Dwm = 0,

    /// <summary>
    /// SetWindowCompositionAttribute acrylic. Undocumented but widely used, and it
    /// carries its own tint so it does not depend on DWM resolving a theme.
    /// </summary>
    Accent = 1,
}

/// <summary>DWM system backdrop material.</summary>
public enum BackdropMaterialKind
{
    /// <summary>DWMSBT_MAINWINDOW. Denser, like a normal app window.</summary>
    Mica = 0,

    /// <summary>DWMSBT_TRANSIENTWINDOW. More translucent.</summary>
    Acrylic = 1,
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

    /// <summary>Show the parent folder below each file name.</summary>
    public bool ShowFilePath { get; init; } = true;

    /// <summary>Keep the panel above ordinary application windows.</summary>
    public bool AlwaysOnTop { get; init; } = true;

    /// <summary>
    /// Use the DWM system backdrop (acrylic/mica) for a translucent window. Falls
    /// back to a solid panel automatically when the OS or composition state does not
    /// support it, so this is safe to leave on.
    /// </summary>
    public bool UseAcrylicBackdrop { get; init; } = true;

    /// <summary>
    /// Dark theme. False (the default) uses the light, white-glass appearance.
    ///
    /// This must not be confused with the Windows system theme: it is passed to the
    /// DWM as the material's light/dark variant, and the XAML palette is built for the
    /// light variant. Following the system setting automatically is possible later,
    /// but the palette is currently a single compiled-in set, so a switch would need
    /// runtime resource swapping.
    /// </summary>
    public bool UseDarkTheme { get; init; }

    /// <summary>
    /// Panel opacity, 0.3 to 1.0. 1.0 is fully opaque; lower values let more of the
    /// desktop (and the acrylic material) read through.
    ///
    /// This scales the BASELINE alphas in Theme.xaml rather than replacing them, so
    /// the designed relationship between the three surfaces is preserved at every
    /// setting instead of each one drifting independently.
    /// </summary>
    public double PanelOpacity { get; init; } = 1.0;

    /// <summary>
    /// Body text size in device-independent pixels. Drives the row name, the folder
    /// line, the relative time and the status bar together.
    /// </summary>
    public double FontSize { get; init; } = 13;

    /// <summary>Row icon size in device-independent pixels.</summary>
    public double IconSize { get; init; } = 16;

    /// <summary>
    /// Draw a drop shadow around the panel.
    ///
    /// Off by default, and that default is deliberate: the shadow is implemented with
    /// a Border.Effect, which makes WPF render the border into an intermediate
    /// surface. In a chrome-less window whose appearance depends on the DWM material
    /// showing through, that intermediate surface composites as an opaque black
    /// rectangle - so the panel looked like it was painted on black and lowering the
    /// opacity only revealed more black.
    ///
    /// Kept as a switch rather than removed: on a machine where the effect composites
    /// correctly it is a nice touch, and this way it can be verified per machine.
    /// </summary>
    public bool ShowDropShadow { get; init; }

    /// <summary>
    /// Paint the panel with an OPAQUE background instead of translucent glass.
    ///
    /// A diagnostic switch, not a style option. It exists because "the panel looks
    /// black" has two possible causes that look identical on screen:
    ///
    ///   1. the DWM material is rendering dark, or
    ///   2. the XAML layer itself is drawing dark, with the material irrelevant.
    ///
    /// Setting this true removes the material from the equation entirely: the panel
    /// becomes a flat opaque surface, so if it STILL looks black the cause is the XAML
    /// (or something above it), and if it turns solid white the cause is the material.
    /// One run settles a question that several rounds of attribute inspection did not.
    /// </summary>
    public bool OpaquePanel { get; init; }

    /// <summary>
    /// Keep the window styles that DWM needs in order to render the system backdrop.
    ///
    /// True by default, and the default matters: clearing WS_THICKFRAME removes the
    /// system frame, but it also appears to stop DWM rendering the acrylic/mica
    /// material at all - the panel then falls back to the app's own layers composited
    /// over black, which is exactly the "everything behind is black" symptom.
    ///
    /// Measured: with the styles cleared, eight different material configurations
    /// (mica/acrylic x dark/light x NC on/off) all sampled identically, proving the
    /// material was not involved. So the frame removal costs the material, and the
    /// frame has to be suppressed a different way.
    /// </summary>
    public bool KeepFrameStylesForBackdrop { get; init; }

    /// <summary>Which implementation produces the translucent background.</summary>
    public BackdropMethodKind BackdropMethod { get; init; } = BackdropMethodKind.Accent;

    /// <summary>Which DWM material to request, when <see cref="BackdropMethod"/> is Dwm.</summary>
    public BackdropMaterialKind BackdropMaterial { get; init; } = BackdropMaterialKind.Acrylic;

    /// <summary>
    /// Tint strength for the Accent method, 0..1. Ignored by the DWM method, which
    /// takes its colours from the system material.
    /// </summary>
    public double AccentTintOpacity { get; init; } = 0.45;

    /// <summary>
    /// Suppress DWM's non-client rendering.
    ///
    /// Removes the system frame, but it is suspect: with it disabled the material
    /// appeared not to composite at all, so it is a switch rather than a default.
    /// </summary>
    public bool SuppressNonClientFrame { get; init; }

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

            // Clamped rather than rejected: these come from sliders, so the only way
            // to get an out-of-range value is a hand-edited config, and clamping is
            // friendlier than silently ignoring the whole file.
            PanelOpacity = Math.Clamp(PanelOpacity, 0.3, 1.0),
            FontSize = Math.Clamp(FontSize, 10, 24),
            IconSize = Math.Clamp(IconSize, 12, 48),
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
