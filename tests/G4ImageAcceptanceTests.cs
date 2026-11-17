using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using ChappyStorageCompressor.Core;
using Microsoft.Win32.SafeHandles;

internal static class G4ImageAcceptanceTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StandardFileInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,
        int infoClass, out StandardFileInfo info, uint size);

    private static long Allocation(string path)
    {
        using var file = File.OpenRead(path);
        const int fileStandardInfo = 1;
        if (!GetFileInformationByHandleEx(file.SafeFileHandle, fileStandardInfo,
            out var info, (uint)Marshal.SizeOf<StandardFileInfo>()))
            throw new IOException("Could not read NTFS file allocation.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        return info.AllocationSize;
    }

    internal static void ExpectProcessExit(string source, string candidate, string stage,
        string backup, string checkpoint, bool restoring, Action<bool, string> check)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { typeof(G4ImageAcceptanceTests).Assembly.Location,
            restoring ? "--g4-pinned-restore-child" : "--g4-pinned-cutover-child",
            source, candidate, stage, backup, checkpoint })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)
            ?? throw new IOException("G4 image worker failed to start");
        var error = child.StandardError.ReadToEndAsync();
        if (!child.WaitForExit(120_000))
        {
            child.Kill(entireProcessTree: true);
            throw new TimeoutException("G4 image worker timed out");
        }
        check(child.ExitCode == 94,
            $"G4-04: real process exit at {checkpoint} / {error.GetAwaiter().GetResult()}");
    }

    internal static void Run(string root, string? tool, Action<bool, string> check)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            return;
        if (tool is null)
            throw new InvalidOperationException("G4 CI needs its source-pinned jpegtran.");
        string sourceVolume = Path.GetPathRoot(root)!;
        string backupParent = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
            .OfType<string>().First(path => Directory.Exists(path) &&
                !string.Equals(Path.GetPathRoot(path), sourceVolume, StringComparison.OrdinalIgnoreCase) &&
                new DriveInfo(Path.GetPathRoot(path)!).DriveFormat == "NTFS");
        string backupRoot = Path.Combine(backupParent,
            "ChappyStorageSmoke_G4Backup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupRoot);
        try
        {
            File.WriteAllText(Path.Combine(backupRoot, ".chappy-g4-generated-only"),
                "CHAPPY-G4-GENERATED-ONLY");
            RunJpegAcceptance(root, backupRoot, tool, check);
        }
        finally
        {
            Directory.Delete(backupRoot, recursive: true); // This fixture owns the fresh directory.
        }
    }

    private static void RunJpegAcceptance(string root, string backupRoot, string tool,
        Action<bool, string> check)
    {
        string input = Directory.CreateDirectory(Path.Combine(root, "g4-jpeg-input")).FullName;
        string output = Directory.CreateDirectory(Path.Combine(root, "g4-jpeg-output")).FullName;
        string source = Path.Combine(input, "g4.jpg");
        R217ValidImageBatchProfile.WriteGeneratedJpeg(source, 1536, 1024);
        var preview = JpegLosslessPreviewEngine.CreatePreview(source, output, tool);
        string candidate = preview.Candidate ?? throw new InvalidDataException(preview.Explanation);
        var fidelity = ReplacementReadiness.InspectCandidatePair(source, candidate);
        check(fidelity.Verified, "G4-03: source-pinned jpegtran produced a verified JPEG");
        var original = Safety.Capture(source);
        var drive = new DriveInfo(Path.GetPathRoot(source)!);
        long freeBeforePreparation = drive.AvailableFreeSpace;
        long originalAllocation = Allocation(source);
        string stage = Directory.CreateDirectory(Path.Combine(root, "g4-cutover-jpeg")).FullName;
        string backup = Directory.CreateDirectory(Path.Combine(backupRoot, "cutover-jpeg")).FullName;
        var prepared = ReplacementReadiness.PrepareVerifiedCopies(source, candidate, stage, backup,
            fidelity.OriginalSha256, fidelity.CandidateSha256);
        long stageAllocation = Allocation(prepared.StagedPath);

        ExpectProcessExit(source, candidate, stage, backup, "g4-after-stage-rename", false, check);
        check(ReplacementReadiness.InspectGeneratedImageSynthetic(source, candidate, stage, backup) ==
            "CandidateAtOriginal", "G4-04: JPEG swap before completion record needs recovery");
        check(ReplacementReadiness.RestorePreparedImage(source, candidate, stage, backup) ==
            "OriginalRestored" && Safety.SameSnapshot(original) &&
            Safety.ByteEqual(source, prepared.BackupPath, CancellationToken.None),
            "G4-04: JPEG replay restores the same original NTFS ID and bytes");

        string secondStage = Directory.CreateDirectory(Path.Combine(root, "g4-cutover-jpeg-backup")).FullName;
        string secondBackup = Directory.CreateDirectory(Path.Combine(backupRoot, "cutover-jpeg-backup")).FullName;
        var second = ReplacementReadiness.PrepareVerifiedCopies(source, candidate, secondStage, secondBackup,
            fidelity.OriginalSha256, fidelity.CandidateSha256);
        ReplacementReadiness.ApplyPreparedImage(source, candidate, secondStage, secondBackup);
        string token = Path.GetFileNameWithoutExtension(second.StagedPath)[".chappy-g4-stage-".Length..];
        string rollback = source + ".chappy-g4-old-" + token + ".rollback";
        string quarantine = source + ".chappy-g4-optimized-" + token + ".quarantine";
        long backupAllocation = Allocation(second.BackupPath);
        long appliedAllocation = Allocation(source) + Allocation(rollback);
        long freeWithRollback = drive.AvailableFreeSpace;
        check(appliedAllocation == originalAllocation + stageAllocation && backupAllocation > 0,
            "G4-06: retained JPEG original still consumes its full NTFS allocation");
        File.Delete(rollback); // Simulate loss of the retired original on the disposable fixture.
        long remainingAllocation = Allocation(source);
        long freeWithoutRollback = drive.AvailableFreeSpace;
        AssertImageBlocksMerge(source, candidate, fidelity.CandidateSha256, check);
        check(remainingAllocation == stageAllocation && appliedAllocation - remainingAllocation == originalAllocation,
            "G4-06: exact NTFS allocation after fixture rollback removal");

        ExpectProcessExit(source, candidate, secondStage, secondBackup,
            "g4-after-candidate-quarantine", true, check);
        check(ReplacementReadiness.RestorePreparedImage(source, candidate, secondStage, secondBackup) ==
            "BackupRestored" && !NativeFile.GetIdentity(source).SameFile(original.Identity) &&
            Safety.ByteEqual(source, second.BackupPath, CancellationToken.None),
            "G4-04: independent JPEG backup replay restores original bytes under a new NTFS ID");
        long restoredAllocation = Allocation(source) + Allocation(quarantine);
        check(restoredAllocation == appliedAllocation,
            "G4-06: restored JPEG and its retained candidate consume both allocations");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText(Path.Combine("diagnostics", "g4-jpeg-ntfs-allocation.json"),
            JsonSerializer.Serialize(new
            {
                originalLogicalBytes = original.Length,
                candidateLogicalBytes = new FileInfo(candidate).Length,
                originalAllocation, stageAllocation, backupAllocation, appliedAllocation,
                remainingAllocation, restoredAllocation,
                freeBeforePreparation, freeWithRollback, freeWithoutRollback,
                freeAfterRestore = drive.AvailableFreeSpace,
                sharedVolumeFreeSpaceObservations = true,
                realUserCapacityRecoveredBytes = 0
            }, new JsonSerializerOptions { WriteIndented = true }));

        // Reuse these exact generated bytes in the isolated VHDX probe.
        string runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidOperationException("CI runner TEMP is required.");
        string fixture = Directory.CreateDirectory(Path.Combine(runnerTemp,
            "chappy-g4-generated-jpeg-pair")).FullName;
        File.Copy(source, Path.Combine(fixture, "original.jpg"));
        File.Copy(candidate, Path.Combine(fixture, "candidate.jpg"));

        AssertDedupeConflict(root, backupRoot, source, candidate, fidelity, check);
    }

    private static void AssertImageBlocksMerge(string source, string candidate, string hash,
        Action<bool, string> check)
    {
        string input = Path.GetDirectoryName(source)!;
        File.WriteAllText(Path.Combine(input, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string twin = Path.Combine(input, "applied-twin.jpg");
        var snapshot = Safety.Capture(source);
        File.Copy(candidate, twin);
        File.SetCreationTimeUtc(twin, snapshot.CreationUtc);
        File.SetLastWriteTimeUtc(twin, snapshot.ModifiedUtc);
        var group = new DuplicateGroup { Hash = hash, Files = [Safety.Capture(twin), snapshot] };
        bool blocked = false;
        try { CompletedGroupLedger.MergeSynthetic(input, group); }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("画像の適用・復元記録", StringComparison.Ordinal))
        {
            blocked = true;
        }
        check(blocked && !Directory.Exists(Safety.StorePath(input)) &&
            Safety.SameSnapshot(snapshot) && Safety.ByteEqual(source, twin, CancellationToken.None) &&
            !NativeFile.SameFile(source, twin),
            "G4-05: applied JPEG denies real merge before creating history, even without rollback");
        bool directBlocked = false;
        try { DedupeEngine.Run(input, [group], false, null, CancellationToken.None); }
        catch (InvalidDataException ex) when (ex.Message.StartsWith("画像の適用・復元記録", StringComparison.Ordinal))
        {
            directBlocked = true;
        }
        check(directBlocked && !Directory.Exists(Safety.StorePath(input)) &&
            Safety.SameSnapshot(snapshot) && !NativeFile.SameFile(source, twin),
            "G4-05: direct merge denies the active image without group preparation");
        File.Delete(twin); // Generated fixture only.
    }

    private static void AssertDedupeConflict(string root, string backupRoot, string source,
        string candidate, ReplacementReadiness.FidelityDecision fidelity, Action<bool, string> check)
    {
        string input = Path.GetDirectoryName(source)!;
        string stage = Directory.CreateDirectory(Path.Combine(root, "g4-cutover-jpeg-conflict")).FullName;
        string backup = Directory.CreateDirectory(Path.Combine(backupRoot, "cutover-jpeg-conflict")).FullName;
        var prepared = ReplacementReadiness.PrepareVerifiedCopies(source, candidate, stage, backup,
            fidelity.OriginalSha256, fidelity.CandidateSha256);
        File.WriteAllText(Path.Combine(input, ".chappy-part23-generated-only"), "Part23 synthetic data only");
        string twin = Path.Combine(input, "representative.jpg");
        File.Copy(source, twin);
        File.SetCreationTimeUtc(twin, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(twin, File.GetLastWriteTimeUtc(source));
        var group = new DuplicateGroup
        {
            Hash = fidelity.OriginalSha256,
            Files = [Safety.Capture(twin), Safety.Capture(source)]
        };
        string operation = CompletedGroupLedger.MergeSynthetic(input, group);
        check(CompletedGroupLedger.Inspect(input).VerifiedCompleted == 1 &&
            NativeFile.SameFile(source, twin), "G4-05: JPEG fixture has a real completed shared group");
        AssertBlocked();
        check(NativeFile.SameFile(source, twin) && CompletedGroupLedger.Inspect(input).VerifiedCompleted == 1,
            "G4-05: denied image apply preserves the completed shared group");

        string undoEvidence = Directory.CreateDirectory(Path.Combine(input, "undo-evidence")).FullName;
        CompletedGroupLedger.ResumeUndoSynthetic(input, operation, undoEvidence);
        check(NativeFile.GetIdentity(source).LinkCount == 1 && !NativeFile.SameFile(source, twin) &&
            CompletedGroupLedger.Inspect(input).VerifiedRestored == 1,
            "G4-05: real independent Undo restores the JPEG group");
        AssertBlocked();
        check(CompletedGroupLedger.Inspect(input).VerifiedRestored == 1 &&
            Safety.ByteEqual(source, prepared.BackupPath, CancellationToken.None) &&
            File.Exists(prepared.StagedPath),
            "G4-05: related Undo history, original bytes, and stage survive a denied image apply");

        void AssertBlocked()
        {
            try
            {
                ReplacementReadiness.ApplyPreparedImage(source, candidate, stage, backup);
            }
            catch (InvalidDataException ex) when (ex.Message.StartsWith("統合・独立Undoの履歴", StringComparison.Ordinal))
            {
                return;
            }
            throw new InvalidOperationException("G4 must reject the real related dedupe history at its history guard.");
        }
    }
}
