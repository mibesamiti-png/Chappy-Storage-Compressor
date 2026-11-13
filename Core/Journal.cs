using System.Text.Json;

namespace ChappyStorageCompressor.Core;

public static class Journal
{
    // Test-only override, set by the isolated smoke-test assembly before any IO.
    internal static string? TestFolderPath { get; set; }
    public static string FolderPath => TestFolderPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChappyStorageCompressor", "pending");

    private static string PathFor(string id) => Path.Combine(FolderPath, id + ".json");

    public static void Add(PendingOperation operation)
    {
        Directory.CreateDirectory(FolderPath);
        string path = PathFor(operation.OperationId);
        string temp = path + ".tmp";
        byte[] contents = JsonSerializer.SerializeToUtf8Bytes(operation, new JsonSerializerOptions { WriteIndented = true });
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
    }

    public static void Completed(string id)
    {
        string path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }

    // Recovery journals are security-sensitive: never perform filesystem
    // operations using a malformed record or a modified record filename.
    private static bool ValidRecord(PendingOperation? r, string journalFile)
    {
        if (r is null || r.OperationId is null || r.OriginalPath is null ||
            r.BackupPath is null || r.MasterPath is null || r.Sha256 is null)
            return false;
        if (!Guid.TryParseExact(r.OperationId, "N", out _) ||
            !string.Equals(Path.GetFileNameWithoutExtension(journalFile), r.OperationId,
                StringComparison.OrdinalIgnoreCase)) return false;
        if (r.Sha256.Length != 64 || !r.Sha256.All(Uri.IsHexDigit)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(r.OriginalPath) ||
                !Path.IsPathFullyQualified(r.BackupPath) ||
                !Path.IsPathFullyQualified(r.MasterPath)) return false;
            if (!string.Equals(Path.GetFullPath(r.BackupPath),
                Path.GetFullPath(r.OriginalPath + ".chappy-backup-" + r.OperationId),
                StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(Path.GetPathRoot(r.OriginalPath), Path.GetPathRoot(r.MasterPath),
                StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(r.MasterPath)),
                Safety.StoreName, StringComparison.OrdinalIgnoreCase)) return false;
            if (r.RecordedUtc == default) return false;
            return true;
        }
        catch { return false; }
    }

    public static List<string> UnresolvedRecordFiles()
    {
        var paths = new List<string>();
        if (!Directory.Exists(FolderPath)) return paths;
        // Even a partially written *.tmp file means the prior operation cannot
        // be declared clean without an explicit inspection.
        paths.AddRange(Directory.EnumerateFiles(FolderPath, "*.tmp"));
        foreach (var path in Directory.EnumerateFiles(FolderPath, "*.json"))
        {
            try
            {
                if (!ValidRecord(JsonSerializer.Deserialize<PendingOperation>(File.ReadAllText(path)), path))
                    paths.Add(path);
            }
            catch { paths.Add(path); }
        }
        return paths;
    }

    public static void AssertCleanBeforeMerge()
    {
        if (UnresolvedRecordFiles().Count != 0 || Pending().Count != 0)
            throw new InvalidOperationException("復旧待ち/破損した操作記録があります。新しい統合は開始できません。");
    }

    public static List<PendingOperation> Pending()
    {
        var result = new List<PendingOperation>();
        if (!Directory.Exists(FolderPath)) return result;
        foreach (var file in Directory.EnumerateFiles(FolderPath, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<PendingOperation>(File.ReadAllText(file));
                if (ValidRecord(record, file)) result.Add(record!);
            }
            catch { /* 構文不正の記録は削除せず、手動調査用に残す。 */ }
        }
        return result;
    }

    public static List<string> RecoverSafely()
    {
        using var mutationLease = MutationLease.Acquire();
        var log = new List<string>();
        foreach (var file in UnresolvedRecordFiles())
            log.Add("要手動確認（不完全な操作記録。削除しないでください）: " + file);
        foreach (var p in Pending())
        {
            try
            {
                // Only restore a verified backup. Never overwrite an existing path.
                if (File.Exists(p.BackupPath) && !File.Exists(p.OriginalPath))
                {
                    if (!string.Equals(Safety.ComputeSha256(p.BackupPath, CancellationToken.None),
                        p.Sha256, StringComparison.Ordinal))
                    {
                        log.Add("復旧保留（退避内容が記録と不一致）: " + p.BackupPath);
                        continue;
                    }
                    File.Move(p.BackupPath, p.OriginalPath);
                    Completed(p.OperationId);
                    log.Add("退避ファイルを元の場所へ復元: " + p.OriginalPath);
                }
                else if (File.Exists(p.BackupPath) && File.Exists(p.OriginalPath))
                {
                    log.Add("要手動確認（元パスと退避パスが両方存在。どちらも削除しない）: " +
                        p.OriginalPath + " / " + p.BackupPath);
                }
                else if (!File.Exists(p.BackupPath) && File.Exists(p.OriginalPath))
                {
                    // Two possible crash points: before the rename, or after successful
                    // deletion. In either case, only clear a record when original bytes
                    // match the recorded value. No irreversible changes are made here.
                    if (string.Equals(Safety.ComputeSha256(p.OriginalPath, CancellationToken.None),
                        p.Sha256, StringComparison.Ordinal))
                    {
                        Completed(p.OperationId);
                        log.Add("元パスの内容一致を確認。復旧記録を完了扱い: " + p.OriginalPath);
                    }
                    else
                        log.Add("要手動確認（元パスの内容が記録と不一致）: " + p.OriginalPath);
                }
                else
                {
                    log.Add("要手動確認（元パス・退避ファイルが両方見つからない）: " +
                        p.OriginalPath + " / " + p.BackupPath);
                }
            }
            catch (Exception e) { log.Add("復旧保留: " + p.OriginalPath + " — " + e.Message); }
        }
        if (log.Count == 0) log.Add("復旧が必要な記録はありません。");
        return log;
    }
}
