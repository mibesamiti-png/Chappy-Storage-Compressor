using System.Text.Json;

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
#endif
        )
    {
#if !CHAPPY_TESTING
        throw new NotSupportedException(
            "独立Undoの本番操作は完了台帳・メタデータ・電源断ゲートを満たすまで無効です。");
#else
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
            !NativeFile.SameFile(alias, master) ||
            NativeFile.GetIdentity(master).LinkCount != expectedSharedLinkCount ||
            !string.Equals(Safety.ComputeSha256(master, ct), expectedHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Undo stage or source changed after durable intent; refused before cutover");

        // This is a single test-owned alias, NOT the master. The independent
        // source and store link remain untouched by the replace operation.
        File.Replace(stage, alias, null);
        FileSnapshot detached = Safety.Capture(alias);
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
                "Undo cutover occurred but full byte/identity/metadata proof failed: retain evidence");
        return new Result(alias, record, shared.Identity, detached.Identity, detached.Length);
#endif
    }
}
