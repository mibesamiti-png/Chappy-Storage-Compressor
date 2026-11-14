using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Export only a generated capacity summary to a new CSV outside input and
/// candidate trees. This module never touches or mutates media originals.
/// Kept separate from the read-only planner's no-file-mutation contract.
/// </summary>
public static class CombinedSavingsFormatCsv
{
    public static void Export(CombinedSavingsPlanner.Estimate estimate, string scanRoot,
        string previewRoot, string csvFile)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        string source = Safety.ValidateRoot(scanRoot);
        string candidates = JpegBatchPreviewEngine.CanonicalFolder(previewRoot);
        JpegBatchPreviewEngine.ValidateFolders(source, candidates);
        string dest = Path.GetFullPath(csvFile);
        if (!Path.GetExtension(dest).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CSV形式を選択してください。");
        string folder = Path.GetDirectoryName(dest) ??
            throw new InvalidOperationException("CSVの保存先がありません。");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        JpegBatchPreviewEngine.CheckNoReparse(folder);
        if (Inside(dest, source) || Inside(dest, candidates))
            throw new InvalidOperationException("CSVは原本・候補フォルダの外へ保存してください。");
        if (File.Exists(dest) || Directory.Exists(dest))
            throw new IOException("既存ファイルを上書きしません。別名を指定してください。");
        if (estimate.Formats.Count != ExpectedOrder.Length ||
            !ExpectedOrder.SequenceEqual(estimate.Formats.Select(f => f.Format)) ||
            estimate.Formats.Sum(f => f.CombinedHypotheticalBytes) !=
                estimate.CombinedHypotheticalBytes)
            throw new InvalidDataException("形式別集計が不整合です。CSVは保存しません。");

        string temporary = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.WriteLine("Format,VerifiedDuplicateGroups,SkippedDuplicateGroups,PreviewOnlyBytes,DedupeOnlyBytes,OverlapAvoidedBytes,CombinedHypotheticalBytes,ActualFreedBytes");
                foreach (var row in estimate.Formats)
                    writer.WriteLine(string.Join(",", new[] {
                        Csv(row.Format), Csv(Num(row.VerifiedDuplicateGroups)),
                        Csv(Num(row.SkippedDuplicateGroups)), Csv(Num(row.PreviewOnlyBytes)),
                        Csv(Num(row.DedupeOnlyBytes)), Csv(Num(row.DoubleCountAvoidedBytes)),
                        Csv(Num(row.CombinedHypotheticalBytes)), Csv("0") }));
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
                catch { /* no existing file is touched on failed export */ }
            }
        }
    }

    private static readonly string[] ExpectedOrder = ["MP4", "JPG", "PNG", "混合拡張子", "その他"];
    private static string Num(long value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Csv(string s)
    {
        string stripped = s.TrimStart(' ', '\t', '\n', '\r');
        if (stripped.Length > 0 && stripped[0] is '=' or '+' or '-' or '@')
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    private static bool Inside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
