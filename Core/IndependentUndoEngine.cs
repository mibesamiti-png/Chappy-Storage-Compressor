using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// First integrated Core-level independent Undo prototype. The public method is
/// ALWAYS disabled in normal app builds. Its CHAPPY_TESTING implementation can
/// operate only on a uniquely generated synthetic NTFS fixture with sentinel.
/// It is NOT a crash-safe production Undo: completed-merge ledger and universal
/// metadata restoration remain release blockers.
/// </summary>
public static class IndependentUndoEngine
{
    public const bool ProductionEnabled = false;
    // Compiled for the same internal Core in both builds. The only normal
    // entrypoint that could run it remains INTERNAL and generated-only.
    // The PUBLIC Restore entrypoint always throws in release builds.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetFileSecurityW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string path,
        uint securityInformation, byte[] securityDescriptor);
    public sealed record Result(string AliasPath, string DurableEvidencePath,
        FileIdentity SharedIdentityBefore, FileIdentity IndependentIdentityAfter,
        long ApproximateRestoredBytes);

    private sealed record Evidence(int Schema, string Alias, string Master,
        string Stage, string Sha256, uint OriginalVolume, ulong OriginalIndex,
        uint BeforeLinks, long OriginalLength, long CreationTicks,
        long ModifiedTicks, string StageSha256);

    public static Result Restore(string testRoot, FileSnapshot originalIndependent,
        string masterPath, string expectedHash, uint expectedSharedLinkCount,
        string evidenceDirectory, CancellationToken ct = default
#if CHAPPY_TESTING
        , Action<string>? testOnlyBeforeFinalValidation = null
        , string? testOnlyCompletedOperationId = null
        , Action<string>? testOnlyCrashCheckpoint = null
        , Action<string, string>? testOnlyGroupPhase = null
        , Action<string>? testOnlyAfterFinalValidation = null
#endif
        )
    {
#if !CHAPPY_TESTING
        throw new NotSupportedException(
            "独立Undoの本番操作は完了台帳・メタデータ・電源断ゲートを満たすまで無効です。");
#else
        return RestoreCore(testRoot, originalIndependent, masterPath,
            expectedHash, expectedSharedLinkCount, evidenceDirectory, ct,
            testOnlyBeforeFinalValidation, testOnlyCompletedOperationId,
            testOnlyCrashCheckpoint, testOnlyGroupPhase,
            testOnlyAfterFinalValidation);
#endif
    }

    // G3 shared implementation: the identical generated-only NTFS cutover
    // logic is compiled in NORMAL and test builds, but deliberately INTERNAL.
    // Never route WPF or the public Restore API to this method. A strict
    // generated-root marker and in-place ID/byte validation are enforced
    // before any file is touched. G3/G5 must approve a separate product
    // authorization boundary before ordinary media can be modified.
    internal static Result RestoreCore(string testRoot,
        FileSnapshot originalIndependent, string masterPath,
        string expectedHash, uint expectedSharedLinkCount,
        string evidenceDirectory, CancellationToken ct = default,
        Action<string>? testOnlyBeforeFinalValidation = null,
        string? testOnlyCompletedOperationId = null,
        Action<string>? testOnlyCrashCheckpoint = null,
        Action<string, string>? testOnlyGroupPhase = null,
        Action<string>? testOnlyAfterFinalValidation = null)
    {
        ct.ThrowIfCancellationRequested();
        string root = Safety.ValidateRoot(testRoot);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string sentinel = Path.Combine(root, ".chappy-part23-generated-only");
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sentinel) ||
            File.ReadAllText(sentinel) != "Part23 synthetic data only")
            throw new InvalidOperationException("Independent Undo refused non-synthetic source root");
        string alias = Path.GetFullPath(originalIndependent.Path);
        string master = Path.GetFullPath(masterPath);
        string evidence = Path.GetFullPath(evidenceDirectory);
        if (!alias.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !master.StartsWith(Safety.StorePath(root) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !evidence.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(alias) is not string aliasFolder ||
            !Directory.Exists(evidence) ||
            expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit) ||
            expectedSharedLinkCount < 3)
            throw new InvalidOperationException("Undo source/master/evidence paths or expected hash invalid");

        using var exclusive = MutationLease.Acquire();
        Journal.AssertCleanBeforeMerge();
        if (!Safety.SameSnapshot(originalIndependent) &&
            !NativeFile.SameFile(alias, master))
            throw new InvalidDataException("Original source changed or expected link missing");
        FileSnapshot shared = Safety.Capture(alias);
        FileSnapshot masterSnapshot = Safety.Capture(master);
        if (!shared.Identity.SameFile(masterSnapshot.Identity) ||
            shared.Identity.LinkCount != expectedSharedLinkCount ||
            masterSnapshot.Identity.LinkCount != expectedSharedLinkCount ||
            !string.Equals(Safety.ComputeSha256(alias, ct), expectedHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(master, ct), expectedHash,
                StringComparison.OrdinalIgnoreCase) ||
            !Safety.ByteEqual(alias, master, ct) ||
            originalIndependent.Length != shared.Length ||
            !originalIndependent.SecurityDescriptor.AsSpan().SequenceEqual(shared.SecurityDescriptor) ||
            originalIndependent.Attributes != shared.Attributes ||
            originalIndependent.CreationUtc != shared.CreationUtc ||
            originalIndependent.ModifiedUtc != shared.ModifiedUtc)
            throw new InvalidDataException("Shared hardlink, original metadata or bytes no longer match Undo contract");

        string id = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(aliasFolder, ".chappy-part23-undo-" + id + ".stage");
        string record = Path.Combine(evidence, id + ".part23-undo.json");
        if (File.Exists(stage) || File.Exists(record))
            throw new IOException("Undo stage/evidence already exists");
#if CHAPPY_TESTING
        if (testOnlyCompletedOperationId is not null)
        {
            IndependentUndoCrashJournal.BeginSynthetic(root,
                testOnlyCompletedOperationId, originalIndependent, master,
                stage, expectedHash, expectedSharedLinkCount);
            testOnlyCrashCheckpoint?.Invoke("intent-written");
        }
#endif

        // Persist group-wide intent BEFORE any stage byte is written.
        // This testing hook is deliberately absent from normal builds.
        testOnlyGroupPhase?.Invoke("intent-written", stage);

        // A full independent copy consumes capacity; the duplicate link is
        // retained until the stage is validated. Use test-owned paths only.
        File.Copy(alias, stage, overwrite: false);
        File.SetAttributes(stage, originalIndependent.Attributes);
        File.SetCreationTimeUtc(stage, originalIndependent.CreationUtc);
        File.SetLastWriteTimeUtc(stage, originalIndependent.ModifiedUtc);
        FileSnapshot staged = Safety.Capture(stage);
        bool StageValid(FileSnapshot x) =>
            x.Identity.LinkCount == 1 &&
            x.Identity.VolumeSerial == shared.Identity.VolumeSerial &&
            !x.Identity.SameFile(shared.Identity) &&
            x.Length == originalIndependent.Length &&
            x.CreationUtc == originalIndependent.CreationUtc &&
            x.ModifiedUtc == originalIndependent.ModifiedUtc &&
            x.Attributes == originalIndependent.Attributes &&
            x.SecurityDescriptor.AsSpan().SequenceEqual(originalIndependent.SecurityDescriptor) &&
            string.Equals(Safety.ComputeSha256(stage, CancellationToken.None),
                expectedHash, StringComparison.OrdinalIgnoreCase);
        if (!StageValid(staged))
            throw new InvalidDataException("Independent stage failed byte or owner/DACL/metadata validation");
        testOnlyGroupPhase?.Invoke("stage-validated", stage);
#if CHAPPY_TESTING
        if (testOnlyCompletedOperationId is not null)
        {
            IndependentUndoCrashJournal.MarkStageSynthetic(root, testOnlyCompletedOperationId);
            testOnlyCrashCheckpoint?.Invoke("stage-validated");
        }
#endif

        var intent = new Evidence(1, alias, master, stage, expectedHash,
            shared.Identity.VolumeSerial, shared.Identity.FileIndex,
            expectedSharedLinkCount, shared.Length,
            originalIndependent.CreationUtc.Ticks,
            originalIndependent.ModifiedUtc.Ticks,
            Safety.ComputeSha256(stage, CancellationToken.None));
        // Durable proof BEFORE stage cutover. These are test-only records and
        // have no automatic post-crash replay in the shipped application.
        using (var stream = new FileStream(record, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, intent);
            stream.Flush(flushToDisk: true);
        }

        testOnlyBeforeFinalValidation?.Invoke(stage);
        ct.ThrowIfCancellationRequested();
        FileSnapshot lateStage = Safety.Capture(stage);
        if (!lateStage.Identity.SameFile(staged.Identity) ||
            !StageValid(lateStage) ||
            // Part31: a writer or ACL editor can change the SHARED master
            // metadata after initial validation without altering its SHA-256.
            // Recheck identity, times, normal attributes and owner+DACL for
            // BOTH names immediately before the irreversible File.Replace.
            !Safety.SameSnapshot(shared) ||
            !Safety.SameSnapshot(masterSnapshot) ||
            !NativeFile.SameFile(alias, master) ||
            NativeFile.GetIdentity(master).LinkCount != expectedSharedLinkCount ||
            !string.Equals(Safety.ComputeSha256(alias, ct), expectedHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(master, ct), expectedHash,
                StringComparison.OrdinalIgnoreCase) ||
            !Safety.ByteEqual(alias, master, ct))
            throw new InvalidDataException(
                "Undo stage, shared source bytes/identity or ACL/timestamps changed after durable intent; refused before cutover");

        // Part32: deterministic TEST-ONLY adversary can rename/delete a
        // pathname AFTER the preceding full verification. Re-open all three
        // original identities immediately before replacing any alias.
        // This narrows the detection window but does not provide an atomic
        // compare-and-replace against arbitrary concurrent deleters.
        if (testOnlyAfterFinalValidation is not null)
        {
            testOnlyAfterFinalValidation(stage);
            if (!Safety.SameSnapshot(lateStage) ||
                !Safety.SameSnapshot(shared) ||
                !Safety.SameSnapshot(masterSnapshot) ||
                !NativeFile.SameFile(alias, master) ||
                NativeFile.GetIdentity(master).LinkCount != expectedSharedLinkCount ||
                !string.Equals(Safety.ComputeSha256(stage, CancellationToken.None),
                    expectedHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Safety.ComputeSha256(alias, CancellationToken.None),
                    expectedHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Safety.ComputeSha256(master, CancellationToken.None),
                    expectedHash, StringComparison.OrdinalIgnoreCase) ||
                !Safety.ByteEqual(stage, alias, CancellationToken.None))
                throw new InvalidDataException(
                    "Pathname was renamed, deleted, replaced or modified after final Undo validation; no cutover performed");
        }

        // This is a single test-owned alias, NOT the master. The independent
        // source and store link remain untouched by the replace operation.
        File.Replace(stage, alias, null);
        // A real post-cutover crash may happen before metadata restoration.
        testOnlyGroupPhase?.Invoke("post-cutover-before-metadata", stage);
        // This crash checkpoint occurs AFTER the name has been switched to
        // an independent NTFS ID, BEFORE owner/DACL/time normalization.
#if CHAPPY_TESTING
        if (testOnlyCompletedOperationId is not null)
        {
            testOnlyCrashCheckpoint?.Invoke("post-cutover-before-metadata");
            IndependentUndoCrashJournal.MarkCutoverSynthetic(root, testOnlyCompletedOperationId);
        }
#endif
        FileSnapshot detached = Safety.Capture(alias);
        // ReplaceFileW can preserve metadata of the *replaced* NTFS alias
        // rather than all metadata of the staged independent file. Reapply
        // only normal attributes and source times after first confirming
        // independent identity, all bytes and owner+DACL. This is a
        // TEST-ONLY positive-path proof, NOT atomic crash-safe restoration.
        bool distinct = detached.Identity.LinkCount == 1 &&
            !detached.Identity.SameFile(NativeFile.GetIdentity(master));
        bool pixelsUnchanged = detached.Length == originalIndependent.Length &&
            string.Equals(Safety.ComputeSha256(alias, CancellationToken.None),
                expectedHash, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Safety.ComputeSha256(master, CancellationToken.None),
                expectedHash, StringComparison.OrdinalIgnoreCase);
        bool securityMatches = detached.SecurityDescriptor.AsSpan()
            .SequenceEqual(originalIndependent.SecurityDescriptor);
        if (!distinct || !pixelsUnchanged)
            throw new InvalidDataException(
                "Undo cutover refused: independent identity=" + distinct +
                ", byte integrity=" + pixelsUnchanged +
                "; durable evidence kept for manual review");
        if (!securityMatches)
        {
            // ReplaceFileW can merge the destination hardlink's security
            // descriptor into the independent stage even though the stage
            // itself was proven to contain the original owner/group/DACL.
            // Apply the exact captured ordinary security descriptor to ONLY
            // the now-proven independent alias in this disposable NTFS test.
            // Production Undo cannot use this until privileges/inheritance
            // and crash-replay gates are independently satisfied.
            const uint ownerGroupDacl = 0x00000001 | 0x00000002 | 0x00000004;
            if (!SetFileSecurity(alias, ownerGroupDacl,
                    originalIndependent.SecurityDescriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Test-only owner/group/DACL restoration failed; durable evidence retained");
            detached = Safety.Capture(alias);
            securityMatches = detached.SecurityDescriptor.AsSpan()
                .SequenceEqual(originalIndependent.SecurityDescriptor);
            if (!securityMatches)
                throw new InvalidDataException(
                    "Test-only owner/group/DACL re-read mismatched original descriptor after SetFileSecurity");
        }
        if (detached.Attributes != originalIndependent.Attributes ||
            detached.CreationUtc != originalIndependent.CreationUtc ||
            detached.ModifiedUtc != originalIndependent.ModifiedUtc)
        {
            // No change to the still-shared master link: the alias now has
            // its own proven identity and exact expected bytes.
            File.SetAttributes(alias, originalIndependent.Attributes);
            File.SetCreationTimeUtc(alias, originalIndependent.CreationUtc);
            File.SetLastWriteTimeUtc(alias, originalIndependent.ModifiedUtc);
            detached = Safety.Capture(alias);
        }
        if (detached.Identity.LinkCount != 1 ||
            detached.Identity.SameFile(NativeFile.GetIdentity(master)) ||
            detached.Length != originalIndependent.Length ||
            detached.Attributes != originalIndependent.Attributes ||
            detached.CreationUtc != originalIndependent.CreationUtc ||
            detached.ModifiedUtc != originalIndependent.ModifiedUtc ||
            !detached.SecurityDescriptor.AsSpan().SequenceEqual(originalIndependent.SecurityDescriptor) ||
            !string.Equals(Safety.ComputeSha256(alias, CancellationToken.None),
                expectedHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(master, CancellationToken.None),
                expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Undo cutover occurred but full byte/identity/metadata proof failed after test-only normalization: " +
                "ctime=" + (detached.CreationUtc == originalIndependent.CreationUtc) +
                ", mtime=" + (detached.ModifiedUtc == originalIndependent.ModifiedUtc) +
                ", attributes=" + (detached.Attributes == originalIndependent.Attributes) +
                ", owner+DACL=" +
                detached.SecurityDescriptor.AsSpan().SequenceEqual(originalIndependent.SecurityDescriptor) +
                "; retain evidence");
        testOnlyGroupPhase?.Invoke("metadata-restored-before-ledger", stage);
#if CHAPPY_TESTING
        if (testOnlyCompletedOperationId is not null)
        {
            IndependentUndoCrashJournal.MarkMetadataSynthetic(root, testOnlyCompletedOperationId);
            testOnlyCrashCheckpoint?.Invoke("metadata-restored-before-ledger");
        }
#endif
        return new Result(alias, record, shared.Identity, detached.Identity, detached.Length);
    }

#if CHAPPY_TESTING
    // Deliberate, generated-only recovery from a genuine post-File.Replace
    // crash. The original security and times are from completed history,
    // never from the potentially half-restored current alias.
    internal static void RestoreMetadataAfterCrashSynthetic(string testRoot,
        FileSnapshot original, string master, string hash, uint expectedLinks)
    {
        string root = Safety.ValidateRoot(testRoot);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(root, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Non-generated Undo recovery denied");
        using var exclusive = MutationLease.Acquire();
        Journal.AssertCleanBeforeMerge();
        var masterId = NativeFile.GetIdentity(master);
        var detachedId = NativeFile.GetIdentity(original.Path);
        if (expectedLinks < 3 ||
            masterId.LinkCount != expectedLinks - 1 ||
            detachedId.LinkCount != 1 ||
            detachedId.SameFile(masterId) ||
            detachedId.VolumeSerial != masterId.VolumeSerial ||
            !string.Equals(Safety.ComputeSha256(original.Path, CancellationToken.None),
                hash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(master, CancellationToken.None),
                hash, StringComparison.OrdinalIgnoreCase) ||
            !Safety.ByteEqual(original.Path, master, CancellationToken.None))
            throw new InvalidDataException("Undo restart refused: NTFS identity/link count or bytes changed");

        FileSnapshot current = Safety.Capture(original.Path);
        if (!current.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor))
        {
            const uint ownerGroupDacl = 0x00000001 | 0x00000002 | 0x00000004;
            if (!SetFileSecurity(original.Path, ownerGroupDacl, original.SecurityDescriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Restart Undo security restoration failed; evidence retained");
        }
        current = Safety.Capture(original.Path);
        if (current.Attributes != original.Attributes ||
            current.CreationUtc != original.CreationUtc ||
            current.ModifiedUtc != original.ModifiedUtc)
        {
            File.SetAttributes(original.Path, original.Attributes);
            File.SetCreationTimeUtc(original.Path, original.CreationUtc);
            File.SetLastWriteTimeUtc(original.Path, original.ModifiedUtc);
        }
        FileSnapshot verified = Safety.Capture(original.Path);
        if (!verified.Identity.SameFile(detachedId) ||
            verified.Identity.LinkCount != 1 ||
            verified.Length != original.Length ||
            verified.Attributes != original.Attributes ||
            verified.CreationUtc != original.CreationUtc ||
            verified.ModifiedUtc != original.ModifiedUtc ||
            !verified.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor) ||
            !string.Equals(Safety.ComputeSha256(original.Path, CancellationToken.None),
                hash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(master, CancellationToken.None),
                hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Interrupted Undo final metadata/identity validation failed");
    }
#endif
}
