namespace ChappyStorageCompressor.Core;

/// <summary>
/// A single read-only explanation of every persisted recovery evidence family.
/// This deliberately has NO Apply/Delete/Retry method, even in test builds.
/// An absent selected root or an unreadable evidence family is never reported
/// as "all clear". Historical Journal recovery remains a separate explicit UI.
/// </summary>
public static class RecoveryEvidenceOverview
{
    public const bool ProductionMutationEnabled = false;

    public sealed record Row(string Source, string Target, string State,
        string Explanation, bool NeedsAttention)
    {
        public string AttentionText => NeedsAttention ? "要確認" : "確認済み";
    }

    public sealed record Summary(string Root, IReadOnlyList<Row> Rows)
    {
        public int PendingLegacy => Rows.Count(x => x.Source == "未完了の操作記録");
        public int CompletedPairs => Rows.Count(x =>
            x.Source == "2件単位の統合・Undo履歴" && !x.NeedsAttention);
        public int GroupHistory => Rows.Count(x =>
            x.Source == "複数ファイルのグループ履歴");
        public int UndoCrashHistory => Rows.Count(x =>
            x.Source == "Undo異常終了の記録");
        public int RequiresAttention => Rows.Count(x => x.NeedsAttention);
        public bool AllEvidenceVerified => RequiresAttention == 0;
        public bool ProductionMergeOrUndoAllowed => false;

        public string Describe() =>
            "【ファイル・復旧履歴の横断チェック：読み取り専用】\n" +
            $"対象: {Root}\n" +
            $"操作中の記録: {PendingLegacy}件 / 2件単位の検証済み履歴: {CompletedPairs}件\n" +
            $"3件以上のグループ履歴: {GroupHistory}件 / Undo異常終了履歴: {UndoCrashHistory}件\n" +
            $"要確認・未完了: {RequiresAttention}件\n" +
            (AllEvidenceVerified ?
                "この時点で読み取れた記録の整合性は確認済みです。" :
                "要確認の記録があります。元ファイルや記録を削除せず、原因を調査してください。") +
            "\n本番の重複統合・独立Undo・原本置換は無効です。" +
            " この画面は実行許可や容量回復を意味しません。";
    }

    public static Summary Inspect(string inputRoot,
        IReadOnlyList<RecoveryCase>? alreadyInspectedJournal = null,
        CancellationToken ct = default)
    {
        string root = Safety.ValidateRoot(inputRoot);
        ct.ThrowIfCancellationRequested();
        var rows = new List<Row>();
        void Failure(string source, string message) =>
            rows.Add(new Row(source, "(untrusted evidence)", "ManualReview",
                message + "。証拠を変更せず手動調査してください。", true));

        try
        {
            var journal = alreadyInspectedJournal ?? RecoveryService.InspectAll(ct: ct);
            foreach (RecoveryCase item in journal)
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(new Row("未完了の操作記録",
                    item.OriginalPath, item.ActionText,
                    item.Explanation +
                    " これは未解決の復旧候補であり、自動的に修復しません。",
                    true));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Failure("未完了の操作記録", "旧復旧記録の読み取り失敗: " +
                ex.GetType().Name);
        }

        try
        {
            var pairs = CompletedMergeLedger.Inspect(root, ct);
            foreach (var e in pairs.Entries)
            {
                ct.ThrowIfCancellationRequested();
                bool verified = e.State is "VerifiedCompleted" or "VerifiedRestored";
                rows.Add(new Row("2件単位の統合・Undo履歴",
                    e.RelativeAlias, e.State, e.Explanation +
                    (verified && e.State == "VerifiedCompleted" ?
                        " ※統合されたファイル名は同じNTFS実体を参照しています。" : ""),
                    !verified));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Failure("2件単位の統合・Undo履歴",
                "完了台帳の読み取り失敗: " + ex.GetType().Name);
        }

        try
        {
            var groups = CompletedGroupLedger.Inspect(root);
            ct.ThrowIfCancellationRequested();
            foreach (var e in groups.Entries)
            {
                bool verified = e.State is "VerifiedCompleted" or "VerifiedRestored";
                string explanation = $"代表1件＋別名{e.AliasCount}件。" +
                    $" 独立Undoチェックポイント済み {e.DetachedCount}件。" +
                    (verified ? " 保存済みのファイルID・リンク数・内容を照合済み。" :
                        " 途中終了・履歴不一致・未知のリンクの可能性。明示的に再検証が必要。") +
                    " この表示からファイルを変更することはできません。";
                rows.Add(new Row("複数ファイルのグループ履歴",
                    e.OperationId, e.State, explanation, !verified));
            }
            // Use the EXISTING verified manifest: each original name and
            // pre-merge NTFS ID is individually visible in the WPF history
            // grid. Unknown/prepared evidence never grants an original row.
            // A post-inspection race becomes an explicit ManualReview row.
            foreach (var original in CompletedGroupLedger.InspectOriginals(root, ct))
            {
                rows.Add(new Row("グループ元ファイルの個別履歴",
                    original.Path, original.State,
                    "操作ID " + original.OperationId + " / " +
                    original.Describe() +
                    "。これは元の記録の照合であり、復元や容量回復の許可ではありません。",
                    false));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Failure("複数ファイルのグループ履歴",
                "グループ台帳の読み取り失敗: " + ex.GetType().Name);
        }

        try
        {
            var interrupted = IndependentUndoCrashJournal.Inspect(root, ct);
            foreach (var e in interrupted.Entries)
            {
                ct.ThrowIfCancellationRequested();
                bool verified = e.State is "VerifiedFinished" or "VerifiedAborted";
                rows.Add(new Row("Undo異常終了の記録",
                    e.RelativeAlias, e.State,
                    e.Explanation + " 中断履歴はこの画面から変更できません。",
                    !verified));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Failure("Undo異常終了の記録",
                "Undo中断証拠の読み取り失敗: " + ex.GetType().Name);
        }

        return new Summary(root, rows);
    }
}
