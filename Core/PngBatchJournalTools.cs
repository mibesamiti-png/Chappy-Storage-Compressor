using System.Text;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R1.7: audit, export and explicit tail repair for R1.6 PNG PREVIEW journaling.
/// Never writes into the source directory or modifies input media.
/// Recovery retains the entire previous journal as a separate backup.
/// </summary>
public static class PngBatchJournalTools
{
    private const string PlanFile = ".chappy-png-batch-r16.json";
    private const string LogFile = ".chappy-png-batch-r16.progress.jsonl";
    private const string LockFile = ".chappy-png-batch-r16.lock";
    private const string PreviewDirectory = "chappy-png-previews";
    private const int MaximumLogBytes = 128 * 1024 * 1024;

    public sealed record TailResult(string State, int TailBytes, string Message);
    public sealed record Row(string RelativePath, string State, long SourceBytes, long PreviewBytes,
        long EstimatedSavingBytes, string Note, string PreviewPath);
    public sealed record Audit(int Total, int Completed, int Pending, int Skipped, int Failed,
        int Deferred, long EstimatedSavingBytes, TailResult Tail, IReadOnlyList<Row> Rows);
    public sealed record RepairResult(string BackupPath, int DiscardedTailBytes, bool AddedNewline);

    public static TailResult InspectTail(string inputRoot, string outputRoot)
    {
        using var state = Open(inputRoot, outputRoot);
        return state.Tail;
    }

    public static RepairResult RepairTail(string inputRoot, string outputRoot)
    {
        using var state = Open(inputRoot, outputRoot);
        if (state.Tail.State is not ("PartialTail" or "MissingNewline"))
            throw new InvalidOperationException("修復可能な末尾はありません。正常記録や中間破損は変更しません。");
        if (!File.Exists(state.LogPath)) throw new InvalidDataException("ログがありません。");

        // Entire original journal is preserved. If there is not enough space
        // for both original and temporary copies the operation fails closed.
        string backup = state.LogPath + ".before-r17-" + Guid.NewGuid().ToString("N") + ".bak";
        string tmp = state.LogPath + ".repair-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(state.LogPath, backup, overwrite: false);
            long usable = state.CompleteBytes;
            using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                output.Write(state.Bytes.AsSpan(0, checked((int)usable)));
                if (state.Tail.State == "MissingNewline")
                    output.WriteByte((byte)'\n');
                output.Flush(flushToDisk: true);
            }
            byte[] newBytes = File.ReadAllBytes(tmp);
            var plan = ReadPlan(state.Input, state.Output);
            var verify = Examine(newBytes, plan, state.Output);
            if (verify.Tail.State is not ("Healthy" or "Empty"))
                throw new InvalidDataException("新しい作業記録の整合性検査に失敗しました。");
            // Same-directory replacement. A crash before this point leaves the
            // original journal untouched; backup is retained in all cases.
            File.Move(tmp, state.LogPath, overwrite: true);
            return new RepairResult(backup,
                state.Tail.State == "PartialTail" ? state.Tail.TailBytes : 0,
                state.Tail.State == "MissingNewline");
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* preserve diagnostic evidence */ }
            }
        }
    }

    public static Audit ReadAudit(string inputRoot, string outputRoot, CancellationToken ct = default)
    {
        using var state = Open(inputRoot, outputRoot);
        if (state.Tail.State is not ("Healthy" or "Empty"))
            throw new InvalidDataException("末尾が不完全です。先に診断・修復してください。");
        var plan = state.Plan;
        var rows = new List<Row>(plan.Entries.Count);
        long savings = 0;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string status = entry.Status;
            string note = entry.Message;
            long entrySavings = 0;
            if (status == "Completed")
            {
                string path = Path.GetFullPath(Path.Combine(state.Input, entry.RelativePath));
                string? preview = entry.PreviewPath;
                bool sourceUnchanged = File.Exists(path) && !IsReparse(path) &&
                    new FileInfo(path).Length == entry.SourceBytes &&
                    File.GetLastWriteTimeUtc(path).Ticks == entry.ModifiedUtcTicks;
                bool previewUnchanged = preview is not null && File.Exists(preview) &&
                    !IsReparse(preview) && new FileInfo(preview).Length == entry.PreviewBytes &&
                    entry.PreviewSha256 is not null &&
                    string.Equals(Safety.ComputeSha256(preview, ct), entry.PreviewSha256,
                        StringComparison.OrdinalIgnoreCase);
                if (sourceUnchanged && previewUnchanged)
                {
                    entrySavings = entry.SourceBytes - entry.PreviewBytes;
                    savings = checked(savings + entrySavings);
                    status = "CandidatePresent";
                }
                else
                {
                    status = "NeedsReview";
                    note = "原本の変更または候補の変更・消失。見込削減量に計上しません。";
                }
            }
            rows.Add(new Row(entry.RelativePath, status, entry.SourceBytes,
                entry.PreviewBytes, entrySavings, note, entry.PreviewPath ?? ""));
        }
        return new Audit(rows.Count, rows.Count(x => x.State == "CandidatePresent"),
            rows.Count(x => x.State == "Pending"), rows.Count(x => x.State == "Skipped"),
            rows.Count(x => x.State == "Failed"), rows.Count(x => x.State == "Deferred"),
            savings, state.Tail, rows);
    }

    public static void ExportCsv(Audit audit, string inputRoot, string outputPath)
    {
        string input = Path.GetFullPath(inputRoot);
        string output = Path.GetFullPath(outputPath);
        if (IsInside(output, input))
            throw new InvalidOperationException("CSVレポートは原本フォルダ外に保存してください。");
        if (!Directory.Exists(Path.GetDirectoryName(output)))
            throw new DirectoryNotFoundException("CSV保存先がありません。");
        if (File.Exists(output) && IsReparse(output))
            throw new InvalidOperationException("リンク先へのレポート上書きは禁止です。");
        string tmp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file, new UTF8Encoding(true)))
            {
                writer.WriteLine("RelativePath,Status,SourceBytes,PreviewBytes,EstimatedSavingBytes,PreviewPath,Note");
                foreach (var row in audit.Rows)
                    writer.WriteLine(string.Join(",", new[] { row.RelativePath, row.State,
                        row.SourceBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        row.PreviewBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        row.EstimatedSavingBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        row.PreviewPath, row.Note }.Select(Csv)));
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            File.Move(tmp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    private static string Csv(string value)
    {
        // Excel CSV formula injection: file names and metadata are untrusted text.
        value ??= "";
        if (value.Length != 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static State Open(string inputRoot, string outputRoot)
    {
        string input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        if (!Directory.Exists(input) || !Directory.Exists(output))
            throw new DirectoryNotFoundException("入力元または出力先がありません。");
        if (input.StartsWith(@"\\", StringComparison.Ordinal) ||
            output.StartsWith(@"\\", StringComparison.Ordinal) ||
            IsInside(input, output) || IsInside(output, input) ||
            IsReparse(input) || IsReparse(output))
            throw new InvalidOperationException("入力と出力は別階層のローカルフォルダに限定します。");
        string lockPath = Path.Combine(output, LockFile);
        string logPath = Path.Combine(output, LogFile);
        if ((File.Exists(lockPath) && IsReparse(lockPath)) ||
            (File.Exists(logPath) && IsReparse(logPath)))
            throw new InvalidDataException("作業ファイルがリンクです。");
        var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None);
        try
        {
            var plan = ReadPlan(input, output);
            byte[] bytes = File.Exists(logPath) ? ReadLimited(logPath) : Array.Empty<byte>();
            var examined = Examine(bytes, plan, output);
            return new State(input, output, logPath, lockStream, bytes, plan,
                examined.Tail, examined.CompleteBytes);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    private static PngBatchPreviewEngine.Plan ReadPlan(string input, string output)
    {
        string path = Path.Combine(output, PlanFile);
        if (!File.Exists(path) || IsReparse(path))
            throw new InvalidDataException("PNG一括処理の計画ファイルがないかリンクです。");
        var plan = JsonSerializer.Deserialize<PngBatchPreviewEngine.Plan>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("計画が読み取れません。");
        PngBatchPreviewEngine.ValidatePlan(plan, input, output, Path.Combine(output, PreviewDirectory));
        return plan;
    }

    private static byte[] ReadLimited(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumLogBytes)
            throw new InvalidDataException("128MiBを超える進捗ログは安全のため手動検証が必要です。");
        return File.ReadAllBytes(path);
    }

    private static (TailResult Tail, int CompleteBytes) Examine(byte[] bytes,
        PngBatchPreviewEngine.Plan plan, string output)
    {
        if (bytes.Length == 0) return (new("Empty", 0, "進捗ログは空です。"), 0);
        int lastLf = Array.LastIndexOf(bytes, (byte)'\n');
        int prefix = lastLf + 1;
        var dictionary = plan.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos < prefix)
        {
            int next = Array.IndexOf(bytes, (byte)'\n', pos);
            if (next < 0 || next >= prefix) throw new InvalidDataException("行区切りが不正です。");
            ParseAndApply(bytes.AsSpan(pos, next - pos), dictionary, output);
            pos = next + 1;
        }
        if (prefix == bytes.Length)
        {
            PngBatchPreviewEngine.ValidatePlan(plan, plan.InputRoot, plan.OutputRoot,
                Path.Combine(output, PreviewDirectory));
            return (new("Healthy", 0, "進捗ログは正常です。"), prefix);
        }
        var tail = bytes.AsSpan(prefix);
        try
        {
            ParseAndApply(tail, dictionary, output);
            PngBatchPreviewEngine.ValidatePlan(plan, plan.InputRoot, plan.OutputRoot,
                Path.Combine(output, PreviewDirectory));
            return (new("MissingNewline", 0, "最後のJSONは正常ですが改行がありません。"), bytes.Length);
        }
        catch (JsonException)
        {
            PngBatchPreviewEngine.ValidatePlan(plan, plan.InputRoot, plan.OutputRoot,
                Path.Combine(output, PreviewDirectory));
            return (new("PartialTail", tail.Length,
                "最後の1レコードのみ破損。元ログ全体をバックアップして末尾を除外できます。"), prefix);
        }
    }

    private static void ParseAndApply(ReadOnlySpan<byte> json,
        Dictionary<string, PngBatchPreviewEngine.Entry> lookup, string output)
    {
        if (json.Length == 0) return; // blank journal line
        var parsed = JsonSerializer.Deserialize<PngBatchPreviewEngine.Entry>(json)
            ?? throw new InvalidDataException("進捗レコードがnullです。");
        if (!lookup.TryGetValue(parsed.RelativePath, out var item) ||
            item.SourceBytes != parsed.SourceBytes || item.ModifiedUtcTicks != parsed.ModifiedUtcTicks ||
            parsed.Status is not ("Pending" or "Completed" or "Skipped" or "Failed" or "Deferred"))
            throw new InvalidDataException("進捗レコードが保存計画と一致しません。自動修復できません。");
        item.Status = parsed.Status;
        item.PreviewPath = parsed.PreviewPath;
        item.PreviewBytes = parsed.PreviewBytes;
        item.PreviewSha256 = parsed.PreviewSha256;
        item.Message = parsed.Message ?? "";
    }

    private static bool IsInside(string path, string dir)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return p.Equals(d, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsReparse(string path)
    {
        for (string? p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
            string? parent = Path.GetDirectoryName(p);
            if (parent is null || string.Equals(parent, p, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private sealed class State : IDisposable
    {
        private readonly FileStream _lock;
        public string Input { get; }
        public string Output { get; }
        public string LogPath { get; }
        public byte[] Bytes { get; }
        public PngBatchPreviewEngine.Plan Plan { get; }
        public TailResult Tail { get; }
        public int CompleteBytes { get; }
        public State(string input, string output, string logPath, FileStream mutex, byte[] bytes,
            PngBatchPreviewEngine.Plan plan, TailResult tail, int completeBytes)
        {
            Input = input; Output = output; LogPath = logPath; _lock = mutex;
            Bytes = bytes; Plan = plan; Tail = tail; CompleteBytes = completeBytes;
        }
        public void Dispose() => _lock.Dispose();
    }
}
