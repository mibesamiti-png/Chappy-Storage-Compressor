namespace ChappyStorageCompressor.Core;

/// <summary>
/// Recovery is deliberately conservative. Inspecting never modifies user files;
/// applying a recovery requires a fresh classification and an explicit choice.
/// </summary>
public enum RecoveryAction
{
    ManualReview,
    RestoreIndependentBackup,
    UndoNewHardLink,
    ClearVerifiedJournal,
    MalformedRecord
}

public sealed class RecoveryCase
{
    public required string OperationId { get; init; }
    public required string OriginalPath { get; init; }
    public required string BackupPath { get; init; }
    public required string MasterPath { get; init; }
    public required RecoveryAction Action { get; init; }
    public required string Explanation { get; init; }
    public bool CanApply => Action is RecoveryAction.RestoreIndependentBackup or
        RecoveryAction.UndoNewHardLink or RecoveryAction.ClearVerifiedJournal;

    public string ActionText => Action switch
    {
        RecoveryAction.RestoreIndependentBackup => "退避ファイルを元の名前へ戻せる",
        RecoveryAction.UndoNewHardLink => "共有リンクを解除し元の独立ファイルへ戻せる",
        RecoveryAction.ClearVerifiedJournal => "内容一致を検証済み・記録のみ整理可能",
        RecoveryAction.MalformedRecord => "記録が壊れている・操作禁止",
        _ => "手動確認が必要・操作禁止"
    };
    public string DisplayName => Path.GetFileName(OriginalPath);
}

public static class RecoveryService
{
    private static readonly object MutationGate = new();

    public static List<RecoveryCase> InspectAll(IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<RecoveryCase>();
        foreach (string path in Journal.UnresolvedRecordFiles())
        {
            ct.ThrowIfCancellationRequested();
            results.Add(new RecoveryCase
            {
                OperationId = "(不正な記録)", OriginalPath = path, BackupPath = "", MasterPath = "",
                Action = RecoveryAction.MalformedRecord,
                Explanation = "記録の構造が不完全、または書き込み途中です。変更・削除せず、記録と実ファイルを手動調査してください。"
            });
        }
        foreach (var p in Journal.Pending())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("復旧状態を検証中: " + p.OriginalPath);
            results.Add(Inspect(p, ct));
        }
        return results.OrderBy(r => r.CanApply ? 1 : 0).ThenBy(r => r.OriginalPath).ToList();
    }

    public static RecoveryCase Inspect(PendingOperation p, CancellationToken ct = default)
    {
        RecoveryCase Make(RecoveryAction action, string explanation) => new()
        {
            OperationId = p.OperationId, OriginalPath = p.OriginalPath,
            BackupPath = p.BackupPath, MasterPath = p.MasterPath,
            Action = action, Explanation = explanation
        };
        try
        {
            ct.ThrowIfCancellationRequested();
            ValidateContext(p);
            bool originalExists = File.Exists(p.OriginalPath);
            bool backupExists = File.Exists(p.BackupPath);

            if (!originalExists && !backupExists)
                return Make(RecoveryAction.ManualReview, "元パスと退避ファイルが両方見つかりません。自動復元はできません。");

            if (!originalExists && backupExists)
            {
                if (IndependentBackupMatches(p, ct))
                    return Make(RecoveryAction.RestoreIndependentBackup,
                        "独立した退避ファイルのSHA-256が記録と一致しました。元パスが空なので退避名から復元できます。");
                return Make(RecoveryAction.ManualReview,
                    "退避ファイルの内容・属性・リンク数が安全条件を満たしません。変更しません。");
            }

            if (originalExists && !backupExists)
            {
                // This is either before the first rename, or after the backup has
                // already been removed. Never delete any content in this case.
                if (NormalFileMatches(p.OriginalPath, p.Sha256, ct))
                    return Make(RecoveryAction.ClearVerifiedJournal,
                        "元パスが存在し内容のSHA-256が一致します。ファイルには触れず、記録のみ完了扱いにできます。");
                return Make(RecoveryAction.ManualReview,
                    "元パスは存在しますが記録された内容と一致しません。変更しません。");
            }

            // BOTH paths present after a crash: do not automatically delete the
            // independently preserved backup. A user may explicitly choose to
            // undo the *newly created link* once its identity is proven.
            if (IndependentBackupMatches(p, ct) && File.Exists(p.MasterPath) &&
                (p.MasterIdentity is null || NativeFile.GetIdentity(p.MasterPath).SameFile(p.MasterIdentity)) &&
                NormalFileMatches(p.MasterPath, p.Sha256, ct) &&
                NormalFileMatches(p.OriginalPath, p.Sha256, ct) &&
                NativeFile.SameFile(p.OriginalPath, p.MasterPath) &&
                !NativeFile.SameFile(p.BackupPath, p.MasterPath) &&
                Safety.ByteEqual(p.BackupPath, p.MasterPath, ct))
            {
                return Make(RecoveryAction.UndoNewHardLink,
                    "元パスは統合先と同じ実体、退避ファイルは独立した完全一致データです。" +
                    "確認後、元パスのリンクだけを外して退避ファイルを戻せます。統合先の代表データは削除しません。");
            }
            return Make(RecoveryAction.ManualReview,
                "元パスと退避ファイルが両方ありますが、内容または実体関係を安全に証明できません。どちらも残します。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Make(RecoveryAction.ManualReview, "検証失敗。ファイルを変更しません: " + ex.Message);
        }
    }

    /// <summary>
    /// Applies only the exact action selected from the recovery preview.
    /// The on-disk record and all file conditions are rechecked immediately.
    /// </summary>
    public static string Apply(string operationId, RecoveryAction expected,
        string? expectedOriginalPath = null, string? expectedMasterPath = null)
    {
        if (expected is not (RecoveryAction.RestoreIndependentBackup or
            RecoveryAction.UndoNewHardLink or RecoveryAction.ClearVerifiedJournal))
            throw new InvalidOperationException("安全に実行できる復旧操作ではありません。");

        lock (MutationGate)
        {
            using var mutationLease = MutationLease.Acquire();
            var p = Journal.Pending().SingleOrDefault(x => x.OperationId == operationId)
                ?? throw new InvalidOperationException("復旧記録が見つかりません。再読み込みしてください。");
            if (expectedOriginalPath is not null &&
                !Path.GetFullPath(p.OriginalPath).Equals(Path.GetFullPath(expectedOriginalPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("プレビュー後に復旧記録の元パスが変更されました。");
            if (expectedMasterPath is not null &&
                !Path.GetFullPath(p.MasterPath).Equals(Path.GetFullPath(expectedMasterPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("プレビュー後に復旧記録の統合先が変更されました。");
            // Never proceed while any malformed record exists. This is
            // intentionally stricter than the preview scan.
            if (Journal.UnresolvedRecordFiles().Count > 0)
                throw new InvalidOperationException("破損/未完成の復旧記録があり、操作できません。");
            ValidateContext(p);

            // Holding an open read handle with shared-delete denies concurrent
            // writers for the duration of the path-replacement operation.
            if (expected == RecoveryAction.RestoreIndependentBackup)
            {
                using var guard = File.Open(p.BackupPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete);
                RequireFresh(p, expected);
                File.Move(p.BackupPath, p.OriginalPath); // Never overwrite an existing path.
                Journal.Completed(p.OperationId);
                return "退避ファイルを元パスへ復元しました。";
            }

            if (expected == RecoveryAction.UndoNewHardLink)
            {
                using var backupGuard = File.Open(p.BackupPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var masterGuard = File.Open(p.MasterPath, FileMode.Open,
                    FileAccess.Read, FileShare.Read | FileShare.Delete);
                RequireFresh(p, expected);
                // Only the path known to be a link to master is removed.
                // Failure between these two calls leaves the verified independent
                // backup intact and the journal pending for a later restore.
                File.Delete(p.OriginalPath);
                FaultInjection.Hit("recovery-link-removed"); // test-only simulated power loss
                File.Move(p.BackupPath, p.OriginalPath);
                Journal.Completed(p.OperationId);
                return "共有リンクを解除し、独立した退避ファイルを元のパスに戻しました。";
            }

            RequireFresh(p, RecoveryAction.ClearVerifiedJournal);
            Journal.Completed(p.OperationId);
            return "元ファイルには触れず、検証済みの復旧記録だけを整理しました。";
        }
    }

    private static void RequireFresh(PendingOperation p, RecoveryAction wanted)
    {
        var inspected = Inspect(p, CancellationToken.None);
        if (inspected.Action != wanted)
            throw new InvalidOperationException("ファイルの状態が画面確認時から変化しました。" +
                "操作は中止しました。状態を再取得してください。現在: " + inspected.ActionText);
    }

    private static bool IndependentBackupMatches(PendingOperation p, CancellationToken ct)
    {
        var file = Safety.Capture(p.BackupPath);
        return file.Identity.LinkCount == 1 &&
            (p.IndependentIdentity is null || file.Identity.SameFile(p.IndependentIdentity)) &&
            string.Equals(Safety.ComputeSha256(p.BackupPath, ct), p.Sha256, StringComparison.OrdinalIgnoreCase) &&
            Safety.SameSnapshot(file);
    }

    private static bool NormalFileMatches(string path, string hash, CancellationToken ct)
    {
        var file = Safety.Capture(path);
        return string.Equals(Safety.ComputeSha256(path, ct), hash, StringComparison.OrdinalIgnoreCase) &&
            Safety.SameSnapshot(file);
    }

    private static void ValidateContext(PendingOperation p)
    {
        if (!Guid.TryParseExact(p.OperationId, "N", out _) || p.Sha256.Length != 64 ||
            !p.Sha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException("復旧記録の識別子・ハッシュが不正です。");
        var store = Path.GetDirectoryName(Path.GetFullPath(p.MasterPath))
            ?? throw new InvalidOperationException("統合先フォルダを特定できません。");
        if (!Path.GetFileName(store).Equals(Safety.StoreName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("統合先が専用フォルダではありません。");
        var root = Safety.ValidateRoot(Path.GetDirectoryName(store)!);
        if (!store.Equals(Safety.StorePath(root), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(store) ||
            (File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("統合先フォルダが変更・削除されています。");
        CheckChild(p.OriginalPath, root);
        CheckChild(p.BackupPath, root);
        if (!Path.GetFullPath(p.BackupPath).Equals(
            Path.GetFullPath(p.OriginalPath + ".chappy-backup-" + p.OperationId),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("退避ファイル名が復旧記録と一致しません。");
    }

    private static void CheckChild(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Split(Path.DirectorySeparatorChar).Any(s =>
                s.Equals(Safety.StoreName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("記録が選択ルートの外を参照しています。");
        for (var dir = Path.GetDirectoryName(full); dir is not null &&
             !dir.Equals(root, StringComparison.OrdinalIgnoreCase); dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("復旧パスの親にリンク/不存在のフォルダがあります。");
        }
    }
}
