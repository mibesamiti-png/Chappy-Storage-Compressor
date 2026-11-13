namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.2 advisory-only safety gate. This component never creates, renames,
/// deletes, moves or replaces a file. A PASS is NOT authority to perform a
/// replacement; content fidelity, atomic rollback, ACL and crash tests remain
/// separate release blockers.
/// </summary>
public static class ReplacementReadiness
{
    // Leave room for OS/application writes, temporary journal files and metadata.
    // This is a conservative configurable-design starting point, not a guarantee.
    public const long MinimumReserveBytes = 64L * 1024 * 1024;

    public sealed record CapacityDecision(bool HasEnoughSourceSpace, bool HasEnoughBackupSpace,
        bool BackupDifferentVolume, bool CandidateIsSmaller, string Explanation)
    {
        public bool MeetsSpaceRequirements => HasEnoughSourceSpace && HasEnoughBackupSpace &&
            BackupDifferentVolume && CandidateIsSmaller;
    }

    public sealed record Row(string Format, string RelativePath, long SourceBytes,
        long CandidateBytes, long ProjectedSavingBytes, bool SpaceRequirementsMet,
        string Explanation);

    public sealed record Summary(int CandidateCount, int SpaceFeasibleCount,
        long SpaceFeasibleLogicalSavingBytes, long InputVolumeFreeBytes,
        long BackupVolumeFreeBytes, bool IndependentBackupVolume,
        IReadOnlyList<Row> Rows)
    {
        public long ActualFreedBytes => 0; // R2.2 contains NO replacement mechanism.
    }

    public static CapacityDecision EvaluateCapacity(long sourceBytes, long candidateBytes,
        long inputVolumeAvailableBytes, long backupVolumeAvailableBytes,
        bool differentVolume, long reserveBytes = MinimumReserveBytes)
    {
        if (sourceBytes <= 0 || candidateBytes <= 0 || reserveBytes < 0 ||
            inputVolumeAvailableBytes < 0 || backupVolumeAvailableBytes < 0)
            return new(false, false, differentVolume, false, "容量の入力値が不正です");
        if (candidateBytes >= sourceBytes)
            return new(false, false, differentVolume, false, "圧縮候補が原本より小さくありません");
        // Subtraction avoids Int64 overflow for enormous files or invalid metadata.
        bool sourceEnough = inputVolumeAvailableBytes >= reserveBytes &&
            candidateBytes <= inputVolumeAvailableBytes - reserveBytes;
        bool backupEnough = backupVolumeAvailableBytes >= reserveBytes &&
            sourceBytes <= backupVolumeAvailableBytes - reserveBytes;
        string reason = !differentVolume ? "バックアップ先が原本と別ボリュームではありません" :
            !backupEnough ? "バックアップ用に原本全体を保存する空き容量が不足" :
            !sourceEnough ? "元ボリュームに候補を安全にステージングする領域が不足" :
            "容量の事前条件のみ合格（内容・ACL・原子性は未検証）";
        return new(sourceEnough, backupEnough, differentVolume, true, reason);
    }

    public static Summary Inspect(CombinedCapacityReport.Summary verifiedPreviewReport,
        string inputRoot, string outputRoot, string backupRoot,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(verifiedPreviewReport);
        string input = Safety.ValidateRoot(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        string backup = JpegBatchPreviewEngine.CanonicalFolder(backupRoot);
        JpegBatchPreviewEngine.ValidateFolders(input, output);
        JpegBatchPreviewEngine.CheckNoReparse(backup);
        if (Inside(backup, input) || Inside(input, backup) ||
            Inside(backup, output) || Inside(output, backup))
            throw new InvalidOperationException("バックアップ先は原本・候補フォルダと分離してください。");
        var inputDrive = new DriveInfo(Path.GetPathRoot(input)!);
        var backupDrive = new DriveInfo(Path.GetPathRoot(backup)!);
        bool different = !inputDrive.Name.Equals(backupDrive.Name,
            StringComparison.OrdinalIgnoreCase);
        long inputFree = inputDrive.AvailableFreeSpace;
        long backupFree = backupDrive.AvailableFreeSpace;
        var rows = new List<Row>();
        int feasible = 0, candidates = 0;
        long projected = 0;
        foreach (var row in verifiedPreviewReport.Rows)
        {
            ct.ThrowIfCancellationRequested();
            if (row.EstimatedSavingBytes <= 0) continue;
            candidates++;
            string explanation;
            bool eligible = false;
            string path;
            try
            {
                if (Path.IsPathRooted(row.RelativePath))
                    throw new InvalidOperationException("相対パスではありません");
                path = Path.GetFullPath(Path.Combine(input, row.RelativePath));
                if (!Inside(path, input) || path.Equals(input, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("親フォルダ外のパス");
                JpegBatchPreviewEngine.CheckNoReparse(path);
                FileSnapshot snapshot = Safety.Capture(path);
                if (snapshot.Identity.LinkCount != 1)
                    throw new InvalidOperationException("ハードリンク済みのため独立ファイルではありません");
                if (snapshot.Length != row.SourceBytes)
                    throw new InvalidOperationException("原本サイズが候補分析時から変更されました");
                var decision = EvaluateCapacity(row.SourceBytes, row.CandidateBytes,
                    inputFree, backupFree, different);
                explanation = decision.Explanation;
                eligible = decision.MeetsSpaceRequirements;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                explanation = "安全判定不可: " + ex.Message;
            }
            if (eligible)
            {
                feasible++;
                projected = checked(projected + row.EstimatedSavingBytes);
            }
            rows.Add(new(row.Format, row.RelativePath, row.SourceBytes,
                row.CandidateBytes, row.EstimatedSavingBytes, eligible, explanation));
        }
        return new(candidates, feasible, projected, inputFree, backupFree, different, rows);
    }

    private static bool Inside(string path, string parent)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
