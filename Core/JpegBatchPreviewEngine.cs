using System.Text;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R1.9: read-only-source JPEG optimization queue. Candidates are stored separately,
/// and nothing in this class ever replaces/moves/deletes source files. An append-only
/// journal is fsynced after every item; torn journals fail closed on resume.
/// </summary>
public static class JpegBatchPreviewEngine
{
    internal const string PlanName = ".chappy-jpeg-r19.plan.json";
    internal const string JournalName = ".chappy-jpeg-r19.progress.jsonl";
    internal const string LockName = ".chappy-jpeg-r19.lock";
    internal const string PreviewName = "chappy-jpeg-r19-previews";
    private const int MaxEntries = 100_000;
    internal const long MaxJournalBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public sealed class Entry
    {
        public string RelativePath { get; set; } = "";
        public long SourceBytes { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public string? SourceSha256 { get; set; }
        public string Status { get; set; } = "Pending";
        public string? CandidatePath { get; set; }
        public long CandidateBytes { get; set; }
        public string? CandidateSha256 { get; set; }
        public string Message { get; set; } = "";
    }

    public sealed class Plan
    {
        public int SchemaVersion { get; set; } = 1;
        public string InputRoot { get; set; } = "";
        public string OutputRoot { get; set; } = "";
        public string ToolPath { get; set; } = "";
        public List<Entry> Entries { get; set; } = new();
        public int ScanErrors { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed record Progress(int Done, int Total, string RelativePath, string Status, long SavingBytes);
    public sealed record Summary(int Done, int Total, int Completed, int Skipped, int Failed,
        int NeedsReview, int Deferred, long EstimatedSavingBytes, int ScanErrors, string PlanPath);
    public sealed record ReportRow(string RelativePath, string Status, long SourceBytes,
        long CandidateBytes, long VerifiedSavingBytes, string CandidatePath, string Note);
    public sealed record Audit(IReadOnlyList<ReportRow> Rows, long VerifiedSavingBytes, int NeedsReview);

    public static Summary Run(string inputRoot, string outputRoot, string jpegtranExe,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        var input = CanonicalFolder(inputRoot);
        var output = CanonicalFolder(outputRoot);
        var tool = Path.GetFullPath(jpegtranExe);
        ValidateFolders(input, output);
        CheckNoReparse(tool);
        if (!File.Exists(tool) || !Path.GetFileName(tool).Equals("jpegtran.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("公式libjpeg-turboのjpegtran.exeを指定してください。", tool);
        string previews = Path.Combine(output, PreviewName);
        ValidateWorkingPaths(output, previews);
        string planPath = Path.Combine(output, PlanName);
        string journalPath = Path.Combine(output, JournalName);
        using var exclusive = new FileStream(Path.Combine(output, LockName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        Plan plan;
        if (File.Exists(planPath))
        {
            plan = LoadPlan(planPath);
            ValidatePlan(plan, input, output, tool);
            ReplayJournal(journalPath, plan);
            ValidatePlan(plan, input, output, tool);
        }
        else
        {
            if (File.Exists(journalPath))
                throw new InvalidDataException("計画が存在せず進捗ログだけが残っています。手動確認してください。");
            plan = BuildPlan(input, output, tool, ct);
            SaveNewPlan(planPath, plan);
        }
        Directory.CreateDirectory(previews);
        CheckNoReparse(previews);
        int total = plan.Entries.Count;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Status is "Completed" or "Skipped" or "NeedsReview")
            {
                if (entry.Status == "Completed" && !ValidateExistingCandidate(input, entry, ct))
                {
                    entry.Status = "NeedsReview";
                    entry.Message = "原本または候補の内容が変化・消失。前回の候補を削除せず要確認としました。";
                    Append(journalPath, entry);
                }
                else if (entry.Status == "Skipped" && !SourceUnchanged(input, entry, ct, checkHash: true))
                {
                    entry.Status = "NeedsReview";
                    entry.Message = "前回のスキップ後に元ファイルが変化。新しい出力先で再計画してください。";
                    Append(journalPath, entry);
                }
                progress?.Report(new Progress(DoneCount(plan), total, entry.RelativePath, entry.Status, 0));
                continue;
            }
            string original = SourcePath(input, entry.RelativePath);
            try
            {
                if (!SourceUnchanged(input, entry, ct, checkHash: false))
                {
                    entry.Status = "NeedsReview";
                    entry.Message = "スキャン時の原本とサイズまたは更新日時が異なります。";
                }
                else
                {
                    // Bind this candidate to the pre-optimization source bytes,
                    // rather than trusting a hash computed only after jpegtran finishes.
                    string sourceBefore = Safety.ComputeSha256(original, ct);
                    var result = JpegLosslessPreviewEngine.CreatePreview(original, previews, tool, ct);
                    if (result.Candidate is not null)
                    {
                        // The original is still locked against writers inside the preview engine.
                        // Recompute its identity for safe read-only auditing on later runs.
                        entry.SourceSha256 = sourceBefore;
                        entry.CandidatePath = result.Candidate;
                        entry.CandidateBytes = result.CandidateBytes;
                        entry.CandidateSha256 = Safety.ComputeSha256(result.Candidate, ct);
                        entry.Status = SourceUnchanged(input, entry, ct, checkHash: true)
                            ? "Completed" : "NeedsReview";
                        entry.Message = entry.Status == "Completed" ? result.Explanation :
                            "候補生成後に原本が変化しました。候補は保留。";
                    }
                    else
                    {
                        entry.SourceSha256 = sourceBefore;
                        bool unchanged = SourceUnchanged(input, entry, ct, checkHash: true);
                        entry.Status = !unchanged ? "NeedsReview" :
                            result.Explanation.Contains("空き", StringComparison.Ordinal) ?
                            "Deferred" : "Skipped";
                        entry.Message = unchanged ? result.Explanation :
                            "候補検査中に原本が変更されました。手動確認が必要です。";
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or InvalidOperationException or NotSupportedException or
                System.ComponentModel.Win32Exception)
            {
                entry.Status = "Failed";
                entry.Message = ex.GetType().Name + ": " + ex.Message;
            }
            Append(journalPath, entry);
            progress?.Report(new Progress(DoneCount(plan), total, entry.RelativePath, entry.Status,
                entry.Status == "Completed" ? entry.SourceBytes - entry.CandidateBytes : 0));
            if (entry.Status == "Deferred") break; // disk is full; defer the rest
        }
        return Summarize(plan, planPath);
    }

    public static Audit Inspect(string inputRoot, string outputRoot, string jpegtranExe,
        CancellationToken ct = default)
    {
        string input = CanonicalFolder(inputRoot), output = CanonicalFolder(outputRoot);
        string tool = Path.GetFullPath(jpegtranExe);
        ValidateFolders(input, output);
        ValidateWorkingPaths(output, Path.Combine(output, PreviewName));
        using var exclusive = new FileStream(Path.Combine(output, LockName), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var plan = LoadPlan(Path.Combine(output, PlanName));
        ValidatePlan(plan, input, output, tool);
        ReplayJournal(Path.Combine(output, JournalName), plan);
        ValidatePlan(plan, input, output, tool);
        var rows = new List<ReportRow>(plan.Entries.Count);
        long saving = 0;
        int needsReview = 0;
        foreach (var e in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            bool good = e.Status == "Completed" && ValidateExistingCandidate(input, e, ct);
            string status = e.Status == "Completed" && !good ? "NeedsReview" : e.Status;
            if (status == "NeedsReview") needsReview++;
            long delta = good ? e.SourceBytes - e.CandidateBytes : 0;
            saving = checked(saving + delta);
            rows.Add(new ReportRow(e.RelativePath, status, e.SourceBytes,
                e.CandidateBytes, delta, e.CandidatePath ?? "", e.Message));
        }
        return new Audit(rows, saving, needsReview);
    }

    public static void ExportCsv(Audit audit, string inputRoot, string outputRoot, string csvPath)
    {
        string csv = Path.GetFullPath(csvPath);
        string input = CanonicalFolder(inputRoot), output = CanonicalFolder(outputRoot);
        ValidateFolders(input, output);
        if (Inside(csv, input) || Inside(csv, output))
            throw new InvalidOperationException("CSVは原本・候補フォルダの外に保存してください。");
        if (!Directory.Exists(Path.GetDirectoryName(csv)))
            throw new DirectoryNotFoundException("CSV出力フォルダがありません。");
        CheckNoReparse(Path.GetDirectoryName(csv)!);
        if (File.Exists(csv)) throw new IOException("既存のCSVは上書きしません。別名を指定してください。");
        string temp = csv + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                w.WriteLine("RelativePath,Status,SourceBytes,CandidateBytes,VerifiedEstimatedSavingBytes,CandidatePath,Note");
                foreach (var e in audit.Rows)
                    w.WriteLine(string.Join(",", new[] { e.RelativePath, e.Status,
                        e.SourceBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        e.CandidateBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        e.VerifiedSavingBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        e.CandidatePath, e.Note }.Select(EscapeCsv)));
                w.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temp, csv); // no replace
        }
        finally { if (File.Exists(temp)) { try { File.Delete(temp); } catch { } } }
    }

    private static string EscapeCsv(string value)
    {
        value ??= "";
        if (value.Length != 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    internal static Plan BuildPlan(string input, string output, string tool, CancellationToken ct)
    {
        ValidateFolders(input, output);
        var plan = new Plan { InputRoot = input, OutputRoot = output, ToolPath = tool };
        var pending = new Stack<string>(); pending.Push(input);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    try { if (!IsReparse(sub)) pending.Push(sub); }
                    catch (IOException) { plan.ScanErrors++; }
                    catch (UnauthorizedAccessException) { plan.ScanErrors++; }
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsJpeg(file)) continue;
                    try
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline |
                            FileAttributes.System)) != 0) continue;
                        plan.Entries.Add(new Entry { RelativePath = Path.GetRelativePath(input, file),
                            SourceBytes = info.Length, ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks });
                        if (plan.Entries.Count > MaxEntries)
                            throw new InvalidOperationException("JPG一括処理は1回100,000件までです。対象を分けてください。");
                    }
                    catch (IOException) { plan.ScanErrors++; }
                    catch (UnauthorizedAccessException) { plan.ScanErrors++; }
                }
            }
            catch (IOException) { plan.ScanErrors++; }
            catch (UnauthorizedAccessException) { plan.ScanErrors++; }
        }
        plan.Entries.Sort((a, b) =>
        {
            int result = a.SourceBytes.CompareTo(b.SourceBytes);
            return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        });
        ValidatePlan(plan, input, output, tool);
        return plan;
    }

    internal static void ValidatePlan(Plan plan, string input, string output, string tool)
    {
        if (plan.SchemaVersion != 1 || !SamePath(plan.InputRoot, input) ||
            !SamePath(plan.OutputRoot, output) || !SamePath(plan.ToolPath, tool) ||
            plan.Entries is null || plan.Entries.Count > MaxEntries || plan.ScanErrors < 0)
            throw new InvalidDataException("JPG計画が指定された入力・出力・ツールと一致しません。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string previewRoot = Path.Combine(output, PreviewName);
        foreach (var e in plan.Entries)
        {
            string path = SourcePath(input, e.RelativePath);
            if (!IsJpeg(path) || e.SourceBytes < 0 || e.ModifiedUtcTicks <= 0 ||
                e.CandidateBytes < 0 || !seen.Add(path) ||
                e.Status is not ("Pending" or "Completed" or "Skipped" or "Failed" or "Deferred" or "NeedsReview"))
                throw new InvalidDataException("JPG計画内に不正なファイル情報があります。");
            if (e.CandidatePath is not null && (!Inside(e.CandidatePath, previewRoot) ||
                !e.CandidatePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("候補パスが専用出力フォルダ外です。");
            if ((e.Status is "Completed" or "Skipped") && e.SourceSha256 is null)
                throw new InvalidDataException("JPG処理済みの原本ハッシュがありません。");
            if (e.Status == "Completed" && (e.SourceSha256 is null || e.CandidatePath is null ||
                e.CandidateSha256 is null || e.CandidateBytes >= e.SourceBytes))
                throw new InvalidDataException("JPG完了記録の候補情報が不正です。");
            if ((e.SourceSha256 is not null && !ValidHash(e.SourceSha256)) ||
                (e.CandidateSha256 is not null && !ValidHash(e.CandidateSha256)))
                throw new InvalidDataException("JPGハッシュが不正です。");
        }
    }

    private static Summary Summarize(Plan plan, string path) => new(
        DoneCount(plan), plan.Entries.Count,
        plan.Entries.Count(e => e.Status == "Completed"),
        plan.Entries.Count(e => e.Status == "Skipped"),
        plan.Entries.Count(e => e.Status == "Failed"),
        plan.Entries.Count(e => e.Status == "NeedsReview"),
        plan.Entries.Count(e => e.Status == "Deferred"),
        plan.Entries.Where(e => e.Status == "Completed").Sum(e => e.SourceBytes - e.CandidateBytes),
        plan.ScanErrors, path);

    private static int DoneCount(Plan plan) => plan.Entries.Count(e =>
        e.Status is "Completed" or "Skipped" or "Failed" or "NeedsReview");

    private static bool SourceUnchanged(string input, Entry e, CancellationToken ct, bool checkHash)
    {
        string path = SourcePath(input, e.RelativePath);
        if (!File.Exists(path) || IsReparse(path)) return false;
        var info = new FileInfo(path);
        if ((info.Attributes & (FileAttributes.Offline | FileAttributes.System)) != 0 ||
            info.Length != e.SourceBytes || info.LastWriteTimeUtc.Ticks != e.ModifiedUtcTicks)
            return false;
        return !checkHash || (e.SourceSha256 is not null &&
            string.Equals(Safety.ComputeSha256(path, ct), e.SourceSha256, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidateExistingCandidate(string input, Entry e, CancellationToken ct)
    {
        if (!SourceUnchanged(input, e, ct, true) || e.CandidatePath is null ||
            !File.Exists(e.CandidatePath) || IsReparse(e.CandidatePath) ||
            new FileInfo(e.CandidatePath).Length != e.CandidateBytes || e.CandidateSha256 is null)
            return false;
        return string.Equals(Safety.ComputeSha256(e.CandidatePath, ct), e.CandidateSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    internal static Plan LoadPlan(string path) =>
        JsonSerializer.Deserialize<Plan>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException("JPGの計画ファイルを読み取れません。");

    private static void SaveNewPlan(string path, Plan plan)
    {
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, plan, Json); file.Flush(flushToDisk: true); }
            File.Move(tmp, path); // never overwrite an existing plan
        }
        finally { if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { } } }
    }

    private static void Append(string path, Entry e)
    {
        byte[] record = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e) + "\n");
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        if (stream.Length + record.Length > MaxJournalBytes)
            throw new IOException("進捗ログが128MiBを超えました。安全のため停止します。");
        stream.Write(record);
        stream.Flush(flushToDisk: true);
    }

    internal static void ReplayJournal(string path, Plan plan)
    {
        if (!File.Exists(path)) return;
        var file = new FileInfo(path);
        if (file.Length > MaxJournalBytes)
            throw new InvalidDataException("JPG進捗ログが128MiBを超えています。");
        ReplayJournalBytes(File.ReadAllBytes(path), plan);
    }

    // A single validation path for normal resume, read-only inspection and explicit tail repair.
    internal static void ReplayJournalBytes(ReadOnlySpan<byte> bytes, Plan plan)
    {
        if (bytes.Length > MaxJournalBytes)
            throw new InvalidDataException("JPG進捗ログが128MiBを超えています。");
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
            throw new InvalidDataException("JPG進捗ログの末尾に未完了レコードがあります。");
        var lookup = plan.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        int start = 0;
        while (start < bytes.Length)
        {
            int offset = bytes[start..].IndexOf((byte)'\n');
            if (offset < 0) throw new InvalidDataException("JPGログの行末が不正です。");
            var line = bytes.Slice(start, offset);
            start += offset + 1;
            if (line.Length == 0) continue;
            if (line.Length > 256 * 1024) throw new InvalidDataException("JPG進捗の1行が長すぎます。");
            Entry logged;
            try { logged = JsonSerializer.Deserialize<Entry>(line) ??
                throw new InvalidDataException("JPG進捗レコードがnullです。"); }
            catch (JsonException ex) { throw new InvalidDataException("JPG進捗ログの途中に破損があります。", ex); }
            if (string.IsNullOrWhiteSpace(logged.RelativePath) ||
                !lookup.TryGetValue(logged.RelativePath, out var original) ||
                logged.SourceBytes != original.SourceBytes ||
                logged.ModifiedUtcTicks != original.ModifiedUtcTicks)
                throw new InvalidDataException("JPG進捗と計画のファイルが一致しません。");
            // Validate EACH row before overwrite. A subsequent valid record must
            // never conceal an invalid completed record in the middle of a log.
            if (logged.Status is not ("Pending" or "Completed" or "Skipped" or "Failed" or
                "Deferred" or "NeedsReview") || logged.CandidateBytes < 0 ||
                (logged.SourceSha256 is not null && !ValidHash(logged.SourceSha256)) ||
                (logged.CandidateSha256 is not null && !ValidHash(logged.CandidateSha256)) ||
                (logged.CandidatePath is not null &&
                    (!Inside(logged.CandidatePath, Path.Combine(plan.OutputRoot, PreviewName)) ||
                     !logged.CandidatePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))) ||
                ((logged.Status is "Completed" or "Skipped") && logged.SourceSha256 is null) ||
                (logged.Status == "Completed" &&
                    (logged.CandidatePath is null || logged.CandidateSha256 is null ||
                     logged.CandidateBytes >= logged.SourceBytes)))
                throw new InvalidDataException("JPG進捗ログの中間レコードが不正です。");
            original.Status = logged.Status;
            original.SourceSha256 = logged.SourceSha256;
            original.CandidatePath = logged.CandidatePath;
            original.CandidateBytes = logged.CandidateBytes;
            original.CandidateSha256 = logged.CandidateSha256;
            original.Message = logged.Message ?? "";
        }
    }

    internal static void ValidateWorkingPaths(string output, string previews)
    {
        CheckNoReparse(output);
        if (Directory.Exists(previews)) CheckNoReparse(previews);
        foreach (string name in new[] { PlanName, JournalName, LockName })
        {
            string path = Path.Combine(output, name);
            if (File.Exists(path)) CheckNoReparse(path);
        }
    }

    internal static string CanonicalFolder(string path)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full) || full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new DirectoryNotFoundException("ローカルフォルダを指定してください: " + full);
        CheckNoReparse(full);
        return full;
    }
    internal static void ValidateFolders(string input, string output)
    {
        if (Inside(input, output) || Inside(output, input))
            throw new InvalidOperationException("入力フォルダと候補保存先は互いに別階層にしてください。");
    }
    private static bool IsJpeg(string path) =>
        path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);
    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string root)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string SourcePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("計画内に不正な相対パスがあります。");
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!Inside(result, root) || SamePath(result, root))
            throw new InvalidDataException("元ファイルのパスが親フォルダ外です。");
        return result;
    }
    internal static void CheckNoReparse(string path)
    {
        if (IsReparse(path)) throw new InvalidOperationException("リンク/ジャンクション経由は安全のため対象外です: " + path);
    }
    private static bool IsReparse(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || SamePath(parent, current)) break;
        }
        return false;
    }
}
