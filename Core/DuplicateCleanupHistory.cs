using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Product-visible READ-ONLY presentation of duplicate-cleanup synthetic
/// journal records. This is never authority to move/restore/delete anything.
/// In particular: file existence is NOT identity verification and a shell
/// recycle intent is NOT proof the item reached the Recycle Bin.
/// </summary>
public static class DuplicateCleanupHistory
{
    public const bool CanModifyUserFiles = false;
    private const string JournalFolder = ".chappy-cleanup-evidence";
    private const long MaxIntentBytes = 2L * 1024 * 1024;
    private const int MaxGroups = 128;
    private const int MaxTotalRows = 4096;

    public sealed record Row(
        string OperationId, int CandidateIndex, string OriginalPath,
        string StagedPath, string KeeperPath, string State, string Note);

    private static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(path) &&
                full.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool SafeOriginal(string root, string path)
    {
        if (!IsInside(root, path)) return false;
        try
        {
            string[] names = Path.GetRelativePath(root, path)
                .Split(Path.DirectorySeparatorChar);
            return names.All(name =>
                name.Length > 0 && name is not "." and not ".." &&
                !name.Equals(JournalFolder, StringComparison.OrdinalIgnoreCase) &&
                !name.Equals(".ChappyDedupeStore", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool ExistsWithin(string root, string path) =>
        SafeOriginal(root, path) && Safety.ContainedRegularSource(root, path);

    public static IReadOnlyList<Row> Inspect(string selectedRoot)
    {
        string root = Safety.ValidateRoot(selectedRoot);
        string journal = Path.Combine(root, JournalFolder);
        if (!Directory.Exists(journal)) return Array.Empty<Row>();
        if ((File.GetAttributes(journal) &
            (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline)) != 0)
            return new[] { Warning("", "復旧記録フォルダに特殊属性があります。手動確認してください。") };

        var results = new List<Row>();
        string[] allFiles;
        try { allFiles = Directory.EnumerateFiles(journal).Take(16385).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new[] { Warning("", "復旧記録の一覧を開けません。手動確認してください。") };
        }
        if (allFiles.Length > 16384)
            return new[] { Warning("", "復旧記録が多すぎるため読み取りを停止しました。") };
        if (allFiles.Any(f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
            results.Add(Warning("", "書込途中の .tmp 記録があります。途中停止・要手動確認。"));

        var intents = allFiles.Where(f =>
        {
            string name = Path.GetFileName(f);
            return name.Length == 44 &&
                name.EndsWith(".intent.json", StringComparison.Ordinal) &&
                Guid.TryParseExact(name[..32], "N", out _);
        }).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();

        if (intents.Length > MaxGroups)
            return new[] { Warning("", "保留操作が多すぎるため一覧表示を停止しました。") };
        foreach (string intentFile in intents)
        {
            string id = Path.GetFileName(intentFile)[..32];
            try
            {
                var attrs = File.GetAttributes(intentFile);
                if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory |
                    FileAttributes.Offline)) != 0 ||
                    new FileInfo(intentFile).Length > MaxIntentBytes)
                    throw new InvalidDataException("Unsafe or oversized intent");
                var intent = JsonSerializer.Deserialize<DuplicateCleanupWorkflow.Intent>(
                    File.ReadAllBytes(intentFile));
                if (intent is null || intent.Schema != 1 || intent.Id != id ||
                    !intent.Root.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    intent.Candidates is null || intent.Candidates.Count is < 1 or > 32 ||
                    intent.GroupHash.Length != 64 || !intent.GroupHash.All(Uri.IsHexDigit) ||
                    !SafeOriginal(root, intent.Keeper.Path) ||
                    !SafeOriginal(root, Path.Combine(root, intent.ReportRelative)) ||
                    !SafeOriginal(root, Path.Combine(root, intent.CollectionRelative)) ||
                    intent.Candidates.Any(c => c is null ||
                        !SafeOriginal(root, c.SourcePath) ||
                        !SafeOriginal(root, c.DestinationPath) ||
                        c.Original.Path != c.SourcePath ||
                        !string.Equals(Path.GetRelativePath(root, c.SourcePath),
                            c.SourceRelative, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(Path.GetRelativePath(root, c.DestinationPath),
                            c.DestinationRelative, StringComparison.OrdinalIgnoreCase) ||
                        !c.DestinationRelative.StartsWith(intent.CollectionRelative +
                            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Invalid intent metadata");

                for (int i = 0; i < intent.Candidates.Count; i++)
                {
                    if (results.Count >= MaxTotalRows)
                    {
                        results.Add(Warning("", "表示上限に達しました。残りは要手動確認。"));
                        return results;
                    }
                    var c = intent.Candidates[i];
                    string prefix = id + ".item-" + i.ToString("D4") + ".";
                    bool stagingEvidence = allFiles.Any(x =>
                        Path.GetFileName(x).Equals(prefix + "staged.json",
                            StringComparison.OrdinalIgnoreCase));
                    bool restoreStarted = allFiles.Any(x =>
                        Path.GetFileName(x).Equals(prefix + "restore-intent.json",
                            StringComparison.OrdinalIgnoreCase));
                    bool restoreReceipt = allFiles.Any(x =>
                        Path.GetFileName(x).Equals(prefix + "restored.json",
                            StringComparison.OrdinalIgnoreCase));
                    bool recycleStarted = allFiles.Any(x =>
                        Path.GetFileName(x).Equals(prefix + "recycle-intent.json",
                            StringComparison.OrdinalIgnoreCase));
                    bool recycleReceipt = allFiles.Any(x =>
                        Path.GetFileName(x).Equals(prefix + "recycled.json",
                            StringComparison.OrdinalIgnoreCase));
                    bool originalPresent = ExistsWithin(root, c.SourcePath);
                    bool stagedPresent = ExistsWithin(root, c.DestinationPath);
                    string state =
                        (originalPresent && stagedPresent) ||
                        (restoreStarted && recycleStarted)
                            ? "異常・要手動確認"
                            : restoreReceipt
                                ? originalPresent && !stagedPresent
                                    ? "元パス復帰の記録あり（ID未照合）"
                                    : "復帰記録とファイル位置が矛盾"
                            : restoreStarted
                                ? "復帰途中・要手動確認"
                            : recycleReceipt
                                ? "ごみ箱相当の記録あり（実ごみ箱とは限らない）"
                            : recycleStarted
                                ? "ごみ箱処理開始の記録あり・要手動確認"
                            : stagingEvidence && stagedPresent && !originalPresent
                                ? "保留先に存在（ID未照合）"
                            : originalPresent && !stagedPresent
                                ? "元パスに存在（ID未照合）"
                            : "所在不明・要手動確認";
                    results.Add(new Row(id, i, c.SourcePath, c.DestinationPath,
                        intent.Keeper.Path, state,
                        "この履歴はファイル名と記録の一覧表示のみ。元ファイルID・SHA・ACLは未再検査。削除や復元の許可ではありません。"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException or NullReferenceException or
                InvalidOperationException or NotSupportedException)
            {
                results.Add(Warning(id, "記録が不正・破損または読み取れません。手動確認してください。"));
            }
        }
        if (intents.Length == 0 && results.Count == 0)
            results.Add(Warning("", "有効な重複整理の保留記録はありません。"));
        return results;
    }

    private static Row Warning(string id, string reason) =>
        new(id, -1, "", "", "", "要手動確認", reason);
}
