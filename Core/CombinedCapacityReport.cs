using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.0: read-only report of PNG and JPEG preview plans.
/// Do not add these estimates to dedupe estimates: identical files can overlap.
/// Actual free disk space gained is always 0 until a separately verified
/// mutation feature is implemented and measured.
/// </summary>
public static class CombinedCapacityReport
{
    public sealed record Row(string Format, string RelativePath, string State,
        long SourceBytes, long CandidateBytes, long EstimatedSavingBytes, string Note);
    public sealed record Summary(int PngFiles, int JpegFiles, int CandidateCount,
        int NeedsReview, long EstimatedSavingBytes, long ActualFreedBytes,
        IReadOnlyList<Row> Rows);

    public static Summary Inspect(string inputRoot, string outputRoot, CancellationToken ct = default)
    {
        string input = JpegBatchPreviewEngine.CanonicalFolder(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        JpegBatchPreviewEngine.ValidateFolders(input, output);
        JpegBatchPreviewEngine.ValidateWorkingPaths(output,
            Path.Combine(output, JpegBatchPreviewEngine.PreviewName));
        string pngPlan = Path.Combine(output, ".chappy-png-batch-r16.json");
        string jpegPlan = Path.Combine(output, JpegBatchPreviewEngine.PlanName);
        bool pngExists = File.Exists(pngPlan), jpgExists = File.Exists(jpegPlan);
        if (!pngExists && !jpgExists)
            throw new FileNotFoundException("選んだフォルダにPNG/JPG一括候補の計画がありません。");
        var rows = new List<Row>();
        int pngCount = 0, jpegCount = 0, count = 0, review = 0;
        long saving = 0;
        if (pngExists)
        {
            var png = PngBatchJournalTools.ReadAudit(input, output, ct);
            pngCount = png.Total;
            foreach (var r in png.Rows)
            {
                ct.ThrowIfCancellationRequested();
                bool accepted = r.State == "CandidatePresent" && r.EstimatedSavingBytes > 0;
                if (accepted) count++;
                if (r.State == "NeedsReview") review++;
                long delta = accepted ? r.EstimatedSavingBytes : 0;
                saving = checked(saving + delta);
                rows.Add(new Row("PNG", r.RelativePath, r.State,
                    r.SourceBytes, r.PreviewBytes, delta, r.Note));
            }
        }
        if (jpgExists)
        {
            // The tool path is read from the saved plan solely to authenticate
            // the plan identity. No executable is launched by this report.
            JpegBatchPreviewEngine.CheckNoReparse(jpegPlan);
            var plan = JpegBatchPreviewEngine.LoadPlan(jpegPlan);
            var jpeg = JpegBatchPreviewEngine.Inspect(input, output, plan.ToolPath, ct);
            jpegCount = jpeg.Rows.Count;
            foreach (var r in jpeg.Rows)
            {
                ct.ThrowIfCancellationRequested();
                bool accepted = r.Status == "Completed" && r.VerifiedSavingBytes > 0;
                if (accepted) count++;
                if (r.Status == "NeedsReview") review++;
                long delta = accepted ? r.VerifiedSavingBytes : 0;
                saving = checked(saving + delta);
                rows.Add(new Row("JPG", r.RelativePath, r.Status,
                    r.SourceBytes, r.CandidateBytes, delta, r.Note));
            }
        }
        rows.Sort((a, b) =>
        {
            int size = b.EstimatedSavingBytes.CompareTo(a.EstimatedSavingBytes);
            return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        });
        // No mutation is performed by any preview feature in R2.0.
        return new Summary(pngCount, jpegCount, count, review, saving, 0, rows);
    }

    public static void ExportCsv(Summary report, string inputRoot, string outputRoot, string csvFile)
    {
        string input = JpegBatchPreviewEngine.CanonicalFolder(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        JpegBatchPreviewEngine.ValidateFolders(input, output);
        string path = Path.GetFullPath(csvFile);
        string directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException("保存先フォルダを指定してください。");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        JpegBatchPreviewEngine.CheckNoReparse(directory);
        if (IsInside(path, input) || IsInside(path, output))
            throw new InvalidOperationException("CSVを原本・候補フォルダ内に保存できません。");
        if (File.Exists(path)) throw new IOException("同名CSVは上書きしません。別名を指定してください。");
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.WriteLine("Format,RelativePath,Status,SourceBytes,CandidateBytes,EstimatedSavingBytes,ActualFreedBytes,Note");
                foreach (var r in report.Rows)
                {
                    var values = new[] { r.Format, r.RelativePath, r.State,
                        Num(r.SourceBytes), Num(r.CandidateBytes), Num(r.EstimatedSavingBytes),
                        "0", r.Note };
                    writer.WriteLine(string.Join(",", values.Select(Csv)));
                }
                writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch { } } }
    }

    private static string Num(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Csv(string value)
    {
        value ??= "";
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private static bool IsInside(string path, string root)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
