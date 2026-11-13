using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.13: read-only format breakdown for previously scanned exact duplicates.
/// Every candidate is revalidated from disk. There are no file mutations and
/// the figures are logical estimates, not actual recovered free space.
/// </summary>
public static class MediaDuplicateReport
{
    public sealed record Row(int GroupNumber, string Format, string State, string Reason,
        string RelativePath, long FileBytes, long GroupPotentialBytes, string Sha256);

    public sealed record FormatSummary(string Format, int EligibleGroups,
        int EligibleFiles, long PotentialBytes);

    public sealed record Summary(int InspectedGroups, int EligibleGroups,
        int BlockedGroups, int ChangedGroups, long PotentialBytes,
        long ActualFreedBytes, IReadOnlyList<FormatSummary> Formats,
        IReadOnlyList<Row> Rows);

    private static readonly string[] FormatOrder =
        ["MP4", "JPG", "PNG", "混合拡張子", "その他"];

    public static Summary Analyze(ScanResult scan, bool allowTimestampDifference,
        CancellationToken ct = default, IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        string root = Safety.ValidateRoot(scan.Root);
        var rows = new List<Row>();
        var aggregates = FormatOrder.ToDictionary(x => x,
            _ => (Groups: 0, Files: 0, Bytes: 0L), StringComparer.Ordinal);
        int eligible = 0, blocked = 0, changed = 0, ordinal = 0;
        long potential = 0;

        foreach (DuplicateGroup group in scan.Groups)
        {
            ct.ThrowIfCancellationRequested();
            int number = ++ordinal;
            string format = GroupFormat(group);
            string? reason = group.BlockReason(allowTimestampDifference);
            bool stale = false;
            if (reason is null)
            {
                if (group.Files.Count < 2 || group.Hash.Length != 64 ||
                    !group.Hash.All(Uri.IsHexDigit))
                    stale = true;
                else
                {
                    // Scans can be minutes or days old. Rehash every candidate
                    // and ensure byte-for-byte equality, not only file lengths.
                    var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    FileSnapshot representative = group.Representative;
                    foreach (FileSnapshot file in group.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        string full;
                        try { full = Path.GetFullPath(file.Path); }
                        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                        { stale = true; break; }

                        if (!Inside(full, root) || !verified.Add(full))
                        { stale = true; break; }

                        try
                        {
                            if (!Safety.SameSnapshot(file) ||
                                NativeFile.GetIdentity(full).LinkCount != 1 ||
                                !string.Equals(Safety.ComputeSha256(full, ct),
                                    group.Hash, StringComparison.OrdinalIgnoreCase) ||
                                (!string.Equals(full, representative.Path,
                                    StringComparison.OrdinalIgnoreCase) &&
                                 !Safety.ByteEqual(full, representative.Path, ct)))
                            { stale = true; break; }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { stale = true; break; }
                    }
                }
            }
            if (stale) reason = "スキャン後の内容・状態変更、または候補の再照合失敗";
            string status = reason is null ? "候補" : stale ? "再スキャン必要" : "対象外";
            long saving = 0;
            if (reason is null)
            {
                saving = checked(group.Representative.Length * (group.Files.Count - 1L));
                potential = checked(potential + saving);
                var old = aggregates[format];
                aggregates[format] = (old.Groups + 1, old.Files + group.Files.Count,
                    checked(old.Bytes + saving));
                eligible++;
            }
            else if (stale) changed++;
            else blocked++;

            // One group estimate appears once, preventing users adding CSV
            // rows and accidentally counting the same group several times.
            foreach (var file in group.Files.Select((snapshot, i) => (snapshot, i)))
            {
                ct.ThrowIfCancellationRequested();
                string relative;
                try
                {
                    string full = Path.GetFullPath(file.snapshot.Path);
                    relative = Inside(full, root) ? Path.GetRelativePath(root, full) : "（対象外パス）";
                }
                catch { relative = "（無効なパス）"; }
                rows.Add(new Row(number, format, status, reason ?? "",
                    relative, file.snapshot.Length, file.i == 0 ? saving : 0, group.Hash));
            }
            progress?.Report((number, scan.Groups.Count));
        }

        return new Summary(scan.Groups.Count, eligible, blocked, changed, potential, 0,
            FormatOrder.Select(f => new FormatSummary(f, aggregates[f].Groups,
                aggregates[f].Files, aggregates[f].Bytes)).ToList(), rows);
    }

    public static void ExportCsv(Summary report, string scanRoot, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(report);
        string root = Safety.ValidateRoot(scanRoot);
        string dest = Path.GetFullPath(outputPath);
        if (!string.Equals(Path.GetExtension(dest), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CSV形式で保存してください。");
        string folder = Path.GetDirectoryName(dest)
            ?? throw new InvalidOperationException("CSVの保存先が不正です。");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (Inside(dest, root))
            throw new InvalidOperationException("CSVはスキャン対象フォルダの外へ保存してください。");
        for (string? ancestor = folder; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
        {
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("リンク・ジャンクション内への保存は拒否します。");
        }
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException("既存ファイルは上書きしません。別名を選択してください。");

        string temporary = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.WriteLine("Group,Format,Status,Reason,RelativePath,FileBytes,GroupEstimatedSavingBytes,Sha256");
                foreach (var row in report.Rows)
                {
                    var values = new[] { row.GroupNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        row.Format, row.State, row.Reason, row.RelativePath,
                        row.FileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        row.GroupPotentialBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        row.Sha256 };
                    writer.WriteLine(string.Join(",", values.Select(CsvField)));
                }
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, dest);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch { /* never touch an existing target on cleanup failure */ }
            }
        }
    }

    private static string GroupFormat(DuplicateGroup group)
    {
        string[] types = group.Files.Select(f => ExtensionFormat(f.Path)).Distinct().ToArray();
        return types.Length == 1 ? types[0] : "混合拡張子";
    }

    private static string ExtensionFormat(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "JPG",
            ".png" => "PNG",
            ".mp4" => "MP4",
            _ => "その他"
        };

    private static string CsvField(string? input)
    {
        string value = input ?? "";
        // Spreadsheet formula injection is possible even from filenames.
        string trimmed = value.TrimStart(' ', '\t', '\r', '\n');
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static bool Inside(string full, string root) =>
        full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        full.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
