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
        string Explanation)
    {
        // These fields are populated ONLY from the already authenticated
        // PNG/JPEG preview journal and current SHA/decoded-image inspection.
        // They are not a mutation authority: PrepareOneVerifiedPreview repeats
        // the entire report and image/NTFS preflight immediately before copy.
        internal string? VerifiedOriginalPath { get; init; }
        internal string? VerifiedCandidatePath { get; init; }
        internal string? SourceSha256 { get; init; }
        internal string? CandidateSha256 { get; init; }
    }

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
            string? acceptedPath = null, acceptedCandidate = null;
            string? acceptedSourceSha = null, acceptedCandidateSha = null;
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
                    if (eligible)
                    {
                        acceptedPath = path;
                        acceptedCandidate = saved.Path;
                        acceptedSourceSha = saved.SourceSha256;
                        acceptedCandidateSha = saved.CandidateSha256;
                    }
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
                row.CandidateBytes, row.EstimatedSavingBytes, eligible, explanation)
            {
                VerifiedOriginalPath = acceptedPath,
                VerifiedCandidatePath = acceptedCandidate,
                SourceSha256 = acceptedSourceSha,
                CandidateSha256 = acceptedCandidateSha
            });
        }
        return new(candidates, feasible, projected, inputFree, backupFree, different, rows);
    }


    /// <summary>
    /// Production WPF entry point for ONE image COPY-ONLY preparation.
    /// The selected source must appear in the existing PNG/JPEG batch journal.
    /// No filename, SHA or candidate path supplied by the UI is trusted.
    /// Rebuild the actual journal-backed readiness INSIDE this method before
    /// writing any staged or external backup bytes; no original is changed.
    /// The two user-selected output directories must already exist and be
    /// empty (enforced by PrepareVerifiedCopies).
    /// </summary>
    internal static PreparedCopies PrepareOneVerifiedPreview(
        string inputRoot, string outputRoot, string sourcePath,
        string stagingDirectory, string independentBackupDirectory,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string root = Safety.ValidateRoot(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        string original = Path.GetFullPath(sourcePath);
        if (!Inside(original, root) || original.Equals(root,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("選択された画像は検証対象フォルダの外側です");
        // Check for a source reparse point before inspecting a journal.
        JpegBatchPreviewEngine.CheckNoReparse(original);
        var combined = CombinedCapacityReport.Inspect(root, output, ct);
        var review = Inspect(combined, root, output, independentBackupDirectory, ct);
        var matching = review.Rows.Where(x =>
            x.VerifiedOriginalPath is not null &&
            x.VerifiedOriginalPath.Equals(original, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length != 1 || !matching[0].SpaceRequirementsMet ||
            matching[0].VerifiedCandidatePath is null ||
            matching[0].SourceSha256 is not { Length: 64 } ||
            matching[0].CandidateSha256 is not { Length: 64 })
            throw new InvalidDataException(
                "選択された画像の生成時SHA/プレビュー台帳・容量条件が一意に合格していません");
        var accepted = matching[0];
        return PrepareVerifiedCopies(original, accepted.VerifiedCandidatePath,
            stagingDirectory, independentBackupDirectory,
            accepted.SourceSha256, accepted.CandidateSha256, ct);
    }

    // G4-03: the preparation phase is intentionally COPY-ONLY: no input
    // rename, File.Replace, unlink, overwrite, Undo, or recovery authorization.
    // Incomplete copy artifacts are retained for explicit inspection, never
    // auto-deleted after an ambiguous exception or process termination.
    internal sealed record PreparedCopies(
        string OriginalPath, string CandidatePath, string StagedPath,
        string BackupPath, FileSnapshot OriginalEvidence,
        string OriginalSha256, string CandidateSha256,
        string ReceiptPath = "")
    {
        internal bool ProductionReplacementAuthorized => false;
        internal long ActualFreedBytes => 0;
    }

    // A checkpoint stored next to the INDEPENDENT original copy, not a
    // separate mutable ledger. It records enough exact source NTFS evidence to
    // classify interrupted preparations after a process has restarted.
    // This is NOT an authenticated commit log or authority to mutate media.
    private const string G4ReceiptName = ".chappy-g4-preparation.json";
    private const int MaximumG4ReceiptBytes = 128 * 1024;

    private sealed class PreparationReceipt
    {
        public int Schema { get; set; } = 1;
        public FileSnapshot? Original { get; set; }
        public FileSnapshot? Candidate { get; set; }
        public FileSnapshot? Stage { get; set; }
        public FileSnapshot? IndependentBackup { get; set; }
        public string OriginalSha256 { get; set; } = "";
        public string CandidateSha256 { get; set; } = "";
    }

    internal sealed record PreparationInspection(
        string State, string Explanation, PreparedCopies? Verified)
    {
        internal bool ReadyForCutover => false; // Never authorize release Apply.
        internal bool Complete => State == "PreparedVerified" && Verified is not null;
    }

    /// <summary>
    /// Read-only revalidation of the persisted G4 copy-preparation checkpoint.
    /// Nothing is deleted on a missing/truncated journal, a foreign file,
    /// a changed ACL/owner/source, a hardlink alias or changed media bytes.
    /// A valid result is evidence of copies, not authority to replace files.
    /// </summary>
    internal static PreparationInspection InspectPreparedCopies(
        string originalPath, string candidatePath, string stagingDirectory,
        string independentBackupDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string original = Path.GetFullPath(originalPath);
        string candidate = Path.GetFullPath(candidatePath);
        try
        {
            string stageRoot = Safety.ValidateRoot(stagingDirectory);
            string backupRoot = Safety.ValidateRoot(independentBackupDirectory);
            string sourceRoot = Safety.ValidateRoot(Path.GetDirectoryName(original)!);
            string candidateRoot = Safety.ValidateRoot(Path.GetDirectoryName(candidate)!);
            if (Inside(original, stageRoot) || Inside(candidate, stageRoot) ||
                Inside(original, backupRoot) || Inside(candidate, backupRoot) ||
                Inside(stageRoot, sourceRoot) || Inside(stageRoot, candidateRoot) ||
                Inside(backupRoot, sourceRoot) || Inside(backupRoot, candidateRoot) ||
                Inside(stageRoot, backupRoot) || Inside(backupRoot, stageRoot))
                throw new InvalidDataException("作業領域と画像原本・候補の階層が重複");
            var stages = Directory.GetFileSystemEntries(stageRoot);
            var backups = Directory.GetFileSystemEntries(backupRoot);
            if (stages.Length == 0 && backups.Length == 0)
                return new("Empty", "作業前の空フォルダ。原本は変更していません", null);
            string checkpoint = Path.Combine(backupRoot, G4ReceiptName);
            if (stages.Length != 1 || backups.Length != 2 ||
                !File.Exists(checkpoint) || Directory.Exists(checkpoint))
                throw new InvalidDataException("stage/独立backupまたは完了レシートが不足・異物あり");
            if (!Safety.EligibleAttributes(File.GetAttributes(checkpoint)) ||
                NativeFile.GetIdentity(checkpoint).LinkCount != 1 ||
                NativeFile.HasNamedDataStreams(checkpoint))
                throw new InvalidDataException("レシートの属性/共有リンク/ADSが不正");
            using var sourceParents = NativeFile.PinnedDirectories.Open(sourceRoot, [original]);
            using var candidateParents = NativeFile.PinnedDirectories.Open(candidateRoot, [candidate]);
            using var stageParents = NativeFile.PinnedDirectories.Open(stageRoot, []);
            using var backupParents = NativeFile.PinnedDirectories.Open(backupRoot, []);
            using var originalRead = new FileStream(original, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            using var candidateRead = new FileStream(candidate, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            using var journal = new FileStream(checkpoint, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            if (journal.Length is < 128 or > MaximumG4ReceiptBytes)
                throw new InvalidDataException("準備レシートが空/過大/途中終了");
            var record = System.Text.Json.JsonSerializer.Deserialize<PreparationReceipt>(journal)
                ?? throw new InvalidDataException("準備レシートが破損");
            if (record.Schema != 1 ||
                record.Original is null || record.Candidate is null ||
                record.Stage is null || record.IndependentBackup is null ||
                record.OriginalSha256 is not { Length: 64 } ||
                !record.OriginalSha256.All(Uri.IsHexDigit) ||
                record.CandidateSha256 is not { Length: 64 } ||
                !record.CandidateSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("準備レシートの形式/データが不正");
            static bool Eq(string a, string b) =>
                a.Equals(b, StringComparison.OrdinalIgnoreCase);
            if (!Eq(record.Original.Path, original) ||
                !Eq(record.Candidate.Path, candidate) ||
                !Eq(record.Stage.Path, stages[0]) ||
                !backups.Any(x => Eq(x, record.IndependentBackup.Path)) ||
                !backups.Any(x => Eq(x, checkpoint)) ||
                !Inside(record.Stage.Path, stageRoot) ||
                !Inside(record.IndependentBackup.Path, backupRoot) ||
                !Path.GetFileName(record.Stage.Path).StartsWith(
                    ".chappy-g4-stage-", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(record.IndependentBackup.Path).StartsWith(
                    ".chappy-g4-backup-", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(record.IndependentBackup.Path).EndsWith(
                    ".bak", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("レシートが別の画像/バックアップパスを指している");
            var old = record.Original;
            var c = record.Candidate;
            var staged = record.Stage;
            var backup = record.IndependentBackup;
            if (old.Identity.LinkCount != 1 || c.Identity.LinkCount != 1 ||
                staged.Identity.LinkCount != 1 || backup.Identity.LinkCount != 1 ||
                old.Identity.SameFile(c.Identity) ||
                staged.Identity.SameFile(c.Identity) ||
                backup.Identity.VolumeSerial == old.Identity.VolumeSerial ||
                staged.Identity.VolumeSerial != old.Identity.VolumeSerial ||
                staged.CreationUtc != old.CreationUtc ||
                staged.ModifiedUtc != old.ModifiedUtc ||
                staged.Attributes != old.Attributes ||
                !NativeFile.SameOwnerGroupDacl(staged.SecurityDescriptor, old.SecurityDescriptor) ||
                !Safety.SameSnapshot(old) || !Safety.SameSnapshot(c) ||
                !Safety.SameSnapshot(staged) || !Safety.SameSnapshot(backup) ||
                !Eq(Safety.ComputeSha256(original, ct), record.OriginalSha256) ||
                !Eq(Safety.ComputeSha256(candidate, ct), record.CandidateSha256) ||
                !Eq(Safety.ComputeSha256(staged.Path, ct), record.CandidateSha256) ||
                !Eq(Safety.ComputeSha256(backup.Path, ct), record.OriginalSha256) ||
                !Safety.ByteEqual(original, backup.Path, ct) ||
                !Safety.ByteEqual(candidate, staged.Path, ct))
                throw new InvalidDataException("再起動後に原本ID/権限/内容または独立stage/backupが変化");
            var equivalence = InspectCandidatePair(original, staged.Path, ct);
            if (!equivalence.Verified ||
                !Eq(equivalence.OriginalSha256, record.OriginalSha256) ||
                !Eq(equivalence.CandidateSha256, record.CandidateSha256))
                throw new InvalidDataException("再起動後の画像画素/メタデータ一致を証明できない");
            sourceParents.AssertUnchanged();
            candidateParents.AssertUnchanged();
            stageParents.AssertUnchanged();
            backupParents.AssertUnchanged();
            var proof = new PreparedCopies(original, candidate, staged.Path,
                backup.Path, old, record.OriginalSha256, record.CandidateSha256,
                checkpoint);
            return new("PreparedVerified",
                "独立バックアップ・原本所有者/DACL/NTFS ID・stage画像を再照合。" +
                "原本切替と自動復元は許可されていません", proof);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or OverflowException or
            System.Text.Json.JsonException or NullReferenceException or
            System.ComponentModel.Win32Exception or
            System.Runtime.InteropServices.COMException)
        {
            return new("ManualReview",
                "変更しません。孤立・不一致・未証明のバックアップ/stage: " +
                ex.GetType().Name + " / " + ex.Message, null);
        }
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
        // Never treat a dirty stage/backup directory as ours. Incomplete
        // artifacts from an earlier crash require manual review; no auto
        // overwrite, removal, or resumption without a durable journal.
        if (Directory.EnumerateFileSystemEntries(stageRoot).Any() ||
            Directory.EnumerateFileSystemEntries(backupRoot).Any())
            throw new InvalidDataException(
                "stage/backupに既存ファイルがあります。異物または中断証拠として停止");
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

        // One final empty-directory proof before writing the first byte.
        stageParents.AssertUnchanged();
        backupParents.AssertUnchanged();
        if (Directory.EnumerateFileSystemEntries(stageRoot).Any() ||
            Directory.EnumerateFileSystemEntries(backupRoot).Any())
            throw new InvalidDataException("コピー開始前にstage/backupへ異物が追加された");
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
        // Different NTFS volumes can have DIFFERENT inheritable ACLs. Do
        // not force source DACL onto the independent backup filesystem: doing
        // so could change backup access protections or leave the wrong
        // inherited rights. Record its actual ID/DACL/metadata, plus the
        // ORIGINAL owner/group/DACL/timestamps in the durable receipt. The
        // copy is byte-identical, and replay checks both separate snapshots.
        // Only the same-volume cutover stage must match original metadata.
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
        // File.Replace may inherit or merge security from the destination.
        // Never enter cutover with an image stage whose owner, DACL, attributes
        // or creation/modified times differ from those of the original.
        ApplyOriginalMetadataToNewCopy(original, staged, originalSnapshot);
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
        // Flush a single final checkpoint adjacent to the independent copy
        // only AFTER both data copies passed full byte/image verification.
        // This records original NTFS owner/DACL/creation/modified timestamps,
        // ID, link count and contents for read-only post-restart classification.
        // A crash before this receipt leaves orphans requiring manual review.
        var receipt = new PreparationReceipt
        {
            Original = originalSnapshot, Candidate = candidateSnapshot,
            Stage = stagedSnapshot, IndependentBackup = copy,
            OriginalSha256 = expectedSourceSha256,
            CandidateSha256 = expectedCandidateSha256
        };
        byte[] recordBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(receipt);
        if (recordBytes.Length > MaximumG4ReceiptBytes || recordBytes.Length < 128)
            throw new InvalidDataException("元ID/権限を保持したレシートが過大/不正");
        string receiptPath = Path.Combine(backupRoot, G4ReceiptName);
        using (var recordStream = new FileStream(receiptPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
        {
            recordStream.Write(recordBytes);
            recordStream.Flush(flushToDisk: true);
        }
        var post = InspectPreparedCopies(original, candidate, stageRoot, backupRoot, ct);
        if (!post.Complete || post.Verified is null ||
            !post.Verified.OriginalSha256.Equals(expectedSourceSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !post.Verified.CandidateSha256.Equals(expectedCandidateSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("作成済みG4レシートの独立再検証に失敗");
        return post.Verified;
    }

    /// <summary>
    /// G4-03 metadata preservation on a NEW, independent file only.
    /// Owner/group are not changed: if they differ, the native method refuses
    /// that unsupported case. This cannot modify the original media path.
    /// </summary>
    private static void ApplyOriginalMetadataToNewCopy(
        string original, string target, FileSnapshot source)
    {
        var before = Safety.Capture(target);
        if (before.Identity.LinkCount != 1 ||
            before.Identity.SameFile(source.Identity))
            throw new InvalidDataException("New independent stage must have its own NTFS ID");
        NativeFile.CopyOwnerGroupDaclToNewFile(original, target, before.Identity);
        File.SetCreationTimeUtc(target, source.CreationUtc);
        File.SetLastWriteTimeUtc(target, source.ModifiedUtc);
        File.SetAttributes(target, source.Attributes);
        var copied = Safety.Capture(target);
        if (!copied.Identity.SameFile(before.Identity) ||
            copied.Identity.LinkCount != 1 ||
            copied.CreationUtc != source.CreationUtc ||
            copied.ModifiedUtc != source.ModifiedUtc ||
            copied.Attributes != source.Attributes ||
            !NativeFile.SameOwnerGroupDacl(copied.SecurityDescriptor, source.SecurityDescriptor) ||
            !Safety.SameSnapshot(source))
            throw new InvalidDataException(
                "Could not reproduce source NTFS owner/group/DACL/timestamps on independent image");
    }

#if CHAPPY_TESTING
    // G4-03/04 isolated REAL-PNG cutover experiment. This code is absent from
    // the shipping WPF assembly. No consumer media may enter these methods.
    // All NTFS renames are via an already-open, identity-bound PinnedFile
    // handle; target replacement and automatic deletion are forbidden.
    private sealed record SyntheticImagePhase(
        PreparationReceipt Receipt, string Original, string Candidate,
        string Stage, string Backup, string Rollback, string Quarantine,
        string State);

    private static void RequireGeneratedImageScope(string original, string candidate,
        string stageRoot, string backupRoot)
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("G4 cutover is synthetic Windows CI ONLY");
        static bool Eq(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string dir = Path.GetDirectoryName(original) ??
            throw new InvalidDataException("Missing PNG parent");
        string root = Path.GetDirectoryName(dir) ??
            throw new InvalidDataException("Missing generated smoke root");
        string name = Path.GetFileName(root);
        if (!name.StartsWith("ChappyStorageSmoke_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(name["ChappyStorageSmoke_".Length..], "N", out _) ||
            !root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Eq(original, Path.Combine(root, "r16-variants", "rgba-multi.png")) ||
            !candidate.StartsWith(
                Path.Combine(root, "r16-variants-output") +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !candidate.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(stageRoot)!.Equals(root,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(stageRoot).StartsWith("g4-cutover-",
                StringComparison.Ordinal) ||
            !Directory.Exists(stageRoot) ||
            !Directory.Exists(backupRoot))
            throw new InvalidDataException("G4 requires exact generated PNG smoke paths");
        string backupParent = Path.GetDirectoryName(backupRoot) ??
            throw new InvalidDataException("Missing generated backup parent");
        string backupName = Path.GetFileName(backupParent);
        if (!backupName.StartsWith("ChappyStorageSmoke_G4Backup_", StringComparison.Ordinal) ||
            !Guid.TryParseExact(backupName["ChappyStorageSmoke_G4Backup_".Length..],
                "N", out _) ||
            !Path.GetFileName(backupRoot).StartsWith("cutover-", StringComparison.Ordinal) ||
            !File.Exists(Path.Combine(backupParent, ".chappy-g4-generated-only")))
            throw new InvalidDataException("G4 independent backup test sentinel missing");
        Safety.ValidateRoot(dir);
        Safety.ValidateRoot(stageRoot);
        Safety.ValidateRoot(backupRoot);
    }

    private static SyntheticImagePhase InspectSyntheticPhase(string originalPath,
        string candidatePath, string stagingDirectory, string backupDirectory)
    {
        string original = Path.GetFullPath(originalPath);
        string candidate = Path.GetFullPath(candidatePath);
        string stageRoot = Path.GetFullPath(stagingDirectory);
        string backupRoot = Path.GetFullPath(backupDirectory);
        RequireGeneratedImageScope(original, candidate, stageRoot, backupRoot);
        string receiptPath = Path.Combine(backupRoot, G4ReceiptName);
        if (!File.Exists(receiptPath) ||
            !Safety.EligibleAttributes(File.GetAttributes(receiptPath)) ||
            NativeFile.GetIdentity(receiptPath).LinkCount != 1 ||
            NativeFile.HasNamedDataStreams(receiptPath) ||
            new FileInfo(receiptPath).Length is < 128 or > MaximumG4ReceiptBytes)
            throw new InvalidDataException("Untrusted G4 preparation receipt");
        var record = System.Text.Json.JsonSerializer.Deserialize<PreparationReceipt>(
            File.ReadAllBytes(receiptPath)) ??
            throw new InvalidDataException("Missing G4 receipt");
        if (record.Schema != 1 ||
            record.Original is null || record.Candidate is null ||
            record.Stage is null || record.IndependentBackup is null ||
            record.OriginalSha256 is not { Length: 64 } ||
            record.CandidateSha256 is not { Length: 64 } ||
            !record.OriginalSha256.All(Uri.IsHexDigit) ||
            !record.CandidateSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid G4 receipt evidence");
        static bool Eq(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase);
        var old = record.Original;
        var preview = record.Candidate;
        var staged = record.Stage;
        var backup = record.IndependentBackup;
        if (!Eq(old.Path, original) || !Eq(preview.Path, candidate) ||
            !Eq(Path.GetDirectoryName(staged.Path)!, stageRoot) ||
            !Eq(Path.GetDirectoryName(backup.Path)!, backupRoot) ||
            old.Identity.LinkCount != 1 || preview.Identity.LinkCount != 1 ||
            staged.Identity.LinkCount != 1 || backup.Identity.LinkCount != 1 ||
            old.Identity.VolumeSerial != staged.Identity.VolumeSerial ||
            old.Identity.VolumeSerial == backup.Identity.VolumeSerial ||
            old.Identity.SameFile(staged.Identity) ||
            !Path.GetFileName(staged.Path).StartsWith(
                ".chappy-g4-stage-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(backup.Path).StartsWith(
                ".chappy-g4-backup-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("G4 receipt paths or NTFS identities disagree");
        string stage = staged.Path;
        string oldName = Path.GetFileNameWithoutExtension(stage);
        string id = oldName[".chappy-g4-stage-".Length..];
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("G4 synthetic stage ID invalid");
        string rollback = original + ".chappy-g4-old-" + id + ".rollback";
        string quarantine = original + ".chappy-g4-optimized-" + id + ".quarantine";
        if (!Safety.SameSnapshot(preview) || !Safety.SameSnapshot(backup) ||
            !Eq(Safety.ComputeSha256(candidate, CancellationToken.None,
                    allowDeleteSharing: true),
                record.CandidateSha256) ||
            !Eq(Safety.ComputeSha256(backup.Path, CancellationToken.None,
                    allowDeleteSharing: true),
                record.OriginalSha256))
            throw new InvalidDataException("G4 preview or external independent backup changed");
        var stageFiles = Directory.GetFileSystemEntries(stageRoot);
        var backupFiles = Directory.GetFileSystemEntries(backupRoot);
        if (stageFiles.Length != (File.Exists(stage) ? 1 : 0) ||
            (stageFiles.Length == 1 && !Eq(stageFiles[0], stage)) ||
            backupFiles.Length != 2 ||
            !backupFiles.Any(x => Eq(x, receiptPath)) ||
            !backupFiles.Any(x => Eq(x, backup.Path)))
            throw new InvalidDataException("G4 synthetic stage/backup foreign or orphan paths");
        bool hasOriginal = File.Exists(original);
        bool hasStage = File.Exists(stage);
        bool hasRollback = File.Exists(rollback);
        bool hasQuarantine = File.Exists(quarantine);
        // A directory at any transaction name is a foreign collision.
        if (new[] { original, stage, rollback, quarantine }.Any(Directory.Exists))
            throw new InvalidDataException("G4 transaction path occupied by a directory");
        bool IsOld(string at) =>
            Safety.SameRenamedSnapshot(old, at) &&
            Eq(Safety.ComputeSha256(at, CancellationToken.None,
                    allowDeleteSharing: true),
                record.OriginalSha256) &&
            Safety.ByteEqual(at, backup.Path, CancellationToken.None,
                allowDeleteSharing: true);
        bool IsOptimized(string at)
        {
            var actual = Safety.Capture(at);
            // NTFS short-name/file-name tunneling: when a staged file is
            // renamed into the recently vacated original name, NTFS can
            // inherit the ORIGINAL file's creation timestamp. The real
            // Windows CI proved that ID, bytes, owner/DACL, attributes and
            // modified UTC all remain identical; ONLY CreationUtc changes.
            // Do not drop the creation-time check: accept exactly the staged
            // timestamp, or (only after a rename) the saved ORIGINAL time.
            bool allowedCreation = actual.CreationUtc == staged.CreationUtc ||
                (!Eq(at, stage) && actual.CreationUtc == old.CreationUtc);
            return actual.Identity.SameFile(staged.Identity) &&
                actual.Identity.LinkCount == 1 &&
                actual.Length == staged.Length &&
                allowedCreation &&
                actual.ModifiedUtc == staged.ModifiedUtc &&
                actual.Attributes == staged.Attributes &&
                actual.SecurityDescriptor.AsSpan().SequenceEqual(staged.SecurityDescriptor) &&
                Eq(Safety.ComputeSha256(at, CancellationToken.None,
                    allowDeleteSharing: true), record.CandidateSha256) &&
                Safety.ByteEqual(at, candidate, CancellationToken.None,
                    allowDeleteSharing: true);
        }
        string phase;
        if (hasOriginal && hasStage && !hasRollback && !hasQuarantine &&
            IsOld(original) && IsOptimized(stage))
            phase = "Prepared";
        else if (!hasOriginal && hasStage && hasRollback && !hasQuarantine &&
            IsOld(rollback) && IsOptimized(stage))
            phase = "OriginalSetAside";
        else if (hasOriginal && !hasStage && hasRollback && !hasQuarantine &&
            IsOld(rollback) && IsOptimized(original))
            phase = "CandidateAtOriginal";
        else if (!hasOriginal && !hasStage && hasRollback && hasQuarantine &&
            IsOld(rollback) && IsOptimized(quarantine))
            phase = "CandidateQuarantined";
        else if (hasOriginal && !hasStage && !hasRollback && hasQuarantine &&
            IsOld(original) && IsOptimized(quarantine))
            phase = "OriginalRestored";
        else
        {
            // CI-only diagnostic: a cross-directory NTFS rename can alter
            // timestamps or security on the moved candidate. Never guess the
            // winner or silently relax the guard before recording real values.
            static string Probe(string path, FileSnapshot expected, string hash)
            {
                if (!File.Exists(path)) return "absent";
                try
                {
                    var actual = Safety.Capture(path);
                    return $"id={actual.Identity.SameFile(expected.Identity)} " +
                        $"links={actual.Identity.LinkCount} " +
                        $"len={actual.Length == expected.Length} " +
                        $"creation={actual.CreationUtc.Ticks == expected.CreationUtc.Ticks} " +
                        $"modified={actual.ModifiedUtc.Ticks == expected.ModifiedUtc.Ticks} " +
                        $"attrs={actual.Attributes == expected.Attributes} " +
                        $"ownerDacl={actual.SecurityDescriptor.AsSpan().SequenceEqual(expected.SecurityDescriptor)} " +
                        $"sha={string.Equals(Safety.ComputeSha256(path, CancellationToken.None), hash, StringComparison.OrdinalIgnoreCase)} " +
                        $"actualCreation={actual.CreationUtc:O} expectedCreation={expected.CreationUtc:O}";
                }
                catch (Exception ex) { return ex.GetType().Name; }
            }
            throw new InvalidDataException(
                $"G4 cutover evidence ambiguous; preserve artifacts. " +
                $"source={hasOriginal},stage={hasStage},rollback={hasRollback},quarantine={hasQuarantine}; " +
                "old@" + (hasRollback ? "rollback=" + Probe(rollback, old, record.OriginalSha256) :
                    hasOriginal ? "original=" + Probe(original, old, record.OriginalSha256) : "absent") +
                "; optimized@" + (hasOriginal ? "original=" + Probe(original, staged, record.CandidateSha256) :
                    hasQuarantine ? "quarantine=" + Probe(quarantine, staged, record.CandidateSha256) :
                    hasStage ? "stage=" + Probe(stage, staged, record.CandidateSha256) : "absent"));
        }
        return new(record, original, candidate, stage, backup.Path,
            rollback, quarantine, phase);
    }

    /// <summary>
    /// CI-only, generated PNG. Unlinks no data; renames the authenticated
    /// original to a same-parent rollback name, then moves an authenticated
    /// staged PNG into the vacated original name by pinned NTFS file ID.
    /// Both operations have no-replace semantics. Persistent receipt already
    /// exists BEFORE either rename and remains untouched on interruption.
    /// </summary>
    internal static void CutoverGeneratedPngSynthetic(string original,
        string candidate, string stageRoot, string backupRoot)
    {
        RequireGeneratedImageScope(Path.GetFullPath(original),
            Path.GetFullPath(candidate), Path.GetFullPath(stageRoot),
            Path.GetFullPath(backupRoot));
        var ready = InspectPreparedCopies(original, candidate, stageRoot, backupRoot);
        if (!ready.Complete)
            throw new InvalidDataException("Unverified image preparation: " + ready.State);
        using var sourceParents = NativeFile.PinnedDirectories.Open(
            Path.GetDirectoryName(Path.GetFullPath(original))!, [original]);
        using var stageParents = NativeFile.PinnedDirectories.Open(stageRoot,
            [ready.Verified!.StagedPath]);
        using var backupParents = NativeFile.PinnedDirectories.Open(backupRoot, []);
        var p = InspectSyntheticPhase(original, candidate, stageRoot, backupRoot);
        if (p.State != "Prepared")
            throw new InvalidDataException("Expected prepared original ID and staged PNG");
        using var sourcePinned = NativeFile.PinnedFile.Open(p.Original,
            p.Receipt.Original!.Identity, p.Receipt.OriginalSha256);
        using var stagePinned = NativeFile.PinnedFile.Open(p.Stage,
            p.Receipt.Stage!.Identity, p.Receipt.CandidateSha256);
        if (!Safety.SameSnapshot(p.Receipt.Original) ||
            !Safety.SameSnapshot(p.Receipt.Stage) ||
            !sourcePinned.PathStillMatches(p.Original) ||
            !stagePinned.PathStillMatches(p.Stage))
            throw new InvalidDataException("Source/stage changed before synthetic cutover");
        sourceParents.AssertUnchanged();
        stageParents.AssertUnchanged();
        backupParents.AssertUnchanged();
        sourcePinned.RenameNoReplace(p.Rollback);
        FaultInjection.Hit("g4-after-original-rename");
        if (!Safety.SameRenamedSnapshot(p.Receipt.Original, p.Rollback) ||
            !stagePinned.PathStillMatches(p.Stage))
            throw new InvalidDataException("Unsafe intermediate G4 original-id state");
        stagePinned.RenameNoReplace(p.Original);
        FaultInjection.Hit("g4-after-stage-rename");
        if (InspectSyntheticPhase(original, candidate, stageRoot, backupRoot).State !=
            "CandidateAtOriginal")
            throw new InvalidDataException("G4 synthetic cutover verification failed");
        // No commit or release authorization: every synthetic cutover must
        // be restored from the original file ID before its test ends.
    }

    /// <summary>
    /// CI-only replay after abrupt process death. Infer phase solely from
    /// original ID + staged ID + independent verified backup + names; no
    /// destructive cleanup. A foreign file or insufficient proof aborts.
    /// </summary>
    internal static string RestoreGeneratedPngSynthetic(string original,
        string candidate, string stageRoot, string backupRoot)
    {
        RequireGeneratedImageScope(Path.GetFullPath(original),
            Path.GetFullPath(candidate), Path.GetFullPath(stageRoot),
            Path.GetFullPath(backupRoot));
        using var sourceParents = NativeFile.PinnedDirectories.Open(
            Path.GetDirectoryName(Path.GetFullPath(original))!, [original]);
        using var stageParents = NativeFile.PinnedDirectories.Open(stageRoot, []);
        using var backupParents = NativeFile.PinnedDirectories.Open(backupRoot, []);
        var p = InspectSyntheticPhase(original, candidate, stageRoot, backupRoot);
        if (p.State == "Prepared") return "OriginalUnchanged";
        if (p.State == "OriginalRestored") return "OriginalRestored";
        if (p.State == "CandidateAtOriginal")
        {
            using var candidatePinned = NativeFile.PinnedFile.Open(p.Original,
                p.Receipt.Stage!.Identity, p.Receipt.CandidateSha256);
            sourceParents.AssertUnchanged();
            candidatePinned.RenameNoReplace(p.Quarantine);
            FaultInjection.Hit("g4-after-candidate-quarantine");
            p = InspectSyntheticPhase(original, candidate, stageRoot, backupRoot);
        }
        if (p.State is not ("OriginalSetAside" or "CandidateQuarantined"))
            throw new InvalidDataException("G4 recovery state changed unexpectedly");
        using (var oldPinned = NativeFile.PinnedFile.Open(p.Rollback,
            p.Receipt.Original!.Identity, p.Receipt.OriginalSha256))
        {
            sourceParents.AssertUnchanged();
            oldPinned.RenameNoReplace(p.Original);
        }
        FaultInjection.Hit("g4-after-original-restored");
        var final = InspectSyntheticPhase(original, candidate, stageRoot, backupRoot);
        if (final.State is not ("Prepared" or "OriginalRestored") ||
            !Safety.SameSnapshot(p.Receipt.Original))
            throw new InvalidDataException("G4 original restoration proof failed");
        return final.State;
    }

    internal static string InspectGeneratedPngSynthetic(string original,
        string candidate, string stageRoot, string backupRoot)
    {
        RequireGeneratedImageScope(Path.GetFullPath(original),
            Path.GetFullPath(candidate), Path.GetFullPath(stageRoot),
            Path.GetFullPath(backupRoot));
        return InspectSyntheticPhase(original, candidate, stageRoot, backupRoot).State;
    }
#endif

    private static bool Inside(string path, string parent)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return p.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
