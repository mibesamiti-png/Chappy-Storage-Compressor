namespace ChappyStorageCompressor.Core;

/// <summary>
/// Part38: a read-only triage/checklist projection of the Part36/37 evidence
/// view. It performs NO IO: source identity, original ownership, arbitrary
/// destination races and actual power-loss safety CANNOT be inferred from
/// an output-only receipt. No decision ever authorizes production mutation.
/// </summary>
public static class CollisionRecoveryChecklist
{
    public const bool ProductionMutationAllowed = false;

    public sealed record Check(string Category, string State, string Explanation)
    {
        public bool NeedsAttention => State is "要確認" or "未完了" or "未検証" or "禁止";
    }

    public sealed record Review(string Root, string Decision,
        IReadOnlyList<Check> Checks, string RecordId, string Scope)
    {
        public bool CanAutomaticallyResume => false;
        public bool CanDeleteOrOverwrite => false;
        public bool AuthorizesOriginalRestore => false;

        public string Describe() =>
            $"同名ファイル復旧判断：{Decision}。検査項目 {Checks.Count}件。" +
            " 保存先の照合は元ファイルの完全な復旧許可ではありません。" +
            " 本番の再開・上書き・削除は禁止です。";

        public string DetailText() =>
            $"【復旧可否の確認項目：読み取り専用】\n" +
            $"対象：{Scope}\n判断：{Decision}\n\n" +
            string.Join("\n", Checks.Select(x =>
                $"・{x.Category}：{x.State} — {x.Explanation}")) +
            "\n\nこの一覧は安全性の判定補助のみ。自動修復・再開・復元許可は発行しません。";
    }

    private static Check CheckOf(string category, string state, string description) =>
        new(category, state, description);

    public static Review Analyze(CollisionEvidenceReview.Summary? evidence,
        CollisionEvidenceReview.Row? selected = null)
    {
        if (evidence is null)
            return Unverified("(対象フォルダ未指定)", "(履歴を読み取れません)",
                "読み取り専用の調査結果がありません。フォルダを選択して再検査してください。");

        string root = evidence.Root;
        var rows = evidence.Rows;
        string scope = selected is null ?
            "選択フォルダ直下の同名ファイル台帳" :
            string.IsNullOrWhiteSpace(selected.RecordId) ?
                "選択された証拠" : "操作ID " + selected.RecordId;
        string id = selected?.RecordId ?? "";

        if (!evidence.JournalPresent)
            return Unverified(root, scope,
                "選択したフォルダに台帳がありません。別フォルダや過去の操作が正常だと示すものではありません。",
                "証拠なし（復旧可否は未検証）", id);

        if (selected is not null && !rows.Contains(selected))
            return Unverified(root, scope,
                "現在の調査結果に選択された項目が存在しません。再検査が必要です。",
                "要確認（表示行が最新証拠と不一致）", id);

        if (rows.Count == 0)
            return Unverified(root, scope,
                "台帳フォルダに検査可能な操作記録がありません。",
                "証拠なし（復旧可否は未検証）", id);

        // A foreign file can belong to any operation in the selected root:
        // never claim that a locally valid receipt set makes the *folder*
        // safe. Unknown evidence blocks EVERY per-record decision too.
        bool unknown = rows.Any(r => r.Kind == "不明な証拠");
        var examined = selected is null || selected.Kind == "不明な証拠" ?
            rows : rows.Where(r => r.RecordId == selected.RecordId).ToList();

        // This can happen with a stale/corrupt UI selection; fail closed.
        if (examined.Count == 0)
            return Unverified(root, scope,
                "選択した操作IDに対応する操作記録がありません。",
                "要確認（対応する操作記録なし）", id);

        bool invalid = examined.Any(r => r.State == "ManualReview") ||
            examined.Any(r => r.Kind == "操作記録" &&
                r.NeedsAttention && r.State != "未完了");
        bool pending = examined.Any(r => r.State == "未書き出し" ||
            r.State == "未完了");
        bool hasIntent = examined.Any(r => r.Kind == "操作記録");
        bool localOutputs = examined.Any(r => r.Kind == "ファイル");
        bool outputsMatch = localOutputs &&
            examined.Where(r => r.Kind == "ファイル")
                .All(r => r.State == "保存先照合済み" && !r.NeedsAttention);
        bool memoMatches = !examined.Any(r => r.Kind == "一覧メモ") ||
            examined.Where(r => r.Kind == "一覧メモ")
                .All(r => r.State == "保存先照合済み" && !r.NeedsAttention);
        bool completedRecord = hasIntent &&
            examined.Where(r => r.Kind == "操作記録").All(r =>
                r.State == "保存先証拠一致（元データは未照合）" &&
                !r.NeedsAttention);

        string decision = unknown || invalid || !hasIntent ?
            "要確認（証拠に不整合）" :
            pending || !outputsMatch || !memoMatches || !completedRecord ?
            "未完了（自動再開不可）" :
            "保存先証拠のみ一致（本番復元不可）";

        var checks = new List<Check>
        {
            CheckOf("対象範囲", "確認範囲限定",
                "選択したフォルダ直下の同名ファイル台帳のみ。サブフォルダや別ドライブは未検査"),
            CheckOf("操作記録", !hasIntent || invalid ? "要確認" :
                !completedRecord ? "未完了" : "保存先照合済み",
                !hasIntent ? "対応する操作記録がありません" :
                invalid ? "操作の識別子、内容または実体が記録と一致しません" :
                !completedRecord ? "完了記録または保存処理が残っています" :
                "完了記録と保存先の情報は一致。元データは未検証"),
            CheckOf("保存先ファイルのID・SHA・ACL",
                !localOutputs ? "未検証" :
                invalid || examined.Any(r => r.Kind == "ファイル" &&
                    r.State == "ManualReview") ? "要確認" :
                outputsMatch ? "保存先照合済み" : "未完了",
                !localOutputs ? "照合対象の出力ファイルがありません" :
                outputsMatch ? "確認済みの各出力ファイルは台帳と一致（元ファイルとは別）" :
                "未書き出し・レシート欠落・ファイルID不一致などの可能性"),
            CheckOf("同名ファイル一覧.txt と完了レシート",
                examined.Any(r => r.Kind == "一覧メモ" &&
                    r.State == "ManualReview") ? "要確認" :
                !examined.Any(r => r.Kind == "一覧メモ") ? "対象なし" :
                memoMatches ? "保存先照合済み" : "未完了",
                "元の保存計画との内容照合を含む。既存メモは移動・上書き・削除しない"),
            CheckOf("未知の証拠・.tmp", unknown ? "要確認" : "調査対象内に検出なし",
                unknown ? "見知らぬ証拠があるため操作全体を完了と判断できません" :
                "このフォルダで検査した範囲に限り、未登録ファイルは検出されませんでした"),
            CheckOf("元ファイル・元の権限・保存場所", "未検証",
                "台帳内のSourcePathは開いていません。元ファイルの現存、NTFS ID、内容と権限は別途照合が必要"),
            CheckOf("競合・停電・途中中断の完全復旧", "未検証",
                "複数ファイルの原子的な書き出し・実電源断・全パスの競合防止は未証明"),
            CheckOf("本番ファイルへの変更・自動復元", "禁止",
                "この画面は証拠表示のみ。再開・上書き・ファイル削除・Undoを許可しない")
        };
        return new Review(root, decision, checks, id, scope);
    }

    private static Review Unverified(string root, string scope, string reason,
        string verdict = "検査不能（復旧可否は未検証）", string id = "") =>
        new(root, verdict, new List<Check>
        {
            CheckOf("台帳の信頼性", "要確認", reason),
            CheckOf("元ファイルの現況", "未検証", "元データの存在・内容・NTFS IDを検証していません"),
            CheckOf("本番ファイルへの変更", "禁止", "検査不能でも自動復元や削除は実行しません")
        }, id, scope);
}
