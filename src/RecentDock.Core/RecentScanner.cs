using Microsoft.Win32;
using RecentDock.Core.Interop;
using RecentDock.Core.Parsing;

namespace RecentDock.Core;

/// <summary>Outcome of one full scan, ready for the UI to render.</summary>
public sealed record RecentScanResult
{
    public required TrackingState Tracking { get; init; }

    public required PanelState Panel { get; init; }

    /// <summary>Items to show, already filtered, de-duplicated and ordered.</summary>
    public required IReadOnlyList<RecentItem> Items { get; init; }

    /// <summary>Number of .lnk files found before filtering.</summary>
    public int LinkFileCount { get; init; }

    /// <summary>Number of .lnk files that could not be resolved at all.</summary>
    public int UnresolvedLinkCount { get; init; }

    /// <summary>Number of RecentDocs records seen in the registry key.</summary>
    public int RegistryRecordCount { get; init; }
}

/// <summary>
/// Builds the panel contents from both sources.
///
/// Source A (the Recent folder) supplies the real target path: the RecentDocs PIDL
/// resolves to the shortcut and never to the document, so the registry alone
/// cannot produce a usable path.
///
/// Source B (the RecentDocs key) supplies the authoritative access order via
/// MRUListEx, plus the folder classification from the 'Folder' bucket.
///
/// The join key is the .lnk file name minus ".lnk", which equals the registry
/// record's name field.
/// </summary>
public sealed class RecentScanner
{
    private const string RecentDocsKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs";

    private const string FolderBucketName = "Folder";
    private const string MruListExValueName = "MRUListEx";

    /// <summary>
    /// Run a scan. Must be awaited, not blocked on: it marshals COM work onto a
    /// dedicated STA thread internally.
    /// </summary>
    public async Task<RecentScanResult> ScanAsync(
        RecentScannerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= RecentScannerOptions.Default;

        TrackingState tracking = EnvironmentProbe.GetTrackingState();

        string recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        string[] linkFiles = Directory.Exists(recentDirectory)
            ? Directory.GetFiles(recentDirectory, "*.lnk", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        RegistrySource registry = ReadRegistry();

        int unresolved = 0;

        List<RecentItem> items = await StaComScope.RunAsync(
            () => ScanLinks(linkFiles, registry, options, ref unresolved),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RecentItem> ordered = Order(items, options);

        return new RecentScanResult
        {
            Tracking = tracking,
            Panel = EnvironmentProbe.GetPanelState(tracking, items.Count, registry.RecordCount),
            Items = ordered,
            LinkFileCount = linkFiles.Length,
            UnresolvedLinkCount = unresolved,
            RegistryRecordCount = registry.RecordCount,
        };
    }

    private static List<RecentItem> ScanLinks(
        string[] linkFiles,
        RegistrySource registry,
        RecentScannerOptions options,
        ref int unresolved)
    {
        var results = new List<RecentItem>(linkFiles.Length);
        int failures = 0;

        foreach (string linkPath in linkFiles)
        {
            string linkFileName = Path.GetFileName(linkPath);

            // Skip the shell's own non-document entries. These have no target path
            // at all: "Internet.lnk" is a virtual network location and
            // "ms-photos<AppId>.lnk" is a UWP application identifier, not a file.
            if (options.ExcludedLinkFileNames.Contains(linkFileName))
            {
                continue;
            }

            string? target = LinkResolver.TryGetTargetPath(linkPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                failures++;
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(target);
            (bool isDirectory, ItemValidity validity) = ProbeTarget(expanded);

            if (!options.IncludeMissingTargets && validity == ItemValidity.Missing)
            {
                continue;
            }

            string name = Path.GetFileNameWithoutExtension(linkFileName);

            results.Add(new RecentItem
            {
                DisplayName = name,
                TargetPath = expanded,
                IsDirectory = isDirectory,
                LastAccessTime = File.GetLastWriteTime(linkPath),
                MruRank = registry.RankByName.TryGetValue(name, out int rank) ? rank : null,
                Validity = validity,
                Source = registry.RankByName.ContainsKey(name) || registry.FolderNames.Contains(name)
                    ? ItemSource.Both
                    : ItemSource.RecentLnk,
                RegistrySaysDirectory = registry.FolderNames.Contains(name) ? true : null,
            });
        }

        unresolved = failures;
        return results;
    }

    /// <summary>
    /// Order by MRU rank when known (authoritative), then by .lnk mtime, then by
    /// path. The final tiebreaker matters: several files opened within the same
    /// second would otherwise reshuffle on every refresh and make the list flicker.
    /// </summary>
    private static List<RecentItem> Order(List<RecentItem> items, RecentScannerOptions options)
    {
        return items
            .OrderBy(i => i.MruRank ?? int.MaxValue)
            .ThenByDescending(i => i.LastAccessTime)
            .ThenBy(i => i.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Take(options.MaxItems)
            .ToList();
    }

    /// <summary>
    /// Decide whether the target is a directory and whether it still exists.
    ///
    /// UNC paths and unmounted drive letters are reported Unreachable WITHOUT
    /// touching the filesystem, because probing them blocks for seconds when the
    /// share or drive is offline.
    /// </summary>
    private static (bool IsDirectory, ItemValidity Validity) ProbeTarget(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (false, ItemValidity.Unreachable);
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
            {
                return (false, ItemValidity.Unreachable);
            }

            if (Directory.Exists(path))
            {
                return (true, ItemValidity.Valid);
            }

            return File.Exists(path)
                ? (false, ItemValidity.Valid)
                : (false, ItemValidity.Missing);
        }
        catch (Exception)
        {
            return (false, ItemValidity.Unreachable);
        }
    }

    private sealed record RegistrySource(
        Dictionary<string, int> RankByName,
        HashSet<string> FolderNames,
        int RecordCount);

    /// <summary>Read the RecentDocs key once, building both mappings.</summary>
    private static RegistrySource ReadRegistry()
    {
        var rankByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var folderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recordCount = 0;

        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(RecentDocsKeyPath, writable: false);
            if (root is null)
            {
                return new RegistrySource(rankByName, folderNames, 0);
            }

            // Each numbered value's payload carries the file name; MRUListEx holds
            // only value NAMES, so both are needed to go from name to rank.
            var nameByValueName = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string valueName in root.GetValueNames())
            {
                if (valueName == MruListExValueName)
                {
                    continue;
                }

                if (root.GetValue(valueName) is byte[] raw)
                {
                    RecentDocRecord? record = RecentDocsRecordParser.Parse(raw);
                    if (record is not null)
                    {
                        nameByValueName[valueName] = record.Name;
                        recordCount++;
                    }
                }
            }

            if (root.GetValue(MruListExValueName) is byte[] mru)
            {
                IReadOnlyList<int> chain = MruListEx.Parse(mru);
                for (int i = 0; i < chain.Count; i++)
                {
                    if (nameByValueName.TryGetValue(chain[i].ToString(), out string? name))
                    {
                        rankByName[name] = i + 1; // rank 1 = most recent
                    }
                }
            }

            using RegistryKey? folderBucket = root.OpenSubKey(FolderBucketName, writable: false);
            if (folderBucket is not null)
            {
                foreach (string valueName in folderBucket.GetValueNames())
                {
                    if (valueName == MruListExValueName)
                    {
                        continue;
                    }

                    if (folderBucket.GetValue(valueName) is byte[] raw)
                    {
                        RecentDocRecord? record = RecentDocsRecordParser.Parse(raw);
                        if (record is not null)
                        {
                            folderNames.Add(record.Name);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Never let a registry problem break the scan; ordering just falls back
            // to .lnk timestamps and folder classification to the filesystem probe.
        }

        return new RegistrySource(rankByName, folderNames, recordCount);
    }
}

/// <summary>
/// Deleting entries from the Recent folder.
///
/// This writes to the user's profile, so it is deliberately explicit and narrow: it
/// deletes exactly the .lnk files whose targets match, never the registry key, and
/// never anything outside the Recent directory. The target file itself is never
/// touched, so the effect is the same as Explorer's own "remove from list".
/// </summary>
public static class RecentEntryCleaner
{
    /// <summary>
    /// Remove the record(s) pointing at <paramref name="targetPath"/>. Returns the
    /// number of .lnk files deleted.
    /// </summary>
    /// <remarks>
    /// Resolving links needs COM on an STA thread; the WPF UI thread qualifies.
    /// </remarks>
    public static int RemoveByTargetPath(string targetPath, RecentScannerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        options ??= RecentScannerOptions.Default;

        string recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentDirectory))
        {
            return 0;
        }

        int removed = 0;

        foreach (string linkPath in Directory.GetFiles(recentDirectory, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            if (options.ExcludedLinkFileNames.Contains(Path.GetFileName(linkPath)))
            {
                continue;
            }

            string? target = LinkResolver.TryGetTargetPath(linkPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(target);
            if (!string.Equals(expanded, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryDelete(linkPath))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Remove records whose target no longer exists. Returns how many were deleted.
    /// </summary>
    public static int RemoveMissingTargets(RecentScannerOptions? options = null)
    {
        options ??= RecentScannerOptions.Default;

        string recentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(recentDirectory))
        {
            return 0;
        }

        int removed = 0;

        foreach (string linkPath in Directory.GetFiles(recentDirectory, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            if (options.ExcludedLinkFileNames.Contains(Path.GetFileName(linkPath)))
            {
                continue;
            }

            string? target = LinkResolver.TryGetTargetPath(linkPath);
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            string expanded = Environment.ExpandEnvironmentVariables(target);

            // Only delete when certain the target is gone. An unreachable network
            // path must NOT count as missing, or the sweep would silently discard
            // valid records for a share that merely happens to be offline.
            bool exists;
            try
            {
                exists = File.Exists(expanded) || Directory.Exists(expanded);
            }
            catch (Exception)
            {
                continue;
            }

            if (!exists && TryDelete(linkPath))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or protected record: skip it rather than aborting the sweep.
            return false;
        }
    }
}

/// <summary>Tunables for a scan.</summary>
public sealed class RecentScannerOptions
{
    public static RecentScannerOptions Default { get; } = new();

    /// <summary>Maximum items returned.</summary>
    public int MaxItems { get; init; } = 50;

    /// <summary>
    /// Include records whose target no longer exists. Off by default: they are
    /// shown in a separate collapsed group by the UI once that lands.
    /// </summary>
    public bool IncludeMissingTargets { get; init; }

    /// <summary>
    /// .lnk files to ignore.
    ///
    /// The shell writes entries that are not documents and have no target path.
    /// Measured on a live profile: "Internet.lnk" is a virtual network location
    /// (classified under the registry's Folder bucket) and
    /// "ms-photosspareprocess-viewer.lnk" is a UWP application id. Both make
    /// IShellLink.GetPath return nothing, so they can only ever appear as broken
    /// rows. Excluding them keeps first-run output clean; a future version can
    /// surface them if a use case appears.
    /// </summary>
    public IReadOnlySet<string> ExcludedLinkFileNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Internet.lnk",
            "ms-photosspareprocess-viewer.lnk",
        };
}
