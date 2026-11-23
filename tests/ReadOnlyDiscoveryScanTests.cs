using System.IO;
using System.Security.Cryptography;
using ChappyStorageCompressor.Core;

/// <summary>
/// Production search-only Core exercised with disposable NTFS fixture.
/// No exFAT drive in hosted CI: this proves common no-write scanner logic,
/// NOT physical exFAT controller/device acceptance.
/// </summary>
internal static class ReadOnlyDiscoveryScanTests
{
    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        var root = Path.Combine(parent, "search-only-volume");
        var keep = Path.Combine(root, "Keep");
        var copies = Path.Combine(root, "Copies");
        var managed = Path.Combine(root, "System Volume Information");
        var store = Path.Combine(root, Safety.StoreName);
        foreach (var folder in new[] { keep, copies, managed, store })
            Directory.CreateDirectory(folder);

        var first = Path.Combine(keep, "a.jpg");
        var second = Path.Combine(copies, "a-copy.bin");
        var readonlyOriginal = Path.Combine(keep, "readonly.mp4");
        var readonlyCopy = Path.Combine(copies, "readonly.mp4");
        byte[] data = new byte[128 * 1024];
        new Random(77301).NextBytes(data);
        File.WriteAllBytes(first, data);
        File.Copy(first, second);
        File.Copy(first, Path.Combine(managed, "must-not-scan.jpg"));
        File.Copy(first, Path.Combine(store, "must-not-scan.jpg"));
        byte[] mediaBytes = new byte[64 * 1024];
        new Random(77302).NextBytes(mediaBytes);
        File.WriteAllBytes(readonlyOriginal, mediaBytes);
        File.Copy(readonlyOriginal, readonlyCopy);
        File.SetAttributes(readonlyCopy, FileAttributes.ReadOnly);
        var nearMatch = Path.Combine(copies, "near-match.bin");
        var nearOther = Path.Combine(keep, "near-other.bin");
        File.WriteAllBytes(nearMatch, data);
        data[^1] ^= 0x1F;
        File.WriteAllBytes(nearOther, data);
        File.SetAttributes(nearMatch, FileAttributes.Hidden);
        var baseline = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(x => x, Hash, StringComparer.OrdinalIgnoreCase);
        try
        {
            var target = Safety.ResolveScanTarget(root, forceDiscovery: true);
            check(target.DiscoveryOnly && target.Format == "NTFS",
                "Search-only explicit mode uses the same no-write path on a disposable NTFS fixture");
            var strict = Safety.ResolveScanTarget(root);
            check(!strict.DiscoveryOnly && strict.Root == root &&
                  Safety.ValidateRoot(root) == root,
                "Existing NTFS nested-folder admission still uses strict mode");
            var driveRoot = Path.GetPathRoot(root)!;
            var whole = Safety.ResolveScanTarget(driveRoot);
            check(whole.DiscoveryOnly && whole.Format == "NTFS" &&
                  whole.Root == Path.TrimEndingDirectorySeparator(driveRoot),
                "Whole NTFS drive root is admitted ONLY for discovery; never scanned in CI");
            expectThrows(() => Safety.ValidateRoot(driveRoot),
                "Original NTFS mutation validation still forbids entire drive root");
            expectThrows(() => Safety.ResolveScanTarget(managed, forceDiscovery: true),
                "Direct System Volume Information selection is forbidden even for discovery");

            var result = ReadOnlyDiscoveryScanner.Run(root, false, null, CancellationToken.None);
            check(result.DiscoveryOnly && result.VolumeFormat == "NTFS" &&
                  result.Groups.Count == 2 && result.Groups.All(g => g.Files.Count == 2),
                "Read-only discovery finds both byte-identical groups despite filenames and metadata");
            check(result.Groups.All(g => g.SearchOnly &&
                  g.Files.All(f => f.SearchOnly && f.Identity.LinkCount == 0 &&
                     f.SecurityDescriptor.Length == 0) &&
                  g.BlockReason(false) is not null &&
                  g.PartitionForSafeMerge(true).Count == 1),
                "Returned groups carry NO NTFS ID/ACL/ADS evidence or merge eligibility");
            check(result.Groups.SelectMany(g => g.Files)
                    .All(f => !f.Path.Contains("System Volume Information",
                                   StringComparison.OrdinalIgnoreCase) &&
                              !f.Path.Contains(Safety.StoreName, StringComparison.OrdinalIgnoreCase)) &&
                  result.FileCount == 6,
                "System/managed folders excluded and two same-size near matches not falsely grouped");
            check(result.Groups.Sum(g => g.Savings) == 192 * 1024,
                "Display estimates only logical duplicate bytes, never recovered physical bytes");
            var mediaOnly = ReadOnlyDiscoveryScanner.Run(root, true, null,
                CancellationToken.None);
            check(mediaOnly.Groups.Count == 1 &&
                  mediaOnly.Groups[0].Files.All(f => f.Path.EndsWith(".mp4",
                      StringComparison.OrdinalIgnoreCase)),
                "Media-only choice still filters non-media names without mutation");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            expectThrows(() => ReadOnlyDiscoveryScanner.Run(root, false,
                null, canceled.Token), "Cancellation does not return an incomplete candidate list");
            check(baseline.Count == Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count() &&
                  baseline.All(kv => Hash(kv.Key) == kv.Value) &&
                  !Directory.Exists(Safety.StorePath(Path.Combine(root, "Keep"))),
                "Discovery did not create storage/journals, alter files, or rename anything");
        }
        finally
        {
            // Generated fixture only; make the single synthetic read-only
            // file removable by the caller's disposable-root teardown.
            File.SetAttributes(readonlyCopy, FileAttributes.Normal);
        }
    }
}
