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

    // G4-01/02/03/05: common *read-only* fresh-image preflight. Nothing
    // here authorizes a File.Replace; the future transaction must repeat
    // these checks under pinned source/destination identities at cutover.
    public sealed record FidelityDecision(string Format, bool Verified,
        long OriginalBytes, long CandidateBytes, string OriginalSha256,
        string CandidateSha256, string Reason)
    {
        public bool ProductionReplacementAuthorized => false;
    }

    public static FidelityDecision InspectCandidatePair(string originalPath,
        string candidatePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string original = Path.GetFullPath(originalPath);
        string candidate = Path.GetFullPath(candidatePath);
        string format = Path.GetExtension(original).ToLowerInvariant();
        if (format is ".jpeg") format = ".jpg";
        if (format is not (".png" or ".jpg"))
            return new(format, false, 0, 0, "", "", "PNG/JPG以外は未対応");
        try
        {
            if (original.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("原本と候補が同じパスです");
            JpegBatchPreviewEngine.CheckNoReparse(original);
            JpegBatchPreviewEngine.CheckNoReparse(candidate);
            var source = Safety.Capture(original);
            var optimized = Safety.Capture(candidate);
            if (source.Identity.LinkCount != 1 ||
                optimized.Identity.LinkCount != 1 ||
                source.Identity.SameFile(optimized.Identity) ||
                optimized.Length >= source.Length ||
                (format == ".png" &&
                 !Path.GetExtension(candidate).Equals(".png",
                     StringComparison.OrdinalIgnoreCase)) ||
                (format == ".jpg" &&
                 !new[] { ".jpg", ".jpeg" }.Contains(
                     Path.GetExtension(candidate), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "原本/候補のID・ハードリンク・形式・削減量が不正");
            // Refuse ordinary concurrent writers and pathname deletion while
            // checking the same two files. This is NOT a mutation lock.
            using var sourceLock = new FileStream(original, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            using var candidateLock = new FileStream(candidate, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            if (sourceLock.Length != source.Length ||
                candidateLock.Length != optimized.Length)
                throw new InvalidDataException("検証中にファイル長が変わりました");
            string originalHash = Safety.ComputeSha256(original, ct);
            string candidateHash = Safety.ComputeSha256(candidate, ct);
            if (originalHash == candidateHash)
                throw new InvalidDataException("候補と原本の内容が同一です");
            bool equivalent = format == ".png"
                ? PngLosslessOptimizer.EquivalentForReplacement(original, candidate, ct)
                : JpegMarkerAudit.Inspect(original, ct) ==
                    JpegMarkerAudit.Inspect(candidate, ct) &&
                    JpegLosslessPreviewEngine.EquivalentDecodedPixels(
                        original, candidate, ct);
            if (!equivalent ||
                !Safety.SameSnapshot(source) ||
                !Safety.SameSnapshot(optimized) ||
                NativeFile.GetIdentity(original).LinkCount != 1 ||
                NativeFile.GetIdentity(candidate).LinkCount != 1 ||
                Safety.ComputeSha256(original, ct) != originalHash ||
                Safety.ComputeSha256(candidate, ct) != candidateHash)
                throw new InvalidDataException(
                    "原本・候補の画素/透明度/付加情報または元NTFS証拠が不一致");
            return new(format, true, source.Length, optimized.Length,
                originalHash, candidateHash,
                "元ID/サイズ/権限/全バイト・画像画素・必須メタデータ再確認済み。" +
                " 本番置換・永続バックアップは未実装のため適用不可");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException or
            OverflowException or System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.COMException)
        {
            return new(format, false, 0, 0, "", "",
                "候補を採用できません: " + ex.GetType().Name + " / " + ex.Message);
        }
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
        // Recover the candidate paths from the EXISTING verified PNG/JPG
        // journals; caller-supplied capacity rows are not trusted as file
        // authority. Full fidelity is checked again at this WPF preflight.
        var candidatesByKey = new Dictionary<string, (string Path, long SourceBytes,
            long CandidateBytes)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path.Combine(output, ".chappy-png-batch-r16.json")))
        {
            foreach (var candidate in PngBatchJournalTools.ReadAudit(input, output, ct).Rows)
                if (candidate.State == "CandidatePresent")
                    candidatesByKey.Add("PNG|" + candidate.RelativePath,
                        (candidate.PreviewPath, candidate.SourceBytes, candidate.PreviewBytes));
        }
        string jpegPlan = Path.Combine(output, JpegBatchPreviewEngine.PlanName);
        if (File.Exists(jpegPlan))
        {
            var plan = JpegBatchPreviewEngine.LoadPlan(jpegPlan);
            foreach (var candidate in JpegBatchPreviewEngine.Inspect(
                         input, output, plan.ToolPath, ct).Rows)
                if (candidate.Status == "Completed" && candidate.VerifiedSavingBytes > 0)
                    candidatesByKey.Add("JPG|" + candidate.RelativePath,
                        (candidate.CandidatePath, candidate.SourceBytes, candidate.CandidateBytes));
        }
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
                if (decision.MeetsSpaceRequirements)
                {
                    if (!candidatesByKey.TryGetValue(row.Format + "|" + row.RelativePath,
                            out var saved) ||
                        saved.SourceBytes != row.SourceBytes ||
                        saved.CandidateBytes != row.CandidateBytes ||
                        !Inside(saved.Path, output))
                        throw new InvalidDataException("候補台帳の保存先・サイズが一致しません");
                    var fidelity = InspectCandidatePair(path, saved.Path, ct);
                    eligible = fidelity.Verified;
                    explanation = eligible ?
                        "容量/独立バックアップ条件・元NTFS ID・画素/メタデータ再検査合格。" +
                        " 原本置換はまだ無効" :
                        fidelity.Reason;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException or
                System.ComponentModel.Win32Exception or NotSupportedException)
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
