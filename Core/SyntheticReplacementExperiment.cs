#if CHAPPY_TESTING
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.6 synthetic-only cutover/cleanup experiment. Not compiled in the application.
/// NEVER use this to transform personal media. It does not prove image fidelity,
/// metadata preservation, independent physical backup or power-loss durability.
/// </summary>
internal static class SyntheticReplacementExperiment
{
    private const string SentinelName = ".chappy-r23-synthetic-only";
    private const string JournalName = ".r23-rollback.json"; // Retained for synthetic R2.3 test compatibility
    private const string StageName = ".r23-stage.bin";
    private const string RollbackName = ".r23-original-rollback.bin";
    private const string QuarantineName = ".r23-quarantined-candidate.bin";
    private const string ExternalName = ".r23-external-copy.bin";

    internal sealed record Record(string OriginalSha, string CandidateSha,
        long OriginalBytes, long CandidateBytes);

    // A pre-journal crash can leave a verified backup or staging file behind.
    // Detection/cleanup is synthetic-only, never reachable from the release GUI.
    internal sealed record PreparationInspection(bool HasArtifacts, bool CanDiscard,
        string Reason, long StagedBytes);

    internal static PreparationInspection InspectPreparation(string directory)
    {
        string dir = ValidateIsolatedDirectory(directory);
        string journal = At(dir, JournalName);
        string external = At(dir, ExternalName);
        string stage = At(dir, StageName);
        string tempJournal = journal + ".tmp";
        bool hasExternal = AnyEntry(external);
        bool hasStage = AnyEntry(stage);
        bool hasTemp = AnyEntry(tempJournal);
        bool hasAny = hasExternal || hasStage || hasTemp;
        if (AnyEntry(journal))
            return new(true, false, "Transaction journal exists: use rollback, not cleanup", 0);
        if (AnyEntry(At(dir, RollbackName)) || AnyEntry(At(dir, QuarantineName)))
            return new(true, false, "Rollback/quarantine exists without journal: manual review", 0);
        if (!hasAny) return new(false, false, "No preparatory artifacts", 0);
        if (!hasExternal || (hasTemp && !hasStage))
            return new(true, false, "Impossible preparation order: manual review", 0);
        try
        {
            string original = At(dir, "original.bin");
            string candidate = At(dir, "candidate.bin");
            FileSnapshot source = Safety.Capture(original);
            FileSnapshot preview = Safety.Capture(candidate);
            if (source.Identity.LinkCount != 1 || preview.Identity.LinkCount != 1)
                return new(true, false, "Shared hardlink input: manual review", 0);
            if (preview.Length >= source.Length)
                return new(true, false, "Candidate is not smaller: manual review", 0);
            foreach (string artifact in new[] { external, stage, tempJournal })
            {
                if (AnyEntry(artifact) &&
                    (File.GetAttributes(artifact) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                    return new(true, false, "Unsafe artifact attributes: manual review", 0);
            }
            string oldHash = Hash(original), newHash = Hash(candidate);
            VerifyFile(external, oldHash, source.Length);
            if (hasStage) VerifyFile(stage, newHash, preview.Length);
            if (hasTemp)
            {
                RequireIndependentPlainFile(tempJournal);
                var info = new FileInfo(tempJournal);
                if (info.Length is <= 0 or > 8192)
                    return new(true, false, "Invalid pending journal size", 0);
                Record? pending = JsonSerializer.Deserialize<Record>(File.ReadAllText(tempJournal));
                if (pending is null || pending.OriginalSha != oldHash ||
                    pending.CandidateSha != newHash || pending.OriginalBytes != source.Length ||
                    pending.CandidateBytes != preview.Length)
                    return new(true, false, "Untrusted temporary journal", 0);
            }
            if (!Safety.SameSnapshot(source) || !Safety.SameSnapshot(preview))
                return new(true, false, "Inputs changed during inspection", 0);
            return new(true, true, "Verified pre-journal artifacts only", source.Length + (hasStage ? preview.Length : 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or
            System.ComponentModel.Win32Exception)
        {
            return new(true, false, "Unable to prove safe cleanup: " + ex.GetType().Name, 0);
        }
    }

    internal static void DiscardVerifiedPreparation(string directory)
    {
        string dir = ValidateIsolatedDirectory(directory);
        PreparationInspection inspection = InspectPreparation(dir);
        if (!inspection.HasArtifacts || !inspection.CanDiscard)
            throw new InvalidOperationException("No verified synthetic pre-journal artifacts: " + inspection.Reason);
        // Recheck immediately before mutation; this is NOT race-free production code.
        if (!InspectPreparation(dir).CanDiscard)
            throw new InvalidOperationException("Synthetic preparation changed before cleanup");
        File.Delete(At(dir, JournalName) + ".tmp");
        File.Delete(At(dir, StageName));
        File.Delete(At(dir, ExternalName));
    }

    // File.Exists intentionally returns false for directories. Treat a reserved
    // transaction name occupied by a directory as a collision, not an empty slot.
    private static bool AnyEntry(string path) => File.Exists(path) || Directory.Exists(path);

    private static string Hash(string path) => Safety.ComputeSha256(path, CancellationToken.None);
    private static string At(string directory, string name) => Path.Combine(directory, name);

    private static string ValidateIsolatedDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows synthetic tests only");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only uniquely generated TEMP tests are allowed");
        string relative = Path.GetRelativePath(temp, full);
        string first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (!first.StartsWith("ChappyStorageSmoke_", StringComparison.Ordinal) ||
            !File.Exists(At(full, SentinelName)) ||
            File.ReadAllText(At(full, SentinelName)) != "CHAPPY-R23-SYNTHETIC-ONLY\n")
            throw new InvalidOperationException("Missing synthetic-only sentinel");
        string cursor = full;
        while (!cursor.Equals(temp, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse-point path refused");
            cursor = Path.GetDirectoryName(cursor) ?? throw new InvalidOperationException("Invalid test path");
        }
        return full;
    }

    internal static void PrepareTestDirectory(string directory)
    {
        // Called only by the isolated test harness, never by the application.
        string full = Path.GetFullPath(directory);
        Directory.CreateDirectory(full);
        string sentinel = At(full, SentinelName);
        using var stream = new FileStream(sentinel, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] text = System.Text.Encoding.ASCII.GetBytes("CHAPPY-R23-SYNTHETIC-ONLY\n");
        stream.Write(text);
        stream.Flush(true);
        stream.Dispose();
        ValidateIsolatedDirectory(full);
    }

    private static void FlushCopy(string source, string destination)
    {
        File.Copy(source, destination, overwrite: false);
        using var stream = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    // A matching SHA-256 does NOT make a rollback artifact safe to move/delete.
    // A second hardlink, reparse point or ADS could carry data or ownership outside
    // the expected single ordinary NTFS stream. Test-only, and still subject to
    // path-replacement races until production uses identity-bound open handles.
    private static void RequireIndependentPlainFile(string path)
    {
        if (Directory.Exists(path) || !File.Exists(path))
            throw new InvalidDataException("Missing or non-file synthetic artifact: " + path);
        FileAttributes attributes = File.GetAttributes(path);
        if (!Safety.EligibleAttributes(attributes))
            throw new InvalidDataException("Unsafe synthetic file attributes: " + path);
        if (NativeFile.GetIdentity(path).LinkCount != 1)
            throw new InvalidDataException("Shared hardlink artifact requires manual review: " + path);
        if (NativeFile.HasNamedDataStreams(path))
            throw new InvalidDataException("Named stream artifact requires manual review: " + path);
    }

    private static void VerifyFile(string path, string expectedHash, long expectedBytes)
    {
        // R2.6: verifying only a hash is insufficient if a path can be swapped
        // for a distinct (possibly hardlinked) file during the read. Capture the
        // NTFS identity and metadata BEFORE the hash, and recheck them afterward.
        // This narrows the race, but the production engine still requires open
        // handles / transactional identity-bound operations for true race safety.
        RequireIndependentPlainFile(path);
        FileSnapshot before = Safety.Capture(path);
        if (before.Identity.LinkCount != 1 || before.Length != expectedBytes ||
            Hash(path) != expectedHash)
            throw new InvalidDataException("Synthetic file was modified or is missing: " + path);
        FaultInjection.Hit("r26-after-artifact-hash");
        RequireIndependentPlainFile(path);
        if (!Safety.SameSnapshot(before))
            throw new InvalidDataException("Synthetic file identity changed during verification: " + path);
    }

    private static string? ExistingHash(string path)
    {
        if (!AnyEntry(path)) return null;
        RequireIndependentPlainFile(path);
        return Hash(path);
    }

    private static Record ReadRecord(string directory)
    {
        string file = At(directory, JournalName);
        RequireIndependentPlainFile(file);
        long journalBytes = new FileInfo(file).Length;
        if (journalBytes is <= 0 or > 8192)
            throw new InvalidDataException("Synthetic rollback journal size out of range");
        Record record = JsonSerializer.Deserialize<Record>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Empty synthetic journal");
        if (record.OriginalSha is null || record.CandidateSha is null ||
            record.OriginalSha.Length != 64 || !record.OriginalSha.All(Uri.IsHexDigit) ||
            record.CandidateSha.Length != 64 || !record.CandidateSha.All(Uri.IsHexDigit) ||
            record.OriginalSha.Equals(record.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            record.CandidateBytes <= 0 || record.CandidateBytes >= record.OriginalBytes)
            throw new InvalidDataException("Invalid synthetic journal values");
        VerifyFile(At(directory, ExternalName), record.OriginalSha, record.OriginalBytes);
        return record;
    }

    internal static bool Pending(string directory) =>
        File.Exists(At(ValidateIsolatedDirectory(directory), JournalName));

    internal static void Begin(string directory)
    {
        string dir = ValidateIsolatedDirectory(directory);
        string original = At(dir, "original.bin");
        string candidate = At(dir, "candidate.bin");
        string journal = At(dir, JournalName);
        string stage = At(dir, StageName);
        string external = At(dir, ExternalName);
        string rollback = At(dir, RollbackName);
        string quarantine = At(dir, QuarantineName);
        if (AnyEntry(journal) || AnyEntry(journal + ".tmp") || AnyEntry(stage) || AnyEntry(external) ||
            AnyEntry(rollback) || AnyEntry(quarantine))
            throw new InvalidOperationException("Existing experiment artifacts; recover first");
        if (!File.Exists(original) || !File.Exists(candidate))
            throw new FileNotFoundException("Synthetic input pair missing");
        if ((File.GetAttributes(original) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Synthetic links refused");
        // Reject named streams, shared hardlinks and unsupported NTFS attributes.
        // This is not a substitute for a future production-grade exclusive lock.
        FileSnapshot originalSnapshot = Safety.Capture(original);
        FileSnapshot candidateSnapshot = Safety.Capture(candidate);
        if (originalSnapshot.Identity.LinkCount != 1 || candidateSnapshot.Identity.LinkCount != 1)
            throw new InvalidOperationException("Synthetic input already has hardlinks");
        // Probe exclusive access; close BEFORE File.Replace (which needs rename access).
        // There remains a TOCTOU window: this is a synthetic experiment only.
        using (var probe = new FileStream(original, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        { }
        using (var probe = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.None))
        { }
        long oldBytes = originalSnapshot.Length;
        long newBytes = candidateSnapshot.Length;
        if (oldBytes <= 0 || newBytes <= 0 || newBytes >= oldBytes)
            throw new InvalidOperationException("Synthetic candidate must be smaller");
        string oldHash = Hash(original), newHash = Hash(candidate);
        if (oldHash == newHash) throw new InvalidOperationException("Experiment needs different bytes");
        FlushCopy(original, external);
        VerifyFile(external, oldHash, oldBytes);
        FaultInjection.Hit("r23-after-external-copy");
        FlushCopy(candidate, stage);
        VerifyFile(stage, newHash, newBytes);
        FaultInjection.Hit("r23-after-stage-copy");
        var record = new Record(oldHash, newHash, oldBytes, newBytes);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(record);
        string journalTemp = journal + ".tmp";
        using (var stream = new FileStream(journalTemp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(json);
            stream.Flush(true);
        }
        FaultInjection.Hit("r24-after-journal-temp");
        File.Move(journalTemp, journal);
        FaultInjection.Hit("r23-after-journal");
        // R2.4 test-only mutation window: recheck immediately after this checkpoint.
        FaultInjection.Hit("r24-before-final-preflight");
        // R2.3 is test-only: this is the only media cutover point.
        // The local rollback is the original NTFS file, not a second copy.
        VerifyFile(original, oldHash, oldBytes);
        VerifyFile(candidate, newHash, newBytes);
        // R2.6: a staged candidate or rollback copy may be replaced/modified
        // AFTER its initial verification. Never perform File.Replace unless both
        // transaction artifacts still match the exact expected bytes/identity.
        VerifyFile(stage, newHash, newBytes);
        VerifyFile(external, oldHash, oldBytes);
        if (!Safety.SameSnapshot(originalSnapshot) || !Safety.SameSnapshot(candidateSnapshot))
            throw new InvalidOperationException("Synthetic input changed during staging");
        File.Replace(stage, original, rollback);
        FaultInjection.Hit("r23-after-replace");
        VerifyFile(original, newHash, newBytes);
        VerifyFile(rollback, oldHash, oldBytes);
        // Intentionally NO commit / delete-original operation. Tests call RollBack.
    }

    internal static void RollBack(string directory)
    {
        string dir = ValidateIsolatedDirectory(directory);
        Record record = ReadRecord(dir); // Fail closed if verified backup was changed.
        string original = At(dir, "original.bin");
        string stage = At(dir, StageName);
        string rollback = At(dir, RollbackName);
        string quarantine = At(dir, QuarantineName);
        string sourceHash = ExistingHash(original);
        string rollbackHash = ExistingHash(rollback);
        string quarantineHash = ExistingHash(quarantine);
        if (sourceHash is not null && sourceHash != record.OriginalSha &&
            sourceHash != record.CandidateSha)
            throw new InvalidDataException("Original path modified; manual review required");
        if (rollbackHash is not null && rollbackHash != record.OriginalSha)
            throw new InvalidDataException("Rollback file modified; manual review required");
        if (quarantineHash is not null && quarantineHash != record.CandidateSha)
            throw new InvalidDataException("Quarantine file modified; manual review required");
        if (AnyEntry(stage)) VerifyFile(stage, record.CandidateSha, record.CandidateBytes);

        if (sourceHash == record.CandidateSha && rollbackHash == record.OriginalSha &&
            quarantineHash is null)
        {
            File.Move(original, quarantine);
            FaultInjection.Hit("r23-after-quarantine");
            sourceHash = null;
            quarantineHash = record.CandidateSha;
        }
        if (sourceHash is null && rollbackHash == record.OriginalSha &&
            quarantineHash == record.CandidateSha)
        {
            File.Move(rollback, original);
            FaultInjection.Hit("r23-after-restore");
            sourceHash = record.OriginalSha;
            rollbackHash = null;
        }
        if (sourceHash != record.OriginalSha || rollbackHash is not null ||
            (quarantineHash is not null && quarantineHash != record.CandidateSha))
            throw new InvalidDataException("Ambiguous cutover state; no destructive cleanup");
        VerifyFile(original, record.OriginalSha, record.OriginalBytes);
        VerifyFile(At(dir, ExternalName), record.OriginalSha, record.OriginalBytes);
        // Cleanup ONLY synthetic experiment artifacts. Keep original and candidate intact.
        if (AnyEntry(quarantine))
        {
            VerifyFile(quarantine, record.CandidateSha, record.CandidateBytes);
            File.Delete(quarantine);
        }
        if (AnyEntry(stage))
        {
            VerifyFile(stage, record.CandidateSha, record.CandidateBytes);
            File.Delete(stage);
        }
        File.Delete(At(dir, JournalName));
        // Crash at this boundary leaves a verified orphaned copy but no pending
        // journal; the test-only cleanup path can detect it on the next run.
        FaultInjection.Hit("r24-after-journal-cleanup");
        VerifyFile(At(dir, ExternalName), record.OriginalSha, record.OriginalBytes);
        File.Delete(At(dir, ExternalName));
        FaultInjection.Hit("r23-after-cleanup");
    }
}
#endif
