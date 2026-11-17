namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.2/G4 advisory preflight plus G4-03 COPY-ONLY preparation.
/// Inspection never writes. Explicit internal preparation only creates NEW
/// files in user-selected independent staging/backup directories; it never
/// renames, deletes, moves or replaces an original image.
/// No return value grants source replacement, Undo or physical recovery.
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
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or OverflowException or
            System.ComponentModel.Win32Exception or
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
            long CandidateBytes, string? SourceSha256, string? CandidateSha256)>(
                StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Path.Combine(output, ".chappy-png-batch-r16.json")))
        {
            foreach (var candidate in PngBatchJournalTools.ReadAudit(input, output, ct).Rows)
                if (candidate.State == "CandidatePresent")
                    candidatesByKey.Add("PNG|" + candidate.RelativePath,
                        (candidate.PreviewPath, candidate.SourceBytes, candidate.PreviewBytes,
                         candidate.SourceSha256, candidate.PreviewSha256));
        }
        string jpegPlan = Path.Combine(output, JpegBatchPreviewEngine.PlanName);
        if (File.Exists(jpegPlan))
        {
            var plan = JpegBatchPreviewEngine.LoadPlan(jpegPlan);
            foreach (var candidate in JpegBatchPreviewEngine.Inspect(
                         input, output, plan.ToolPath, ct).Rows)
                if (candidate.Status == "Completed" && candidate.VerifiedSavingBytes > 0)
                    candidatesByKey.Add("JPG|" + candidate.RelativePath,
                        (candidate.CandidatePath, candidate.SourceBytes, candidate.CandidateBytes,
                         candidate.SourceSha256, candidate.CandidateSha256));
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
                        !Inside(saved.Path, output) ||
                        saved.SourceSha256 is not { Length: 64 } ||
                        saved.CandidateSha256 is not { Length: 64 })
                        throw new InvalidDataException(
                            "候補台帳の保存先・サイズ・生成時原本SHA証拠が一致しません");
                    var fidelity = InspectCandidatePair(path, saved.Path, ct);
                    eligible = fidelity.Verified &&
                        fidelity.OriginalSha256.Equals(saved.SourceSha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        fidelity.CandidateSha256.Equals(saved.CandidateSha256,
                            StringComparison.OrdinalIgnoreCase);
                    explanation = eligible ?
                        "容量/独立バックアップ条件・候補生成時の原本全バイト・" +
                        "元NTFS ID・画素/メタデータ再検査合格。原本置換はまだ無効" :
                        "生成時の原本/候補SHAと現在の実データが不一致、または" +
                        "候補再検証不合格: " + fidelity.Reason;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or System.ComponentModel.Win32Exception or
                NotSupportedException)
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


    // G4-03: the preparation phase is intentionally COPY-ONLY: no input
    // rename, File.Replace, unlink, overwrite, Undo, or recovery authorization.
    // Incomplete copy artifacts are retained for explicit inspection, never
    // auto-deleted after an ambiguous exception or process termination.
    internal sealed record PreparedCopies(
        string OriginalPath, string CandidatePath, string StagedPath,
        string BackupPath, FileSnapshot OriginalEvidence,
        string OriginalSha256, string CandidateSha256)
    {
        internal bool ProductionReplacementAuthorized => false;
        internal long ActualFreedBytes => 0;
    }

    /// <summary>
    /// Copy an independently verified image pair to (1) a staging directory on
    /// the SOURCE NTFS volume and (2) a backup directory on a DIFFERENT local
    /// NTFS volume. Both destinations must already exist and have no junction
    /// ancestors. The only writes are to newly named files in these two
    /// directories; the original image and saved preview are never modified.
    ///
    /// NOT a durable transaction or a release Apply path: the existing
    /// crash-safe journal, exact NTFS metadata restoration and path-ID-bound
    /// cutover still need to be integrated and tested before any source change.
    /// </summary>
    internal static PreparedCopies PrepareVerifiedCopies(
        string originalPath, string candidatePath,
        string stagingDirectory, string independentBackupDirectory,
        string expectedSourceSha256, string expectedCandidateSha256,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("NTFS画像ステージはWindows専用");
        ct.ThrowIfCancellationRequested();
        if (expectedSourceSha256 is not { Length: 64 } ||
            !expectedSourceSha256.All(Uri.IsHexDigit) ||
            expectedCandidateSha256 is not { Length: 64 } ||
            !expectedCandidateSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("生成時の原本・候補SHAが不足または不正");
        string original = Path.GetFullPath(originalPath);
        string candidate = Path.GetFullPath(candidatePath);
        string stageRoot = Safety.ValidateRoot(stagingDirectory);
        string backupRoot = Safety.ValidateRoot(independentBackupDirectory);
        string sourceRoot = Safety.ValidateRoot(Path.GetDirectoryName(original)!);
        string candidateRoot = Safety.ValidateRoot(Path.GetDirectoryName(candidate)!);
        if (Inside(original, stageRoot) || Inside(candidate, stageRoot) ||
            Inside(stageRoot, sourceRoot) || Inside(stageRoot, candidateRoot) ||
            Inside(original, backupRoot) || Inside(candidate, backupRoot) ||
            Inside(backupRoot, sourceRoot) || Inside(backupRoot, candidateRoot) ||
            Inside(stageRoot, backupRoot) || Inside(backupRoot, stageRoot))
            throw new InvalidOperationException(
                "原本・候補・stage・別ボリュームbackupは重ならない独立フォルダが必要");
        JpegBatchPreviewEngine.CheckNoReparse(original);
        JpegBatchPreviewEngine.CheckNoReparse(candidate);
        var originalDrive = new DriveInfo(Path.GetPathRoot(original)!);
        var stageDrive = new DriveInfo(Path.GetPathRoot(stageRoot)!);
        var backupDrive = new DriveInfo(Path.GetPathRoot(backupRoot)!);
        if (!originalDrive.IsReady || !stageDrive.IsReady || !backupDrive.IsReady ||
            !originalDrive.Name.Equals(stageDrive.Name, StringComparison.OrdinalIgnoreCase) ||
            originalDrive.Name.Equals(backupDrive.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "stageは元NTFSボリューム、原本バックアップは別NTFSボリューム必須");

        // Pin the parent and ancestor directory IDs for all four locations.
        // Failed pinning must STOP before allocating a destination file.
        using var sourceParents = NativeFile.PinnedDirectories.Open(sourceRoot, [original]);
        using var candidateParents = NativeFile.PinnedDirectories.Open(candidateRoot, [candidate]);
        using var stageParents = NativeFile.PinnedDirectories.Open(stageRoot, []);
        using var backupParents = NativeFile.PinnedDirectories.Open(backupRoot, []);
        var originalSnapshot = Safety.Capture(original);
        var candidateSnapshot = Safety.Capture(candidate);
        if (originalSnapshot.Identity.LinkCount != 1 ||
            candidateSnapshot.Identity.LinkCount != 1 ||
            originalSnapshot.Identity.SameFile(candidateSnapshot.Identity))
            throw new InvalidDataException("外部ハードリンク・元候補IDの共有を拒否");
        var capacity = EvaluateCapacity(originalSnapshot.Length,
            candidateSnapshot.Length, stageDrive.AvailableFreeSpace,
            backupDrive.AvailableFreeSpace, differentVolume: true);
        if (!capacity.MeetsSpaceRequirements)
            throw new IOException("stage/独立バックアップの容量不足: " + capacity.Explanation);
        var fidelity = InspectCandidatePair(original, candidate, ct);
        if (!fidelity.Verified ||
            !fidelity.OriginalSha256.Equals(expectedSourceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !fidelity.CandidateSha256.Equals(expectedCandidateSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("保存台帳と原本・候補の全バイト/画像検証が不一致");
        // Keep both source names and bytes unavailable to ordinary external
        // writers/rename for the entire independent-copy preparation.
        using var sourceLock = new FileStream(original, FileMode.Open,
            FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        using var candidateLock = new FileStream(candidate, FileMode.Open,
            FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (sourceLock.Length != originalSnapshot.Length ||
            candidateLock.Length != candidateSnapshot.Length ||
            !Safety.SameSnapshot(originalSnapshot) ||
            !Safety.SameSnapshot(candidateSnapshot))
            throw new InvalidDataException("コピー前に原本または候補ID/権限が変化");
        sourceParents.AssertUnchanged();
        candidateParents.AssertUnchanged();
        stageParents.AssertUnchanged();
        backupParents.AssertUnchanged();

        string id = Guid.NewGuid().ToString("N");
        string staged = Path.Combine(stageRoot,
            ".chappy-g4-stage-" + id + Path.GetExtension(candidate));
        string backedUp = Path.Combine(backupRoot, ".chappy-g4-backup-" + id + ".bak");
        // These are CreateNew operations only. On a crash both copies remain
        // as evidence; the future persistent transaction will classify orphans.
        static void FlushVerifiedCopy(FileStream input, string destination,
            CancellationToken token)
        {
            input.Position = 0;
            using var output = new FileStream(destination, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 256 * 1024,
                FileOptions.WriteThrough | FileOptions.SequentialScan);
            byte[] buffer = new byte[256 * 1024];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
            }
            output.Flush(flushToDisk: true);
        }
        FlushVerifiedCopy(sourceLock, backedUp, ct);
        // Verify the independent copy before even starting to stage candidate.
        var copy = Safety.Capture(backedUp);
        if (copy.Length != originalSnapshot.Length || copy.Identity.LinkCount != 1 ||
            copy.Identity.VolumeSerial == originalSnapshot.Identity.VolumeSerial ||
            !string.Equals(Safety.ComputeSha256(backedUp, ct), expectedSourceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !Safety.ByteEqual(original, backedUp, ct))
            throw new InvalidDataException(
                "別NTFSボリュームの原本バックアップが独立全バイト一致でない");
        FlushVerifiedCopy(candidateLock, staged, ct);
        var stagedSnapshot = Safety.Capture(staged);
        if (stagedSnapshot.Length != candidateSnapshot.Length ||
            stagedSnapshot.Identity.LinkCount != 1 ||
            stagedSnapshot.Identity.VolumeSerial != originalSnapshot.Identity.VolumeSerial ||
            !string.Equals(Safety.ComputeSha256(staged, ct), expectedCandidateSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !Safety.ByteEqual(candidate, staged, ct))
            throw new InvalidDataException("元NTFS stageの独立バイト照合に失敗");

        // The new staging image must still meet the complete PNG/JPEG pixel
        // and metadata audit. No source rename/write is attempted.
        var stagedFidelity = InspectCandidatePair(original, staged, ct);
        sourceParents.AssertUnchanged();
        candidateParents.AssertUnchanged();
        stageParents.AssertUnchanged();
        backupParents.AssertUnchanged();
        if (!stagedFidelity.Verified ||
            !stagedFidelity.OriginalSha256.Equals(expectedSourceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !stagedFidelity.CandidateSha256.Equals(expectedCandidateSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !Safety.SameSnapshot(originalSnapshot) ||
            !Safety.SameSnapshot(candidateSnapshot) ||
            !Safety.SameSnapshot(copy) || !Safety.SameSnapshot(stagedSnapshot) ||
            !string.Equals(Safety.ComputeSha256(original, ct), expectedSourceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(candidate, ct), expectedCandidateSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("コピー後の原本/候補/backup/stage証拠が不一致");
        return new PreparedCopies(original, candidate, staged, backedUp,
            originalSnapshot, expectedSourceSha256, expectedCandidateSha256);
    }

    private static bool Inside(string path, string parent)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
