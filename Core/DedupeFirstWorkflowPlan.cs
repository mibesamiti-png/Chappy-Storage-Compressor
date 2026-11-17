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

    // B2 G2-07: explicit selected-row review shares the same immutable
    // FileSnapshot / NTFS ID / full-byte checks as the internal Core, but
    // has NO mutation entrypoint. A user confirming this text still cannot
    // call MergeVerifiedGroups; G3/G5 release gates remain authoritative.
    public sealed record SelectedGroupReview(string Representative,
        IReadOnlyList<string> Paths, string Reason, long PotentialSavingBytes)
    {
        public bool Verified => Reason == "照合一致（未実行）";
    }

    public sealed record SelectedReview(string Root,
        IReadOnlyList<SelectedGroupReview> Groups, long LogicalSavingBytes,
        long UndoAdditionalBytesApprox, long TargetAvailableBytes,
        long JournalAvailableBytes, bool JournalClean, bool HistoryClean,
        bool CapacityEnough)
    {
        public bool VerifiedForPreview => Groups.Count > 0 &&
            Groups.All(x => x.Verified) && JournalClean &&
            HistoryClean && CapacityEnough;

        public bool ProductionMergeAuthorized => false;

        public string Describe()
        {
            var lines = new List<string>
            {
                "【選択した重複グループの最終確認・読み取り専用】",
                "対象フォルダ: " + Root,
                $"明示選択したグループ: {Groups.Count:N0}件 / 検証済み: {Groups.Count(x => x.Verified):N0}件",
                "推定論理削減量: " + GroupRow.HumanSize(LogicalSavingBytes),
                "独立Undoに必要な追加容量の概算: " +
                    GroupRow.HumanSize(UndoAdditionalBytesApprox),
                "対象ドライブの確認時空き: " +
                    GroupRow.HumanSize(TargetAvailableBytes),
                "復旧記録ドライブの確認時空き: " +
                    GroupRow.HumanSize(JournalAvailableBytes),
                "未解決Journal: " + (JournalClean ? "なし" : "あり・操作禁止"),
                "未完了グループ履歴: " + (HistoryClean ? "なし" : "あり・操作禁止"),
                "容量下限: " + (CapacityEnough ? "確認時は条件一致" : "不足・操作禁止"),
                ""
            };
            foreach (var g in Groups)
            {
                lines.Add("代表: " + g.Representative + " / " + g.Reason);
                foreach (string path in g.Paths)
                    lines.Add("  ・" + path);
            }
            lines.Add("");
            lines.Add("【重要】共有化後、片方のファイル内容を直接編集すると別名の内容も同時に変わり得ます。");
            lines.Add("Undoでは再び独立したバイト列が必要となり、削減量と同程度の追加容量が必要です。");
            lines.Add("保存データは必ず別ドライブ等にバックアップしてください。");
            lines.Add("同意後も再照合し、記録・パス・元NTFS ID・SHAが変化した場合は拒否します。");
            lines.Add("※ 表示値は実空き容量の予約でも物理削減実測でもありません。");
            lines.Add("※ この製品ビルドはG3/G5安全ゲート未完のためMerge/Undoを実行できません。");
            lines.Add("実際の変更・回復容量: 0 B");
            return string.Join("\n", lines);
        }
    }

    public static SelectedReview InspectSelected(string inputRoot,
        IReadOnlyList<DuplicateGroup> selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        string root = Safety.ValidateRoot(inputRoot);
        ct.ThrowIfCancellationRequested();
        bool journalClean = Journal.Pending().Count == 0 &&
            Journal.UnresolvedRecordFiles().Count == 0;
        bool historyClean = CompletedGroupLedger.Inspect(root).Entries.All(x =>
            x.State is "VerifiedCompleted" or "VerifiedRestored");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedGroups = new List<SelectedGroupReview>();
        long savings = 0;
        foreach (DuplicateGroup group in selected)
        {
            ct.ThrowIfCancellationRequested();
            string reason = group.BlockReason(false) ?? "";
            var shownPaths = group.Files.Select(f => f.Path).ToArray();
            if (group.Files.Count is < 2 or > 33)
                reason = "対象件数が許可範囲外（2〜33件）";
            if (reason.Length == 0)
            {
                try
                {
                    string master = Path.Combine(Safety.StorePath(root), group.Hash +
                        Path.GetExtension(group.Representative.Path).ToLowerInvariant());
                    if (!masters.Add(master) || File.Exists(master))
                        reason = "統合先の重複/既存ハードリンク";
                    foreach (FileSnapshot file in group.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        string full = Path.GetFullPath(file.Path);
                        if (!paths.Add(full) ||
                            !Safety.ContainedRegularSource(root, full) ||
                            !Safety.SameSnapshot(file) ||
                            !string.Equals(Safety.ComputeSha256(full, ct),
                                group.Hash, StringComparison.OrdinalIgnoreCase) ||
                            !Safety.ByteEqual(full, group.Representative.Path, ct))
                        {
                            reason = "スキャン後のパス・内容・権限・元NTFS IDが不一致";
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    reason = "元ファイルまたは統合先を厳密に検証できません";
                }
            }
            bool verified = reason.Length == 0;
            if (verified) savings = checked(savings + group.Savings);
            checkedGroups.Add(new SelectedGroupReview(
                group.Files.Count == 0 ? "(invalid selection)" :
                    group.Representative.Path, shownPaths,
                verified ? "照合一致（未実行）" : reason,
                verified ? group.Savings : 0));
        }
        var (target, journal) = CapacityGuard.ReadAvailable(root);
        bool enough = CapacityGuard.Enough(target, CapacityGuard.TargetReserve) &&
            CapacityGuard.Enough(journal, CapacityGuard.JournalReserve);
        return new SelectedReview(root, checkedGroups, savings, savings,
            target, journal, journalClean, historyClean, enough);
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
