namespace ChappyStorageCompressor.Core;

/// <summary>
/// Read-only, hypothetical estimate combining separately verified JPG/PNG
/// preview savings with fully revalidated identical-file groups. Does not
/// mutate originals or measure physical free space.
/// </summary>
public static class CombinedSavingsPlanner
{
    public sealed record FormatEstimate(string Format, int VerifiedDuplicateGroups,
        int SkippedDuplicateGroups, long PreviewOnlyBytes, long DedupeOnlyBytes,
        long DoubleCountAvoidedBytes, long CombinedHypotheticalBytes);

    public sealed record Estimate(long PreviewOnlyBytes, long DedupeOnlyBytes,
        long CombinedHypotheticalBytes, long DoubleCountAvoidedBytes,
        int VerifiedDuplicateGroups, int SkippedDuplicateGroups,
        long ActualFreedBytes)
    {
        public IReadOnlyList<FormatEstimate> Formats { get; init; } = [];
    }

    private static readonly string[] Order = ["MP4", "JPG", "PNG", "混合拡張子", "その他"];

    private sealed class Totals
    {
        public int Verified;
        public int Skipped;
        public long Preview;
        public long Dedupe;
        public long Overlap;
    }

    public static Estimate Analyze(CombinedCapacityReport.Summary preview,
        ScanResult scan, bool allowTimestampDifference, CancellationToken ct = default,
        IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(scan);
        string root = Safety.ValidateRoot(scan.Root);
        var totals = Order.ToDictionary(x => x, _ => new Totals(), StringComparer.Ordinal);
        var savingsByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in preview.Rows)
        {
            ct.ThrowIfCancellationRequested();
            // Reports are expected to have been authenticated by
            // CombinedCapacityReport.Inspect. Still reject malformed or
            // inconsistent inputs if called directly by another code path.
            if (!ValidPreviewRow(r) || Path.IsPathRooted(r.RelativePath)) continue;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, r.RelativePath)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { continue; }
            if (!Inside(full, root) || full.Equals(root, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!SafePreviewSource(full, r.SourceBytes)) continue;
            if (savingsByPath.TryGetValue(full, out long previous))
                savingsByPath[full] = Math.Max(previous, r.EstimatedSavingBytes);
            else savingsByPath.Add(full, r.EstimatedSavingBytes);
        }

        long previewOnly = 0;
        foreach (var item in savingsByPath)
        {
            long bytes = item.Value;
            previewOnly = checked(previewOnly + bytes);
            totals[PathFormat(item.Key)].Preview =
                checked(totals[PathFormat(item.Key)].Preview + bytes);
        }

        long dedupeOnly = 0, overlap = 0;
        int verified = 0, skipped = 0, processed = 0;
        var globallySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in scan.Groups)
        {
            ct.ThrowIfCancellationRequested();
            string type = GroupFormat(group);
            var bucket = totals[type];
            bool safe = group.Files.Count >= 2 &&
                group.Hash.Length == 64 && group.Hash.All(Uri.IsHexDigit) &&
                group.BlockReason(allowTimestampDifference) is null;
            var paths = new List<(FileSnapshot Snapshot, string Full)>(group.Files.Count);
            var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (safe)
            {
                foreach (var file in group.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    string full;
                    try { full = Path.GetFullPath(file.Path); }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                    { safe = false; break; }
                    if (!Inside(full, root) || !localNames.Add(full) ||
                        globallySeen.Contains(full))
                    { safe = false; break; }
                    try
                    {
                        if (!Safety.SameSnapshot(file) ||
                            NativeFile.GetIdentity(full).LinkCount != 1 ||
                            !string.Equals(Safety.ComputeSha256(full, ct), group.Hash,
                                StringComparison.OrdinalIgnoreCase) ||
                            (paths.Count > 0 && !Safety.ByteEqual(full, paths[0].Full, ct)) ||
                            !Safety.SameSnapshot(file))
                        { safe = false; break; }
                        paths.Add((file, full));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or InvalidOperationException or System.ComponentModel.Win32Exception)
                    { safe = false; break; }
                }
            }
            if (safe)
            {
                long bytes = checked(group.Representative.Length * (paths.Count - 1L));
                long sum = 0, max = 0;
                foreach (var p in paths)
                {
                    if (savingsByPath.TryGetValue(p.Full, out long saving))
                    {
                        sum = checked(sum + saving);
                        max = Math.Max(max, saving);
                    }
                }
                long duplicatePreviewSavings = checked(sum - max);
                dedupeOnly = checked(dedupeOnly + bytes);
                overlap = checked(overlap + duplicatePreviewSavings);
                bucket.Dedupe = checked(bucket.Dedupe + bytes);
                bucket.Overlap = checked(bucket.Overlap + duplicatePreviewSavings);
                bucket.Verified++;
                verified++;
                foreach (var p in paths) globallySeen.Add(p.Full);
            }
            else
            {
                skipped++;
                bucket.Skipped++;
            }
            progress?.Report((++processed, scan.Groups.Count));
        }

        long combined = checked(checked(previewOnly + dedupeOnly) - overlap);
        var formatRows = Order.Select(name =>
        {
            var b = totals[name];
            return new FormatEstimate(name, b.Verified, b.Skipped, b.Preview,
                b.Dedupe, b.Overlap,
                checked(checked(b.Preview + b.Dedupe) - b.Overlap));
        }).ToList();
        // Mixed-extension groups can overlap with JPG/PNG preview rows. The
        // per-format net may be negative in that mixed bucket; only the TOTAL
        // is a meaningful capacity scenario, not each bucket as a promise.
        if (formatRows.Sum(x => x.PreviewOnlyBytes) != previewOnly ||
            formatRows.Sum(x => x.DedupeOnlyBytes) != dedupeOnly ||
            formatRows.Sum(x => x.DoubleCountAvoidedBytes) != overlap ||
            formatRows.Sum(x => x.CombinedHypotheticalBytes) != combined)
            throw new InvalidDataException("集計の合計が一致しません。安全のため停止します。");
        return new Estimate(previewOnly, dedupeOnly, combined, overlap,
            verified, skipped, 0) { Formats = formatRows };
    }

    public static void ExportFormatCsv(Estimate estimate, string scanRoot,
        string previewRoot, string csvFile) =>
        CombinedSavingsFormatCsv.Export(estimate, scanRoot, previewRoot, csvFile);

    private static bool ValidPreviewRow(CombinedCapacityReport.Row r)
    {
        if (r.SourceBytes <= 0 || r.CandidateBytes < 0 ||
            r.CandidateBytes >= r.SourceBytes || r.EstimatedSavingBytes <= 0 ||
            r.EstimatedSavingBytes != r.SourceBytes - r.CandidateBytes ||
            string.IsNullOrWhiteSpace(r.RelativePath)) return false;
        string format = PathFormat(r.RelativePath);
        return (format == "JPG" && r.Format == "JPG" && r.State == "Completed") ||
            (format == "PNG" && r.Format == "PNG" && r.State == "CandidatePresent");
    }

    private static bool SafePreviewSource(string path, long bytes)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists && info.Length == bytes &&
                Safety.EligibleAttributes(info.Attributes) &&
                NativeFile.GetIdentity(path).LinkCount == 1 &&
                !NativeFile.HasNamedDataStreams(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static string GroupFormat(DuplicateGroup group)
    {
        var kinds = group.Files.Select(f => PathFormat(f.Path)).Distinct().ToArray();
        return kinds.Length == 1 ? kinds[0] : "混合拡張子";
    }

    private static string PathFormat(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "MP4",
            ".jpg" or ".jpeg" => "JPG",
            ".png" => "PNG",
            _ => "その他"
        };

    private static bool Inside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
