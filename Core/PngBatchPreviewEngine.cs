using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R1.6: a resumable, PREVIEW-ONLY PNG queue. Never replaces, renames,
/// hard-links or deletes a user source file. Journal belongs to output dir.
/// Failed entries can be retried on the next invocation.
/// </summary>
public static class PngBatchPreviewEngine
{
    private const int MaxEntries = 100_000;
    private const string StateName = ".chappy-png-batch-r16.json";
    private const string ProgressName = ".chappy-png-batch-r16.progress.jsonl";
    private const string PreviewName = "chappy-png-previews";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class Entry
    {
        public string RelativePath { get; set; } = "";
        public long SourceBytes { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public string Status { get; set; } = "Pending";
        public string? PreviewPath { get; set; }
        public long PreviewBytes { get; set; }
        public string? PreviewSha256 { get; set; }
        public string Message { get; set; } = "";
    }

    public sealed class Plan
    {
        public int SchemaVersion { get; set; } = 1;
        public string InputRoot { get; set; } = "";
        public string OutputRoot { get; set; } = "";
        public List<Entry> Entries { get; set; } = new();
        public int ScanErrors { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    public sealed record Progress(int Done, int Total, string RelativePath, string Status,
        long SavedBytes);
    public sealed record Summary(int Done, int Total, int Optimized, int Skipped,
        int Failed, long OriginalBytes, long PreviewBytes, string ManifestPath)
    {
        public long SavedBytes => OriginalBytes - PreviewBytes;
    }

    /// <summary>Start a new batch, or resume a saved batch from the same output folder.</summary>
    public static Summary Run(string inputRoot, string outputRoot,
        IProgress<Progress>? progress = null, CancellationToken ct = default)
    {
        string input = CanonicalDirectory(inputRoot);
        string output = CanonicalDirectory(outputRoot);
        if (IsWithin(output, input) || IsWithin(input, output))
            throw new InvalidOperationException("入力フォルダと出力フォルダは別の独立した階層にしてください。");
        if (IsReparsePath(input) || IsReparsePath(output))
            throw new InvalidOperationException("リンク/ジャンクション経由のフォルダは対象外です。");
        string previews = Path.Combine(output, PreviewName);
        string manifest = Path.Combine(output, StateName);
        string progressFile = Path.Combine(output, ProgressName);
        // Reject a dangerous substitution of our private preview directory.
        if (Directory.Exists(previews) && IsReparsePath(previews))
            throw new InvalidOperationException("候補出力フォルダがリンクです。");
        foreach (string privateFile in new[] { manifest, progressFile,
            Path.Combine(output, ".chappy-png-batch-r16.lock") })
            if (File.Exists(privateFile) && IsReparsePath(privateFile))
                throw new InvalidOperationException("作業記録がリンク経由です。安全のため停止します。");

        // Cross-process exclusive lock for this output root. A stale lock file
        // is harmless: only the open file handle controls exclusivity.
        using var batchLock = new FileStream(Path.Combine(output, ".chappy-png-batch-r16.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        if (!File.Exists(manifest) && File.Exists(progressFile))
            throw new InvalidDataException("計画がないのに進捗ログがあります。出力先を確認してください。");

        Plan plan;
        if (File.Exists(manifest))
        {
            plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(manifest), JsonOptions)
                ?? throw new InvalidDataException("再開記録が読めません。");
            ValidatePlan(plan, input, output, previews);
            LoadProgress(progressFile, plan);
            ValidatePlan(plan, input, output, previews);
        }
        else
        {
            plan = NewPlan(input, output, ct);
            // Journal must exist before the first output candidate is created.
            SavePlan(manifest, plan);
        }
        Directory.CreateDirectory(previews);
        if (IsReparsePath(previews)) throw new InvalidOperationException("候補出力がリンクです。");

        int total = plan.Entries.Count;
        int done = plan.Entries.Count(e => e.Status is "Completed" or "Skipped");
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string absolute = FullPathInside(input, entry.RelativePath);
            if (entry.Status is "Completed" or "Skipped")
            {
                if (entry.Status == "Skipped")
                {
                    progress?.Report(new Progress(done, total, entry.RelativePath, "既処理", 0));
                    continue;
                }
                var currentFile = new FileInfo(absolute);
                currentFile.Refresh();
                if (!currentFile.Exists || IsReparsePath(absolute) || currentFile.Length != entry.SourceBytes ||
                    currentFile.LastWriteTimeUtc.Ticks != entry.ModifiedUtcTicks)
                {
                    // The previously generated preview may still be present,
                    // but it must no longer be counted against the changed source.
                    entry.Status = "Skipped";
                    entry.Message = "候補生成後に原本が変化。既存候補は手動確認が必要です。";
                    entry.PreviewPath = null;
                    entry.PreviewBytes = 0;
                    entry.PreviewSha256 = null;
                    AppendProgress(progressFile, entry);
                    progress?.Report(new Progress(done, total, entry.RelativePath, "原本変更", 0));
                    continue;
                }
                if (entry.PreviewPath is not null && File.Exists(entry.PreviewPath))
                {
                    if (IsReparsePath(entry.PreviewPath))
                        throw new InvalidDataException("候補がリンクになっています。手動確認が必要です。");
                    if (new FileInfo(entry.PreviewPath).Length != entry.PreviewBytes ||
                        string.IsNullOrEmpty(entry.PreviewSha256) ||
                        !string.Equals(Safety.ComputeSha256(entry.PreviewPath, ct),
                            entry.PreviewSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("保存済み候補の内容が変化しました。手動で確認してください: " + entry.PreviewPath);
                    progress?.Report(new Progress(done, total, entry.RelativePath, "既処理", 0));
                    continue;
                }
                // Previously completed candidate was removed by the user.
                done--;
                entry.Status = "Pending";
                entry.PreviewPath = null;
                entry.PreviewBytes = 0;
                entry.PreviewSha256 = null;
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(absolute);
                info.Refresh();
                if (!info.Exists || IsReparsePath(absolute) ||
                    info.Length != entry.SourceBytes ||
                    info.LastWriteTimeUtc.Ticks != entry.ModifiedUtcTicks)
                {
                    entry.Status = "Skipped";
                    entry.Message = "前回スキャン後にファイルが変更/削除されました。再計画が必要です。";
                }
                else
                {
                    var optimized = PngLosslessOptimizer.CreatePreview(absolute, previews, ct);
                    entry.Status = optimized.PreviewPath is not null ? "Completed" :
                        optimized.Explanation.Contains("空き容量が不足", StringComparison.Ordinal) ?
                        "Deferred" : "Skipped";
                    entry.Message = optimized.Explanation;
                    entry.PreviewPath = optimized.PreviewPath;
                    entry.PreviewBytes = optimized.PreviewBytes;
                    entry.PreviewSha256 = optimized.PreviewPath is null ? null :
                        Safety.ComputeSha256(optimized.PreviewPath, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Keep a resumable state without marking the current item complete.
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or InvalidOperationException or NotSupportedException)
            {
                entry.Status = "Failed";
                entry.Message = ex.GetType().Name + ": " + ex.Message;
            }
            if (entry.Status != "Deferred") done++;
            AppendProgress(progressFile, entry);
            progress?.Report(new Progress(done, total, entry.RelativePath,
                entry.Status, entry.Status == "Completed" ? entry.SourceBytes - entry.PreviewBytes : 0));
            if (entry.Status == "Deferred") break; // Stop; retry this item on the next run.
        }
        return Summarize(plan, manifest);
    }

    private static Summary Summarize(Plan plan, string manifest)
    {
        var good = plan.Entries.Where(e => e.Status == "Completed").ToList();
        return new Summary(
            plan.Entries.Count(e => e.Status is "Completed" or "Skipped" or "Failed"),
            plan.Entries.Count, good.Count,
            plan.Entries.Count(e => e.Status == "Skipped"),
            plan.Entries.Count(e => e.Status == "Failed"),
            good.Sum(e => e.SourceBytes), good.Sum(e => e.PreviewBytes), manifest);
    }

    private static Plan NewPlan(string input, string output, CancellationToken ct)
    {
        var plan = new Plan { InputRoot = input, OutputRoot = output };
        var pending = new Stack<string>();
        pending.Push(input);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string current = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateDirectories(current))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(path);
                    }
                    catch (IOException) { plan.ScanErrors++; }
                    catch (UnauthorizedAccessException) { plan.ScanErrors++; }
                }
                foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var info = new FileInfo(file);
                        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline |
                            FileAttributes.System)) != 0) continue;
                        plan.Entries.Add(new Entry
                        {
                            RelativePath = Path.GetRelativePath(input, file),
                            SourceBytes = info.Length,
                            ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks
                        });
                        if (plan.Entries.Count > MaxEntries)
                            throw new InvalidOperationException($"R1.6は一度に{MaxEntries:N0}件までです。親フォルダを分けてください。");
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
            int c = a.SourceBytes.CompareTo(b.SourceBytes); // small first for low-space workflows
            return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        });
        return plan;
    }

    internal static void ValidatePlan(Plan plan, string input, string output, string previews)
    {
        if (plan.SchemaVersion != 1 || !PathEquals(plan.InputRoot, input) ||
            !PathEquals(plan.OutputRoot, output) || plan.Entries is null ||
            plan.Entries.Count > MaxEntries)
            throw new InvalidDataException("保存された作業記録のフォルダまたは形式が一致しません。別の出力先を選択してください。");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in plan.Entries)
        {
            string path = FullPathInside(input, e.RelativePath);
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                e.SourceBytes < 0 || e.PreviewBytes < 0 ||
                e.ModifiedUtcTicks <= 0 || !unique.Add(path) ||
                e.Status is not ("Pending" or "Completed" or "Skipped" or "Failed" or "Deferred"))
                throw new InvalidDataException("作業記録のファイル一覧が不正です。");
            if (e.Status == "Completed" &&
                (e.PreviewPath is null || e.PreviewBytes >= e.SourceBytes || e.PreviewSha256 is null))
                throw new InvalidDataException("完了記録の候補情報が不正です。");
            if (e.PreviewPath is not null && !IsWithin(e.PreviewPath, previews))
                throw new InvalidDataException("作業記録の候補ファイルが出力先の外にあります。");
            if (e.PreviewSha256 is not null &&
                (e.PreviewSha256.Length != 64 || !e.PreviewSha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("作業記録の候補ハッシュが不正です。");
        }
    }

    // One append per processed file: avoids re-serializing the entire scan plan
    // (which would generate quadratic disk writes for large folders).
    private static void AppendProgress(string path, Entry entry)
    {
        byte[] line = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry) + "\n");
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
        file.Write(line);
        file.Flush(flushToDisk: true);
    }

    private static void LoadProgress(string path, Plan plan)
    {
        if (!File.Exists(path)) return;
        var byPath = plan.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
        // An incomplete final record is treated as a safety stop. No source
        // files are touched and the evidence is preserved for recovery.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        using (var reader = new StreamReader(file, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                Entry parsed;
                try { parsed = JsonSerializer.Deserialize<Entry>(line) ?? throw new InvalidDataException(); }
                catch (JsonException)
                {
                    throw new InvalidDataException("再開ログが破損しています。手動で確認してください。");
                }
                if (!byPath.TryGetValue(parsed.RelativePath, out var original) ||
                    parsed.SourceBytes != original.SourceBytes ||
                    parsed.ModifiedUtcTicks != original.ModifiedUtcTicks)
                    throw new InvalidDataException("再開ログが計画と一致しません。");
                original.Status = parsed.Status;
                original.PreviewPath = parsed.PreviewPath;
                original.PreviewBytes = parsed.PreviewBytes;
                original.PreviewSha256 = parsed.PreviewSha256;
                original.Message = parsed.Message;
            }
        }
        // The last JSON line must end in LF. Preserve a corrupt/partial log.
        if (file.Length > 0)
        {
            file.Position = file.Length - 1;
            if (file.ReadByte() != '\n')
                throw new InvalidDataException("再開ログの末尾が未完了です。手動で確認してください: " + path);
        }
    }

    private static void SavePlan(string path, Plan plan)
    {
        // New temporary file + replace, so interrupted writes do not truncate
        // the last valid checkpoint. The only files touched are our state files.
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, plan, JsonOptions);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* leave diagnostic temp on disk */ }
            }
        }
    }

    private static string CanonicalDirectory(string dir)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("ネットワーク共有はR1.6対象外です。");
        return full;
    }
    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
    private static bool IsWithin(string potentialChild, string parent)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(potentialChild));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string FullPathInside(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("作業記録の相対パスが不正です。");
        string absolute = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsWithin(absolute, root) || PathEquals(absolute, root))
            throw new InvalidDataException("作業記録が対象フォルダ外を参照しています。");
        return absolute;
    }
    private static bool IsReparsePath(string full)
    {
        string current = full;
        while (!string.IsNullOrWhiteSpace(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || PathEquals(parent, current)) break;
            current = parent;
        }
        return false;
    }
}
