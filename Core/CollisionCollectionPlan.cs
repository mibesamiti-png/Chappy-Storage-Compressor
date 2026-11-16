using System.Text;
#if CHAPPY_TESTING
using System.Security.Cryptography;
#endif

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.17 Part34: non-mutating collision plan for a future extraction or
/// independent restore workflow. A generated-only exercise is available
/// under CHAPPY_TESTING. This is NOT connected to the production Undo/merge.
/// </summary>
public static class CollisionCollectionPlan
{
    public const string FolderName = "同名ファイル";
    public const string ReportName = "同名ファイル一覧.txt";
    public const bool ProductionExtractionOrUndoEnabled = false;

    public sealed record Entry(string OriginalRelativePath,
        string DestinationRelativePath, bool Collected, string Reason);

    public sealed record Result(string Root, string CollectionRelativePath,
        string ReportRelativePath, IReadOnlyList<Entry> Entries)
    {
        public int Conflicts => Entries.Count(e => e.Collected);
        public bool CanModifyUserFiles => false;
        public string Describe() =>
            $"同名ファイル {Conflicts}件 / 予定 {Entries.Count}件\n" +
            (Conflicts > 0
                ? $"集約先: {CollectionRelativePath}\n報告書: {ReportRelativePath}\n"
                : "同名ファイルなし。報告書は作成しません。\n") +
            "既存ファイルへの上書きなし。これは読み取り専用の予定表です。";
    }

    private static string ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("絶対パス、空の名前は許可しません");
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith(' ') || p.EndsWith('.') ||
            p.Contains(':') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("不正なパスや親フォルダへの参照は禁止です");
        // NTFS names are case-insensitive; normalization preserves the
        // entry's intended subdirectory layout.
        return Path.Combine(parts);
    }

    private static string Full(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("対象ルートの外に書き出せません");
        return path;
    }

    private static bool UnsafeParentOrFile(string root, string relative,
        out string reason)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        string current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (File.Exists(current))
            {
                reason = "親フォルダ名が既存ファイルと衝突";
                return true;
            }
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("リンク・ジャンクション内には展開できません");
        }
        string target = Full(root, relative);
        if ((File.Exists(target) || Directory.Exists(target)) &&
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("リンク/ジャンクションの名前は要手動確認");
        if (File.Exists(target) || Directory.Exists(target))
        {
            reason = "同名のファイルまたはフォルダが既に存在";
            return true;
        }
        reason = "";
        return false;
    }

    private static string CollectionRoot(string root)
    {
        string folder = FolderName;
        // User choice: if 同名ファイル exists, create 同名ファイル
        // INSIDE 同名ファイル; never overwrite/rename the old directory.
        for (int depth = 0; depth < 32; depth++)
        {
            string path = Full(root, folder);
            if (File.Exists(path) || (Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new InvalidDataException(
                    "同名フォルダが通常のフォルダでないため操作せず停止");
            if (!Directory.Exists(path)) return folder;
            folder = Path.Combine(folder, FolderName);
        }
        throw new InvalidDataException("同名フォルダの入れ子が深すぎます。手動確認してください");
    }

    private static string AvailableReport(string root, HashSet<string> reserved)
    {
        for (int number = 0; number < 10000; number++)
        {
            string name = number == 0 ? ReportName
                : $"同名ファイル一覧 ({number}).txt";
            if (!reserved.Contains(name) &&
                !UnsafeParentOrFile(root, name, out _))
                return name;
        }
        throw new IOException("既存の報告書が多すぎます。上書きはしません");
    }

    public static Result Inspect(string outputRoot, IEnumerable<string> incomingPaths)
    {
        string root = Safety.ValidateRoot(outputRoot);
        var normalized = incomingPaths.Select(ValidateRelative).ToList();
        if (normalized.Count > 200000)
            throw new InvalidDataException("一度に扱う項目数が上限を超えています");
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>(normalized.Count);
        string? collection = null;

        foreach (string relative in normalized)
        {
            bool occupied = UnsafeParentOrFile(root, relative, out string reason);
            if (!occupied && reserved.Contains(relative))
            {
                occupied = true;
                reason = "展開・復元予定に同じパスが複数含まれる";
            }
            if (!occupied)
            {
                reserved.Add(relative);
                entries.Add(new Entry(relative, relative, false, ""));
                continue;
            }

            collection ??= CollectionRoot(root);
            string parent = Path.GetDirectoryName(relative) ?? "";
            string basename = Path.GetFileNameWithoutExtension(relative);
            string extension = Path.GetExtension(relative);
            bool placed = false;
            for (int n = 1; n < 10000; n++)
            {
                // The first collision is always "(1)"; even when the
                // collision resides in a different original subfolder,
                // the original relative hierarchy is kept under the
                // single central 同名ファイル collection directory.
                string proposed = Path.Combine(collection, parent,
                    $"{basename} ({n}){extension}");
                if (reserved.Contains(proposed) ||
                    UnsafeParentOrFile(root, proposed, out _))
                    continue;
                reserved.Add(proposed);
                entries.Add(new Entry(relative, proposed, true, reason));
                placed = true;
                break;
            }
            if (!placed)
                throw new IOException("同名ファイルの連番上限に達しました");
        }

        return new Result(root, collection ?? "", collection is null
            ? "" : AvailableReport(root, reserved), entries);
    }

    public static string ReportText(Result plan)
    {
        if (plan.Conflicts == 0) return "";
        var b = new StringBuilder();
        b.AppendLine("Chappy Storage Compressor — 同名ファイルの退避一覧")
         .AppendLine("既存のファイル・フォルダを上書きしていません。")
         .AppendLine("同名フォルダ内に相対パスを保持し、連番で保存しています。")
         .AppendLine($"同名件数: {plan.Conflicts}")
         .AppendLine($"保存先: {plan.CollectionRelativePath}")
         .AppendLine("対象ルート: " + plan.Root)
         .AppendLine();
        foreach (var (item, i) in plan.Entries.Where(x => x.Collected)
            .Select((x, i) => (x, i + 1)))
        {
            b.AppendLine($"{i}. 元の相対パス: {item.OriginalRelativePath}")
             .AppendLine($"   保存先: {item.DestinationRelativePath}")
             .AppendLine($"   理由: {item.Reason}");
        }
        b.AppendLine()
         .AppendLine("注意: 自動上書きや同名ファイルの自動削除はしていません。");
        return b.ToString();
    }

#if CHAPPY_TESTING
    // Only for generated Windows Core smoke fixtures. The production app
    // cannot invoke this writer and no real decompressor is connected.
    internal static void ExerciseSynthetic(string testParent, Result plan,
        IReadOnlyList<(string Source, string Relative)> incoming)
    {
        string parent = Safety.ValidateRoot(testParent);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!parent.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !parent.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(parent, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only" ||
            !plan.Root.StartsWith(parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            incoming.Count != plan.Entries.Count ||
            !Directory.Exists(plan.Root))
            throw new InvalidOperationException("同名ファイル試験は人工NTFSルート限定");

        // Require an identical new read-only plan at execution time. If
        // another program has added a filename since preview, do not copy
        // or overwrite anything and require re-planning first.
        var fresh = Inspect(plan.Root, incoming.Select(x => x.Relative));
        if (fresh.CollectionRelativePath != plan.CollectionRelativePath ||
            fresh.ReportRelativePath != plan.ReportRelativePath ||
            !fresh.Entries.SequenceEqual(plan.Entries))
            throw new InvalidDataException("プレビュー後に同名ファイル状態が変わったため中止");

        for (int i = 0; i < incoming.Count; i++)
        {
            var source = Path.GetFullPath(incoming[i].Source);
            if (!source.StartsWith(parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !source.StartsWith(Path.Combine(parent, "part34-incoming") +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(source) ||
                (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 ||
                NativeFile.HasNamedDataStreams(source) ||
                incoming[i].Relative != plan.Entries[i].OriginalRelativePath ||
                File.Exists(Full(plan.Root, plan.Entries[i].DestinationRelativePath)))
                throw new InvalidDataException("未確認の元データ/保存先は変更しません");
        }

        // This small exercise uses CreateNew exclusively. A true product
        // needs a durable all-or-nothing transaction and crash-safe report
        // before any user-writable mutation can be enabled.
        foreach (var (source, i) in incoming.Select((x, i) => (x.Source, i)))
        {
            string output = Full(plan.Root, plan.Entries[i].DestinationRelativePath);
            string folder = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(folder);
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("展開先がジャンクションへ変化しました");
            using var read = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.Read);
            using var write = new FileStream(output, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            read.CopyTo(write);
            write.Flush(flushToDisk: true);
            if (Safety.ComputeSha256(source, CancellationToken.None) !=
                Safety.ComputeSha256(output, CancellationToken.None))
                throw new InvalidDataException("生成データの書き出し内容が不一致");
        }
        if (plan.Conflicts > 0)
        {
            using var report = new FileStream(Full(plan.Root, plan.ReportRelativePath),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
                .GetBytes(ReportText(plan));
            report.Write(utf8);
            report.Flush(flushToDisk: true);
        }
    }
#endif
}
