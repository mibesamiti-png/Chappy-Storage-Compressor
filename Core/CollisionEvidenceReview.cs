using System.Security.Cryptography;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Part36 read-only viewer for collision-journal evidence. This is INCLUDED
/// in the WPF release app, but can NEVER perform an extraction, restore,
/// deletion, rename, repair, or journal mutation. A matched destination is
/// only a local evidence match, NOT authorization to resume production Undo.
/// Untrusted SourcePath in JSON is displayed never opened.
/// </summary>
public static class CollisionEvidenceReview
{
    private const string Folder = ".chappy-collision-journal";
    private const int MaxEntries = 512;
    private const int MaxItems = 64;
    private const long MaxRecordBytes = 1024 * 1024;
    public const bool ProductionMutationEnabled = false;

    public sealed record Row(string Kind, string RecordId,
        string OriginalRelativePath, string DestinationRelativePath,
        string State, string Explanation, bool NeedsAttention)
    {
        public string AttentionText => NeedsAttention ? "要確認" : "保存先照合";
        public string DisplayTarget => string.IsNullOrEmpty(DestinationRelativePath)
            ? RecordId : DestinationRelativePath;

        /// <summary>
        /// Safe instructions only. In particular, no instruction here permits
        /// deleting an orphan, overwriting a destination or editing a journal.
        /// The WPF view has no mutation API and offers no Apply action.
        /// </summary>
        public string ReviewGuidance =>
            Kind == "不明な証拠" ?
                "見覚えのない証拠を移動・削除せず、操作IDと保存先の記録を保管して調査してください。" :
            Kind == "操作記録" && NeedsAttention ?
                "台帳全体に未完了・不整合があります。元ファイルと保存先を保存したまま、該当する証拠を個別に確認してください。" :
            State == "未書き出し" ?
                "元ファイルや保存先に変更を加えず、正式な復旧手順が検証されるまで再開しないでください。" :
            Kind == "ファイル" && NeedsAttention &&
                Explanation.Contains("完了レシートがありません",
                    StringComparison.Ordinal) ?
                "保存先に既存ファイルがあります。内容が一致していても削除・上書き・自動採用せず、記録を保管してください。" :
            NeedsAttention ?
                "証拠が一致していません。ファイルと台帳を編集せず、ID・SHA-256・権限情報を再調査してください。" :
                "保存先の証拠だけが一致しています。元データと台帳全体の復旧完了は未検証です。";
    }

    public sealed record Summary(string Root, IReadOnlyList<Row> Rows,
        bool JournalPresent)
    {
        public int TotalRecords => Rows.Count(x => x.Kind == "操作記録");
        public int FilesVerified => Rows.Count(x =>
            x.Kind == "ファイル" && !x.NeedsAttention);
        public int NeedsReview => Rows.Count(x => x.NeedsAttention);
        public bool CanModify => false;
        public string Describe() =>
            $"同名ファイル調査（読み取り専用）：記録 {TotalRecords} 件／" +
            $"保存先照合 {FilesVerified} 件／要確認 {NeedsReview} 件。" +
            (JournalPresent
                ? " 記録と保存先だけの照合です。元データの安全な復旧完了ではありません。"
                : " 選択した直下に同名ファイル用の記録はありません。別フォルダの記録は未検査です。") +
            " ファイルの復元・削除・移動はできません。";
    }

    private sealed class Manifest
    {
        public int Schema { get; set; }
        public string? Id { get; set; }
        public string? Root { get; set; }
        public string? CollectionRelativePath { get; set; }
        public string? ReportRelativePath { get; set; }
        public List<Item>? Items { get; set; }
    }
    private sealed class Item
    {
        public string? SourcePath { get; set; }
        public string? OriginalRelativePath { get; set; }
        public string? DestinationRelativePath { get; set; }
        public bool Collected { get; set; }
        public string? Reason { get; set; }
        public string? Hash { get; set; }
        public long Length { get; set; }
    }
    private sealed class Receipt
    {
        public int Schema { get; set; }
        public string? Id { get; set; }
        public int Index { get; set; }
        public string? DestinationRelativePath { get; set; }
        public string? Hash { get; set; }
        public uint Volume { get; set; }
        public ulong FileIndex { get; set; }
        public long Length { get; set; }
        public long CreationTicks { get; set; }
        public long ModifiedTicks { get; set; }
        public FileAttributes Attributes { get; set; }
        public string? SecurityHash { get; set; }
    }

    private static bool Hash64(string? hash) =>
        hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static string StrictRelative(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("不正な相対パス");
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => p.Length == 0 || p is "." or ".." ||
            p.EndsWith(' ') || p.EndsWith('.') ||
            p.Contains(':') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("絶対・遡り・ADSパスは禁止");
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("選択フォルダ外へのパス");
        return full;
    }

    private static void RejectReparseParents(string root, string full)
    {
        string rel = Path.GetRelativePath(root, full);
        var parts = rel.Split(Path.DirectorySeparatorChar);
        string cursor = root;
        for (int i = 0; i < parts.Length; i++)
        {
            cursor = Path.Combine(cursor, parts[i]);
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("シンボリックリンク・ジャンクションを経由");
            }
            if (i < parts.Length - 1 && File.Exists(cursor))
                throw new InvalidDataException("親フォルダ名がファイル");
        }
    }

    private static T ParseSmall<T>(string file)
    {
        var info = new FileInfo(file);
        info.Refresh();
        if (!info.Exists || info.Length is < 1 or > MaxRecordBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("壊れた/大きすぎる/リンク形式の記録");
        return JsonSerializer.Deserialize<T>(File.ReadAllBytes(file)) ??
            throw new InvalidDataException("空の操作記録");
    }

    private static string CanonicalReportHash(string root, Manifest manifest)
    {
        // Reconstruct exactly the Part35 report from the persisted user-
        // approved collision plan, without opening SourcePath or touching
        // any output file. This detects a self-consistent *changed* report
        // and edited receipt that no longer reflect the intent's mapping.
        if (manifest.Items is null ||
            string.IsNullOrWhiteSpace(manifest.ReportRelativePath) ||
            string.IsNullOrWhiteSpace(manifest.CollectionRelativePath))
            throw new InvalidDataException("一覧メモの元となる操作内容がありません");
        var plan = new CollisionCollectionPlan.Result(
            root, manifest.CollectionRelativePath, manifest.ReportRelativePath,
            manifest.Items.Select(x => new CollisionCollectionPlan.Entry(
                x.OriginalRelativePath ?? "", x.DestinationRelativePath ?? "",
                x.Collected, x.Reason ?? "")).ToList());
        var utf8 = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        byte[] bytes = utf8.GetPreamble().Concat(
            utf8.GetBytes(CollisionCollectionPlan.ReportText(plan))).ToArray();
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static bool Matches(string root, Receipt rec, string target)
    {
        if (rec.Schema != 1 || !Hash64(rec.Hash) ||
            !Hash64(rec.SecurityHash) || rec.Length <= 0 ||
            rec.Volume == 0 || rec.FileIndex == 0 ||
            rec.CreationTicks <= 0 || rec.ModifiedTicks <= 0)
            return false;
        try
        {
            RejectReparseParents(root, target);
            FileSnapshot now = Safety.Capture(target);
            return now.Identity.LinkCount == 1 &&
                now.Identity.VolumeSerial == rec.Volume &&
                now.Identity.FileIndex == rec.FileIndex &&
                now.Length == rec.Length &&
                now.CreationUtc.Ticks == rec.CreationTicks &&
                now.ModifiedUtc.Ticks == rec.ModifiedTicks &&
                now.Attributes == rec.Attributes &&
                Convert.ToHexString(SHA256.HashData(now.SecurityDescriptor)) ==
                    rec.SecurityHash &&
                Safety.ComputeSha256(target, CancellationToken.None)
                    .Equals(rec.Hash, StringComparison.OrdinalIgnoreCase) &&
                Safety.SameSnapshot(now);
        }
        catch { return false; }
    }

    public static Summary Inspect(string inputRoot, CancellationToken ct = default)
    {
        string root = Safety.ValidateRoot(inputRoot);
        var rows = new List<Row>();
        string folder = Path.Combine(root, Folder);
        if (!File.Exists(folder) && !Directory.Exists(folder))
            return new Summary(root, rows, false);

        Row Problem(string kind, string id, string original, string destination,
            string issue) => new(kind, id, original, destination,
                "ManualReview", issue + "。元ファイル・保存先・証拠は変更しません。", true);
        if (File.Exists(folder) ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
        {
            rows.Add(Problem("操作記録", "(不正な証拠フォルダ)", "", "", 
                "同名ファイル証拠の場所が普通のフォルダではありません"));
            return new Summary(root, rows, true);
        }
        string[] evidence;
        try
        {
            evidence = Directory.EnumerateFileSystemEntries(folder,
                "*", SearchOption.TopDirectoryOnly).ToArray();
            if (evidence.Length > MaxEntries)
                throw new InvalidDataException("調査可能な記録数上限を超えました");
        }
        catch (Exception ex)
        {
            rows.Add(Problem("操作記録", "(一覧取得失敗)", "", "",
                "証拠一覧を確認できません: " + ex.GetType().Name));
            return new Summary(root, rows, true);
        }
        var knownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var intentPaths = evidence.Where(p =>
            p.EndsWith(".intent.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (string path in intentPaths)
        {
            ct.ThrowIfCancellationRequested();
            string filename = Path.GetFileName(path);
            string id = filename[..^".intent.json".Length];
            knownNames.Add(filename);
            try
            {
                if (!Guid.TryParseExact(id, "N", out _))
                    throw new InvalidDataException("記録ファイル名の識別子が不正");
                Manifest record = ParseSmall<Manifest>(path);
                if (record.Schema != 1 || record.Id != id ||
                    !string.Equals(record.Root, root, StringComparison.OrdinalIgnoreCase) ||
                    record.Items is not { Count: > 0 and <= MaxItems } ||
                    record.Items.Any(item => item == null))
                    throw new InvalidDataException("操作記録の型・ルート・件数が不正");

                string reportRelative = record.ReportRelativePath ?? "";
                bool collected = record.Items.Any(x => x.Collected);
                string collection = record.CollectionRelativePath ?? "";
                if (collected != (reportRelative.Length > 0) ||
                    collected != (collection.Length > 0))
                    throw new InvalidDataException("同名ファイルの有無と報告書が不整合");
                if (collected)
                {
                    // Collection nesting is exactly 同名ファイル/同名ファイル...,
                    // never an arbitrary path supplied by a forged journal.
                    string[] names = collection.Replace('\\', '/').Split('/');
                    if (names.Length is < 1 or > 32 ||
                        names.Any(name => name != CollisionCollectionPlan.FolderName))
                        throw new InvalidDataException("同名ファイル集約フォルダの形式が不正");
                    StrictRelative(root, collection);
                }
                if (reportRelative.Length > 0)
                {
                    string reportPath = StrictRelative(root, reportRelative);
                    if (!string.Equals(Path.GetDirectoryName(reportPath), root,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("報告メモが最上位以外に指定されました");
                }
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int verified = 0, missing = 0, review = 0;
                for (int i = 0; i < record.Items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    Item item = record.Items[i];
                    string original = item.OriginalRelativePath ?? "";
                    string relative = item.DestinationRelativePath ?? "";
                    string destination = StrictRelative(root, relative);
                    StrictRelative(root, original);
                    if (item.Reason is null ||
                        (item.Collected && (collection.Length == 0 ||
                         !relative.StartsWith(collection +
                             Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase))) ||
                        !Hash64(item.Hash) || item.Length <= 0 ||
                        !seen.Add(destination) ||
                        relative.Equals(Folder, StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith(Folder + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("元データ・保存先の形式が不正");
                    RejectReparseParents(root, destination);
                    string receiptName = id + ".item-" + i.ToString("D4") + ".json";
                    knownNames.Add(receiptName);
                    string receipt = Path.Combine(folder, receiptName);
                    bool hasOutput = File.Exists(destination) ||
                        Directory.Exists(destination);
                    bool hasReceipt = File.Exists(receipt) ||
                        Directory.Exists(receipt);
                    if (!hasReceipt)
                    {
                        if (hasOutput)
                        {
                            review++;
                            rows.Add(Problem("ファイル", id, original, relative,
                                "書き出し先は存在しますが、対応する完了レシートがありません"));
                        }
                        else
                        {
                            missing++;
                            rows.Add(new Row("ファイル", id, original, relative,
                                "未書き出し", "まだ保存先がありません。明示的な台帳検証なしでは再開できません。", true));
                        }
                        continue;
                    }
                    try
                    {
                        Receipt proof = ParseSmall<Receipt>(receipt);
                        if (proof.Id != id || proof.Index != i ||
                            !string.Equals(proof.DestinationRelativePath, relative,
                                StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(proof.Hash, item.Hash,
                                StringComparison.OrdinalIgnoreCase) ||
                            proof.Length != item.Length ||
                            !Matches(root, proof, destination))
                            throw new InvalidDataException("レシートと保存先ファイルの実体/内容が不一致");
                        verified++;
                        rows.Add(new Row("ファイル", id, original, relative,
                            "保存先照合済み",
                            "保存先のNTFSファイルID・SHA-256・時刻・属性・owner/DACLが記録と一致。" +
                            " 元ファイルの現在の状態や処理全体の安全性は別途未検証です。", false));
                    }
                    catch (Exception ex)
                    {
                        review++;
                        rows.Add(Problem("ファイル", id, original, relative,
                            "保存先の記録検証失敗: " + ex.GetType().Name));
                    }
                }
                if (reportRelative.Length > 0 &&
                    seen.Contains(StrictRelative(root, reportRelative)))
                    throw new InvalidDataException("一覧メモが出力ファイルと同じ名前です");
                string reportName = id + ".report.json";
                string completeName = id + ".complete";
                knownNames.Add(completeName);
                if (reportRelative.Length > 0)
                {
                    knownNames.Add(reportName);
                    string pathToReport = StrictRelative(root, reportRelative);
                    string receiptPath = Path.Combine(folder, reportName);
                    bool hasReport = File.Exists(pathToReport) ||
                        Directory.Exists(pathToReport);
                    if (!File.Exists(receiptPath) && hasReport)
                    {
                        review++;
                        rows.Add(Problem("一覧メモ", id, "", reportRelative,
                            "一覧メモが存在しますが、完了レシートがありません"));
                    }
                    else if (!File.Exists(receiptPath))
                    {
                        missing++;
                        rows.Add(new Row("一覧メモ", id, "", reportRelative,
                            "未書き出し", "報告書はまだ完成していません。既存メモを上書きしません。", true));
                    }
                    else
                    {
                        try
                        {
                            Receipt proof = ParseSmall<Receipt>(receiptPath);
                            if (proof.Id != id || proof.Index != record.Items.Count ||
                                !string.Equals(proof.DestinationRelativePath,
                                    reportRelative, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(proof.Hash,
                                    CanonicalReportHash(root, record),
                                    StringComparison.OrdinalIgnoreCase) ||
                                !Matches(root, proof, pathToReport))
                                throw new InvalidDataException("一覧メモのレシート不一致");
                            rows.Add(new Row("一覧メモ", id, "", reportRelative,
                                "保存先照合済み",
                                "保存先の一覧メモと完了レシートは一致。元データと全体の完了は別途確認が必要です。", false));
                        }
                        catch (Exception ex)
                        {
                            review++;
                            rows.Add(Problem("一覧メモ", id, "", reportRelative,
                                "一覧メモの内容または実体が不一致: " + ex.GetType().Name));
                        }
                    }
                }
                bool completed = File.Exists(Path.Combine(folder, completeName));
                if (completed)
                {
                    try
                    {
                        string completeFile = Path.Combine(folder, completeName);
                        if (new FileInfo(completeFile).Length > 128 ||
                            (File.GetAttributes(completeFile) & FileAttributes.ReparsePoint) != 0 ||
                            File.ReadAllText(completeFile) != "Part35 verified complete")
                            throw new InvalidDataException("完了マーカーの形式不正");
                    }
                    catch { review++; completed = false; }
                }
                string recordState = review == 0 && missing == 0 && completed &&
                    verified == record.Items.Count ? "保存先証拠一致（元データは未照合）" :
                    review > 0 ? "ManualReview" : "未完了";
                rows.Add(new Row("操作記録", id, "", "",
                    recordState,
                    $"書き出し予定 {record.Items.Count}件／保存先照合 {verified}件／未書き出し {missing}件／" +
                    $"要調査 {review}件。完了マーカー: {(completed ? "あり" : "なし")}。" +
                    " 本画面は元データを再検証せず、復元・再開はできません。",
                    recordState != "保存先証拠一致（元データは未照合）"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                rows.Add(Problem("操作記録", id, "", "",
                    "操作記録を検証できません: " + ex.GetType().Name));
            }
        }
        var unrecognized = evidence.Where(path =>
            !knownNames.Contains(Path.GetFileName(path))).ToArray();
        foreach (string path in unrecognized)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(Problem("不明な証拠", Path.GetFileName(path), "", "",
                "未登録・未完了・別操作の証拠があります"));
        }
        // Part36 displayed the foreign .tmp row but could still mark the
        // *operation* Verified because the known receipts were valid.
        // One unexplained file means this journal folder is NOT all-clear.
        if (unrecognized.Length > 0)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Kind != "操作記録" || row.NeedsAttention) continue;
                rows[i] = Problem("操作記録", row.RecordId,
                    row.OriginalRelativePath, row.DestinationRelativePath,
                    "同じ台帳に不明な証拠があるため操作全体は未検証");
            }
        }
        return new Summary(root, rows, true);
    }
}
