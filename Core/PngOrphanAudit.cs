using System.Text;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Read-only catalog of untracked files in the R1.6+ PNG preview directory.
/// "Untracked" does NOT mean safe to delete. Never mutates a PNG candidate.
/// </summary>
public static class PngOrphanAudit
{
    private const string PlanName = ".chappy-png-batch-r16.json";
    private const string LogName = ".chappy-png-batch-r16.progress.jsonl";
    private const string LockName = ".chappy-png-batch-r16.lock";
    private const string PreviewFolderName = "chappy-png-previews";

    public sealed record Item(string Path, long Bytes, bool LooksGenerated,
        string Status);
    public sealed record Result(int Tracked, IReadOnlyList<Item> Untracked,
        long UntrackedBytes, string Note);

    public static Result Inspect(string inputRoot, string outputRoot, CancellationToken ct = default)
    {
        string input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        if (Inside(input, output) || Inside(output, input) || !Directory.Exists(input) ||
            !Directory.Exists(output) || input.StartsWith(@"\\", StringComparison.Ordinal) ||
            output.StartsWith(@"\\", StringComparison.Ordinal) || IsReparse(input) || IsReparse(output))
            throw new InvalidOperationException("入力と出力は別階層の通常フォルダが必要です。");
        string planPath = Path.Combine(output, PlanName);
        string logPath = Path.Combine(output, LogName);
        string lockPath = Path.Combine(output, LockName);
        string previews = Path.Combine(output, PreviewFolderName);
        if (!File.Exists(planPath) || IsReparse(planPath) ||
            (File.Exists(logPath) && IsReparse(logPath)) ||
            (File.Exists(lockPath) && IsReparse(lockPath)))
            throw new InvalidDataException("PNG作業記録がない、またはリンクが使われています。");
        using var guard = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // Must require a fully healthy journal; don't present partly-written records as orphans.
        var plan = JsonSerializer.Deserialize<PngBatchPreviewEngine.Plan>(File.ReadAllBytes(planPath))
            ?? throw new InvalidDataException("PNG作業計画が読めません。");
        PngBatchPreviewEngine.ValidatePlan(plan, input, output, previews);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in plan.Entries) Remember(entry.PreviewPath);
        if (File.Exists(logPath))
        {
            if (new FileInfo(logPath).Length > 128L * 1024 * 1024)
                throw new InvalidDataException("128MiBより大きいログは安全のため監査対象外です。");
            byte[] bytes = File.ReadAllBytes(logPath);
            if (bytes.Length != 0 && bytes[^1] != (byte)'\n')
                throw new InvalidDataException("未完了の進捗ログです。先に末尾診断を行ってください。");
            using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
            var byRelative = plan.Entries.ToDictionary(e => e.RelativePath, StringComparer.OrdinalIgnoreCase);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                var item = JsonSerializer.Deserialize<PngBatchPreviewEngine.Entry>(line)
                    ?? throw new InvalidDataException("ログにnullレコードがあります。");
                if (!byRelative.TryGetValue(item.RelativePath, out var original) ||
                    original.SourceBytes != item.SourceBytes ||
                    original.ModifiedUtcTicks != item.ModifiedUtcTicks ||
                    item.Status is not ("Pending" or "Completed" or "Skipped" or "Failed" or "Deferred"))
                    throw new InvalidDataException("ログと作業計画が一致しません。");
                if (item.PreviewPath is not null && !Inside(item.PreviewPath, previews))
                    throw new InvalidDataException("候補パスが出力ディレクトリ外です。");
                Remember(item.PreviewPath);  // Preserve all historical candidates, too.
            }
        }
        var untracked = new List<Item>();
        if (Directory.Exists(previews))
        {
            if (IsReparse(previews)) throw new InvalidOperationException("プレビュー格納場所がリンクです。");
            foreach (string path in Directory.EnumerateFiles(previews, "*", SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (untracked.Count >= 100_000)
                    throw new InvalidOperationException("監査上限10万件を超えました。");
                string normalized = Path.GetFullPath(path);
                if (referenced.Contains(normalized)) continue;
                if (IsReparse(normalized))
                    untracked.Add(new(path, 0, false, "リンク / 手動確認のみ"));
                else
                {
                    var info = new FileInfo(normalized);
                    bool generated = LooksGenerated(Path.GetFileName(normalized));
                    untracked.Add(new(path, info.Length, generated,
                        generated ? "未参照の候補風ファイル（削除禁止）" : "利用者作成の可能性・操作禁止"));
                }
            }
        }
        return new(referenced.Count, untracked, untracked.Sum(x => x.Bytes),
            "未参照でも自動削除・移動しません。処理途中の候補や手動配置ファイルを含む可能性があります。");

        void Remember(string? path)
        {
            if (path is null) return;
            if (!Inside(path, previews))
                throw new InvalidDataException("候補参照が対象フォルダ外です。");
            referenced.Add(Path.GetFullPath(path));
        }
    }

    public static void ExportCsv(Result result, string inputRoot, string savePath)
    {
        string output = Path.GetFullPath(savePath);
        if (Inside(output, inputRoot))
            throw new InvalidOperationException("原本内にレポートを書き込めません。");
        if (!Directory.Exists(Path.GetDirectoryName(output)))
            throw new DirectoryNotFoundException("保存先が存在しません。");
        if (File.Exists(output) && IsReparse(output))
            throw new InvalidOperationException("リンクへの出力は禁止です。");
        string temp = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
            {
                writer.WriteLine("Path,Bytes,LooksGenerated,Status");
                foreach (var row in result.Untracked)
                    writer.WriteLine(string.Join(",", Quote(row.Path), row.Bytes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture), row.LooksGenerated,
                        Quote(row.Status)));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temp, output, overwrite: true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    private static string Quote(string s)
    {
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
    private static bool LooksGenerated(string name)
    {
        int i = name.LastIndexOf(".chappy-preview-", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
            name.Length == i + ".chappy-preview-".Length + 32 + 4 &&
            Guid.TryParseExact(name.Substring(i + ".chappy-preview-".Length, 32), "N", out _);
    }
    private static bool Inside(string file, string parent)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(file));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsReparse(string path)
    {
        for (string? p = path; !string.IsNullOrWhiteSpace(p); p = Path.GetDirectoryName(p))
        {
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
            string? parent = Path.GetDirectoryName(p);
            if (parent is null || string.Equals(parent, p, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }
}
