namespace ChappyStorageCompressor.Core;

/// <summary>
/// Production read-only dry-run of the actual Scanner -> duplicate-first ->
/// one representative image candidate -> later independent-Undo order.
/// Nothing in this class creates, deletes or links media. This is NOT permission
/// to invoke the test-only DedupeEngine or a production cutover.
/// </summary>
public static class DedupeFirstWorkflowPlan
{
    public sealed record GroupPreview(string Representative, string Sha256,
        int FileCount, long DuplicateBytes, bool ImageCandidateRepresentative,
        bool CanConsolidate, string Reason);

    public sealed record Summary(int EligibleGroups, int BlockedGroups,
        int DuplicateAliases, int ImageRepresentatives, int RepeatImageCandidatesAvoided,
        long EstimatedDuplicateBytes, long UndoAdditionalBytesApprox,
        long ActualFreedBytes, bool ProductionMutationEnabled,
        IReadOnlyList<GroupPreview> Groups)
    {
        public string Describe()
        {
            return "【読み取り専用：重複→代表1件だけ圧縮→独立Undo の事前計画】\n" +
                $"再照合した統合可能グループ: {EligibleGroups:N0} 件 / 要確認 {BlockedGroups:N0} 件\n" +
                $"重複する別名: {DuplicateAliases:N0} 件\n" +
                $"PNG/JPEG代表候補: {ImageRepresentatives:N0} 件（重複候補 {RepeatImageCandidatesAvoided:N0} 件分を回避見込）\n" +
                $"重複統合による論理削減見込: {GroupRow.HumanSize(EstimatedDuplicateBytes)}\n" +
                $"独立Undoに再度必要な容量の概算: {GroupRow.HumanSize(UndoAdditionalBytesApprox)}\n" +
                "実際に削減した容量: 0 B\n\n" +
                "※ 統合・圧縮原本置換・Undoはまだ実行されていません。\n" +
                "※ 保存済み完了台帳と本番Undo安全ゲートが未完成のため、本番統合は無効です。\n" +
                "※ ここに示す値は見積りであり、ファイルの同時変更・実物理割当を保証しません。";
        }
    }

    public static Summary Inspect(ScanResult scan, bool allowTimestampDifference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scan);
        string root = Safety.ValidateRoot(scan.Root);
        ct.ThrowIfCancellationRequested();
        // Read-only global recovery gate: never produce a seemingly safe plan
        // while a prior transaction remains unresolved or malformed.
        Journal.AssertCleanBeforeMerge();
        int eligible = 0, blocked = 0, aliases = 0, imageGroups = 0, repeatedImages = 0;
        long savings = 0;
        var result = new List<GroupPreview>();
        // Keep all byte-exact scan findings, including ineligible singleton
        // partitions, while re-evaluating safe metadata-compatible subsets.
        foreach (DuplicateGroup group in scan.Groups.SelectMany(g =>
            g.PartitionForSafeMerge(allowTimestampDifference)))
        {
            ct.ThrowIfCancellationRequested();
            bool image = group.Files.Count != 0 && group.Files.All(f =>
                Path.GetExtension(f.Path).Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(f.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(f.Path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
            string reason = group.BlockReason(allowTimestampDifference) ?? "";
            // Never follow a supplied ScanResult to arbitrary paths, even if it
            // was constructed by an internal caller rather than Scanner.Run.
            if (reason.Length == 0)
            {
                var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in group.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    string full = Path.GetFullPath(item.Path);
                    if (!Safety.ContainedRegularSource(root, full) ||
                        !known.Add(full) ||
                        !Safety.SameSnapshot(item) ||
                        !string.Equals(Safety.ComputeSha256(full, ct), group.Hash,
                            StringComparison.OrdinalIgnoreCase) ||
                        !Safety.ByteEqual(group.Representative.Path, full, ct))
                    {
                        reason = "パス・ファイルID・属性・内容がスキャン後に変更されました";
                        break;
                    }
                }
            }
            bool valid = reason.Length == 0;
            if (valid)
            {
                eligible++;
                aliases = checked(aliases + group.Files.Count - 1);
                savings = checked(savings + group.Savings);
                if (image)
                {
                    imageGroups++;
                    repeatedImages = checked(repeatedImages + group.Files.Count - 1);
                }
            }
            else blocked++;
            result.Add(new GroupPreview(
                group.Files.Count == 0 ? "(no files)" :
                    Path.GetRelativePath(root, group.Representative.Path),
                group.Hash, group.Files.Count, valid ? group.Savings : 0,
                valid && image, valid, valid ? "再検証済み（未実行）" : reason));
        }
        ct.ThrowIfCancellationRequested();
        return new Summary(eligible, blocked, aliases, imageGroups,
            repeatedImages, savings, savings, 0, false, result);
    }
}
