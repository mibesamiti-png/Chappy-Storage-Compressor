namespace ChappyStorageCompressor.Core;

/// <summary>
/// Read-only, deliberately hypothetical capacity estimate when file-level
/// deduplication and lossless previews cover the same input paths.  No files
/// are modified. All figures are logical byte estimates, NOT reclaimed blocks.
/// The caller must use a freshly verified duplicate scan.
/// </summary>
public static class CombinedSavingsPlanner
{
    public sealed record Estimate(long PreviewOnlyBytes, long DedupeOnlyBytes,
        long CombinedHypotheticalBytes, long DoubleCountAvoidedBytes,
        int VerifiedDuplicateGroups, int SkippedDuplicateGroups,
        long ActualFreedBytes);

    public static Estimate Analyze(CombinedCapacityReport.Summary preview,
        ScanResult scan, bool allowTimestampDifference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(scan);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.Root));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var savingsByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in preview.Rows)
        {
            ct.ThrowIfCancellationRequested();
            // Rows originate from the read-only validated batch report. Reject
            // malformed relative paths rather than letting them escape root.
            if (r.EstimatedSavingBytes <= 0 || r.SourceBytes <= 0 ||
                Path.IsPathRooted(r.RelativePath)) continue;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, r.RelativePath)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
            if (!Inside(full, root) || full.Equals(root, StringComparison.OrdinalIgnoreCase)) continue;
            // A disappearing or reparse-point file must not abort analysis or
            // inflate the estimate via another target.
            if (!MatchesLength(full, r.SourceBytes)) continue;
            if (r.EstimatedSavingBytes > r.SourceBytes) continue;
            if (!savingsByPath.TryGetValue(full, out long old) || old < r.EstimatedSavingBytes)
                savingsByPath[full] = r.EstimatedSavingBytes;
        }

        long previewOnly = 0;
        foreach (long amount in savingsByPath.Values) previewOnly = checked(previewOnly + amount);
        long dedupeOnly = 0, overlap = 0;
        int verified = 0, skipped = 0;
        var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in scan.Groups)
        {
            ct.ThrowIfCancellationRequested();
            if (group.Files.Count < 2 || group.BlockReason(allowTimestampDifference) != null)
            { skipped++; continue; }
            var paths = new List<string>();
            bool safe = true;
            foreach (var file in group.Files)
            {
                string full = Path.GetFullPath(file.Path);
                if (!Inside(full, root) || !counted.Add(full)) { safe = false; break; }
                paths.Add(full);
                // Refresh file metadata and content, not just the prior scan's
                // hash. A changed file disables this group's estimate.
                try
                {
                    if (!Safety.SameSnapshot(file) ||
                        !string.Equals(Safety.ComputeSha256(full, ct), group.Hash,
                            StringComparison.OrdinalIgnoreCase)) { safe = false; break; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { safe = false; break; }
            }
            if (!safe)
            {
                // Release tentative path reservations on an aborted group.
                foreach (var path in paths) counted.Remove(path);
                skipped++;
                continue;
            }
            verified++;
            dedupeOnly = checked(dedupeOnly + group.Savings);
            long sum = 0, max = 0;
            foreach (var file in group.Files)
            {
                string path = Path.GetFullPath(file.Path);
                if (savingsByPath.TryGetValue(path, out long saving) &&
                    MatchesLength(path, file.Length))
                {
                    sum = checked(sum + saving);
                    max = Math.Max(max, saving);
                }
            }
            // After deduplication a shared byte stream could be optimized at
            // most once. This does NOT implement such an operation.
            overlap = checked(overlap + (sum - max));
        }
        return new Estimate(previewOnly, dedupeOnly,
            checked(checked(previewOnly + dedupeOnly) - overlap),
            overlap, verified, skipped, 0);
    }

    private static bool MatchesLength(string path, long expected)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                return false;
            return new FileInfo(path).Length == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return false; }
    }

    private static bool Inside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
