using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Read-only projection of the existing Part34 collision folder/memo policy
/// onto byte-exact duplicate groups. No file is moved, copied, deleted or
/// added to the recycle bin by this planner.
/// </summary>
public static class DuplicateCleanupPreview
{
    public sealed record Item(
        string OriginalPath,
        string OriginalRelativePath,
        string ProposedCollectionRelativePath,
        long OriginalBytes,
        string Reason);

    public sealed record Result(
        string Root,
        string KeeperPath,
        string KeeperRelativePath,
        string GroupSha256,
        string CollectionRelativePath,
        string ReportRelativePath,
        IReadOnlyList<Item> DeleteCandidates,
        long LogicalCandidateBytes)
    {
        public bool CanModifyUserFiles => false;

        public string Describe()
        {
            var memo = new StringBuilder();
            memo.AppendLine("Chappy Storage Compressor — 重複整理の確認メモ（未実行）")
                .AppendLine("このメモは画面上の予定表です。ファイルの移動・削除・ごみ箱操作は未実施です。")
                .AppendLine("対象の最上位フォルダ: " + Root)
                .AppendLine("残すファイル: " + KeeperPath)
                .AppendLine("残す相対パス: " + KeeperRelativePath)
                .AppendLine("グループSHA-256（前回スキャン時）: " + GroupSha256)
                .AppendLine("削除候補: " + DeleteCandidates.Count + "件")
                .AppendLine("候補の論理サイズ合計: " + GroupRow.HumanSize(LogicalCandidateBytes))
                .AppendLine("整理フォルダ予定: " + CollectionRelativePath)
                .AppendLine("最上位の一覧メモ予定: " + ReportRelativePath)
                .AppendLine()
                .AppendLine("【削除候補の元パス → 仮の整理先】");
            foreach (var (candidate, i) in DeleteCandidates.Select((value, index) => (value, index + 1)))
            {
                memo.AppendLine($"{i}. 元のフルパス: {candidate.OriginalPath}")
                    .AppendLine($"   元の相対パス: {candidate.OriginalRelativePath}")
                    .AppendLine($"   フォルダ分け後: {candidate.ProposedCollectionRelativePath}")
                    .AppendLine($"   元サイズ: {GroupRow.HumanSize(candidate.OriginalBytes)}")
                    .AppendLine($"   理由: {candidate.Reason}");
            }
            memo.AppendLine()
                .AppendLine("【今後の安全な削除手順（未解禁）】")
                .AppendLine("1. 残す1件と候補ごとの元パス・予定整理先を確認する")
                .AppendLine("2. 変更前にファイルID・全バイト・権限・リンク数・空き容量を再照合する")
                .AppendLine("3. 明示確認後にのみごみ箱へ移動し、移動成否を永続履歴に記録する")
                .AppendLine("4. ごみ箱移動失敗なら停止し、勝手に完全削除しない")
                .AppendLine("5. 実容量回復は別に測定する。ごみ箱に残る間は空き容量が増えない場合がある")
                .AppendLine()
                .AppendLine("既存ファイルや既存メモの上書きは禁止です。");
            return memo.ToString();
        }
    }

    private static string RelativeWithin(string root, string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("重複ファイルが検索ルートの外にあります。再スキャンしてください。");
        if (!Safety.ContainedRegularSource(root, full))
            throw new InvalidDataException("特殊ファイル/リンク/安全対象外のパスが含まれます。再スキャンしてください。");
        return Path.GetRelativePath(root, full);
    }

    public static Result Inspect(string scanRoot, DuplicateGroup group, string keeperPath)
    {
        if (group.Files.Count < 2)
            throw new InvalidDataException("整理には完全一致ファイルが2件以上必要です。");

        string root = Safety.ValidateRoot(scanRoot);
        string fullKeeper = Path.GetFullPath(keeperPath);
        var keeper = group.Files.SingleOrDefault(file =>
            string.Equals(Path.GetFullPath(file.Path), fullKeeper, StringComparison.OrdinalIgnoreCase));
        if (keeper is null)
            throw new InvalidDataException("残すファイルが現在のグループにありません。");
        string keeperRelative = RelativeWithin(root, keeper.Path);

        // This preview concerns independent removable duplicate names only.
        // Existing hardlinks need their own policy; they cannot be counted
        // as independent reclaimable allocations.
        if (group.Files.Any(file => file.Identity.LinkCount != 1 ||
            file.Identity.VolumeSerial != keeper.Identity.VolumeSerial))
            throw new InvalidDataException(
                "既存ハードリンクまたは異なるボリュームが含まれます。削除候補の自動整理はできません。");

        var candidates = group.Files.Where(file =>
            !string.Equals(file.Path, keeper.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        var relatives = candidates.Select(file => RelativeWithin(root, file.Path)).ToArray();
        if (relatives.Distinct(StringComparer.OrdinalIgnoreCase).Count() != relatives.Length)
            throw new InvalidDataException("重複したファイルパスが含まれます。再スキャンしてください。");

        // Every source path already exists, so the existing collision planner
        // will route every candidate under the top-level 同名ファイル folder,
        // preserving source subfolders, numbering and existing records.
        var organization = CollisionCollectionPlan.Inspect(root, relatives);
        if (organization.Entries.Count != candidates.Count ||
            organization.Conflicts != candidates.Count ||
            string.IsNullOrWhiteSpace(organization.CollectionRelativePath) ||
            string.IsNullOrWhiteSpace(organization.ReportRelativePath))
            throw new InvalidDataException("整理先を安全に確保できません。再スキャンしてください。");

        var items = candidates.Select((file, index) =>
            new Item(file.Path, relatives[index],
                organization.Entries[index].DestinationRelativePath,
                file.Length, organization.Entries[index].Reason)).ToArray();
        long logical = checked(items.Sum(item => item.OriginalBytes));
        return new Result(root, keeper.Path, keeperRelative, group.Hash,
            organization.CollectionRelativePath, organization.ReportRelativePath,
            items, logical);
    }
}
