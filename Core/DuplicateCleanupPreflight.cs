using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Product-visible, strictly read-only verification of a previously scanned
/// duplicate group and its planned top-level collision collection. This is a
/// LAST LOOK for the user's review, not transaction consent or authorization:
/// time-of-check/time-of-use is still open, and production mutations stay
/// compiled out until independent acceptance and explicit release approval.
/// </summary>
public static class DuplicateCleanupPreflight
{
    public const bool CanModifyUserFiles = false;

    public sealed record CandidateResult(
        string OriginalPath, bool CheckedByUser,
        bool VerifiedNow, string Reason);

    public sealed record Report(
        bool Passed, string KeeperPath, bool KeeperVerified,
        int CheckedCount, int VerifiedCount,
        IReadOnlyList<CandidateResult> Candidates, string Summary)
    {
        public bool CanMoveOrDelete => false;
    }

    private static Report Fail(DuplicateCleanupPreview.Result plan,
        HashSet<string> selected, string problem)
    {
        var states = plan.DeleteCandidates.Select(item =>
            new CandidateResult(item.OriginalPath, selected.Contains(item.OriginalPath),
                false, "未検証：" + problem)).ToArray();
        return new Report(false, plan.KeeperPath, false, selected.Count,
            0, states, "安全停止：" + problem +
            "。変更は一切行っていません。フォルダを再スキャンしてください。");
    }

    private static string? ValidateOne(FileSnapshot scanned,
        string root, string expectedHash, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            string path = scanned.Path;
            if (!Safety.ContainedRegularSource(root, path))
                return "対象フォルダの外、または安全対象外です";
            FileSnapshot actual = Safety.Capture(path);
            if (!actual.Identity.SameFile(scanned.Identity) ||
                actual.Identity.LinkCount != 1 ||
                actual.Length != scanned.Length ||
                actual.CreationUtc != scanned.CreationUtc ||
                actual.ModifiedUtc != scanned.ModifiedUtc ||
                actual.Attributes != scanned.Attributes ||
                !actual.SecurityDescriptor.AsSpan().SequenceEqual(
                    scanned.SecurityDescriptor))
                return "ファイルID・サイズ・日時・属性・権限がスキャン時から変更されています";
            if (!string.Equals(Safety.ComputeSha256(path, ct),
                    expectedHash, StringComparison.OrdinalIgnoreCase))
                return "スキャン時と内容ハッシュが異なります";
            if (!Safety.SameSnapshot(actual))
                return "再照合中にファイルが変更されました";
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or ArgumentException or
            NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return "安全確認に失敗：" + ex.GetType().Name;
        }
    }

    /// <summary>
    /// Check the whole exact-match group against fresh captures, full SHA-256
    /// and bytes. Also reject any changed destination collision/renumbering.
    /// CheckedByUser is a transient UI filter, never durable permission.
    /// </summary>
    public static Report Check(DuplicateCleanupPreview.Result plan,
        DuplicateGroup scannedGroup, IReadOnlyCollection<string> checkedPaths,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(scannedGroup);
        ArgumentNullException.ThrowIfNull(checkedPaths);
        var selected = new HashSet<string>(checkedPaths, StringComparer.OrdinalIgnoreCase);
        ct.ThrowIfCancellationRequested();

        if (plan.CanModifyUserFiles || selected.Count != checkedPaths.Count ||
            !selected.IsSubsetOf(plan.DeleteCandidates.Select(c => c.OriginalPath)) ||
            plan.DeleteCandidates.Count < 1 ||
            scannedGroup.Files.Count != plan.DeleteCandidates.Count + 1 ||
            !string.Equals(plan.GroupSha256, scannedGroup.Hash,
                StringComparison.OrdinalIgnoreCase))
            return Fail(plan, selected, "候補またはグループが一致しません");

        // Validate full scan-set membership: a compromised or stale snapshot
        // cannot silently become authority merely because paths look alike.
        var scanned = scannedGroup.Files.ToDictionary(
            f => f.Path, StringComparer.OrdinalIgnoreCase);
        if (scanned.Count != scannedGroup.Files.Count ||
            !scanned.ContainsKey(plan.KeeperPath) ||
            plan.DeleteCandidates.Any(c => !scanned.ContainsKey(c.OriginalPath)))
            return Fail(plan, selected, "検索時のファイル一覧が一致しません");

        string root;
        try
        {
            root = Safety.ValidateRoot(plan.Root);
            var currentPlan = DuplicateCleanupPreview.Inspect(
                root, scannedGroup, plan.KeeperPath);
            if (!string.Equals(currentPlan.KeeperPath, plan.KeeperPath,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentPlan.KeeperRelativePath, plan.KeeperRelativePath,
                    StringComparison.OrdinalIgnoreCase) ||
                currentPlan.LogicalCandidateBytes != plan.LogicalCandidateBytes ||
                !string.Equals(currentPlan.CollectionRelativePath,
                    plan.CollectionRelativePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentPlan.ReportRelativePath,
                    plan.ReportRelativePath, StringComparison.OrdinalIgnoreCase) ||
                currentPlan.DeleteCandidates.Count != plan.DeleteCandidates.Count ||
                currentPlan.DeleteCandidates.Where((item, index) =>
                    !string.Equals(item.OriginalPath,
                        plan.DeleteCandidates[index].OriginalPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.ProposedCollectionRelativePath,
                        plan.DeleteCandidates[index].ProposedCollectionRelativePath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.OriginalRelativePath,
                        plan.DeleteCandidates[index].OriginalRelativePath,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.OriginalBytes != plan.DeleteCandidates[index].OriginalBytes ||
                    !string.Equals(item.Reason,
                        plan.DeleteCandidates[index].Reason, StringComparison.Ordinal)).Any())
                return Fail(plan, selected, "整理先・連番・一覧メモの予定が変わりました");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException or
            NotSupportedException)
        {
            return Fail(plan, selected,
                "整理先の再確認ができません：" + ex.GetType().Name);
        }

        string? keeperError = ValidateOne(scanned[plan.KeeperPath], root,
            plan.GroupSha256, ct);
        if (keeperError != null)
            return Fail(plan, selected, "残すファイル：" + keeperError);

        FileSnapshot keeper = scanned[plan.KeeperPath];
        var rows = new List<CandidateResult>(plan.DeleteCandidates.Count);
        int verified = 0;
        foreach (var candidate in plan.DeleteCandidates)
        {
            ct.ThrowIfCancellationRequested();
            FileSnapshot source = scanned[candidate.OriginalPath];
            string? error = ValidateOne(source, root, plan.GroupSha256, ct);
            if (error is null)
            {
                if (source.Identity.SameFile(keeper.Identity) ||
                    source.Identity.VolumeSerial != keeper.Identity.VolumeSerial)
                    error = "既存ハードリンクまたは別ボリュームです";
                else
                {
                    try
                    {
                        if (!Safety.ByteEqual(source.Path, keeper.Path, ct))
                            error = "残すファイルと全バイトが一致しません";
                        else if (!Safety.SameSnapshot(source) ||
                            !Safety.SameSnapshot(keeper))
                            error = "照合中にファイルの状態が変わりました";
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidDataException or InvalidOperationException or ArgumentException)
                    {
                        error = "全バイト比較ができません：" + ex.GetType().Name;
                    }
                }
            }
            if (error is null) verified++;
            rows.Add(new CandidateResult(candidate.OriginalPath,
                selected.Contains(candidate.OriginalPath),
                error is null, error ?? "ファイルID・属性・ハッシュ・全バイト一致"));
        }
        ct.ThrowIfCancellationRequested();
        // An attacker can modify the keeper during the last candidate hash.
        // This final read-only check reduces, but does not eliminate, TOCTOU.
        keeperError = ValidateOne(keeper, root, plan.GroupSha256, ct);
        if (keeperError != null)
            return Fail(plan, selected, "最後の残すファイル再確認：" + keeperError);

        bool passed = verified == rows.Count;
        string summary = passed
            ? $"直前再照合合格：全{rows.Count}候補、選択済み{selected.Count}件、" +
              "残すファイルを再確認。削除・移動・ごみ箱操作は一切行っていません。" +
              " この結果は将来の変更操作の許可・保証ではありません。"
            : $"安全停止：{rows.Count - verified}件が不一致。" +
              " 原本は変更せず、再スキャンが必要です。";
        return new Report(passed, plan.KeeperPath, true,
            selected.Count, verified, rows, summary);
    }
}
