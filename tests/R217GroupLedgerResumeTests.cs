using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part27: real Core four-name synthetic merge, group-level persisted history,
// actual killed independent-Undo worker AFTER its first durable alias checkpoint.
internal static class R217GroupLedgerResumeTests
{
    private const string Flag = "--r217-part27-generated-group-undo-kill";
    private const string Guard = "CHAPPY_PART27_GROUP_UNDO_GENERATED_ONLY";
    private static string Hash(string path) => Safety.ComputeSha256(path, CancellationToken.None);

    public static bool IsChild(string[] args) => args.Length > 0 && args[0] == Flag;

    private static void GuardRoot(string root)
    {
        string full = Safety.ValidateRoot(root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(full, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part27 worker denied non-generated root");
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 4 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            !Guid.TryParseExact(args[2], "N", out _))
            throw new InvalidOperationException("Part27 worker not authorized");
        string root = Path.GetFullPath(args[1]);
        GuardRoot(root);
        string signal = Path.GetFullPath(args[3]);
        if (!signal.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Part27 checkpoint outside generated root");
        Journal.TestFolderPath = Path.Combine(root, "part27-journal");
        string evidence = Path.Combine(root, "part27-undo-evidence");
        CompletedGroupLedger.ResumeUndoSynthetic(root, args[2], evidence,
            afterDurableAlias: count =>
            {
                if (count != 1) return;
                using (var stream = new FileStream(signal, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                        "first-alias-checkpoint-durable");
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                using var self = Process.GetCurrentProcess();
                self.Kill(); // only the explicitly authorized generated worker
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidOperationException("Part27 synthetic process kill checkpoint not reached");
    }

    private static int KillWorker(string root, string id, string signal)
    {
        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("Missing test runner executable");
        var info = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(R217GroupLedgerResumeTests).Assembly.Location);
        foreach (var arg in new[] { Flag, root, id, signal }) info.ArgumentList.Add(arg);
        info.Environment[Guard] = "generated-only";
        using var child = Process.Start(info) ??
            throw new IOException("Part27 generated process failed to start");
        try
        {
            if (!child.WaitForExit(60000))
                throw new TimeoutException("Part27 generated worker failed to exit");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
        return child.ExitCode;
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? oldJournal = Journal.TestFolderPath;
        try
        {
            string root = Path.Combine(parent, "r217-part27-four-name-group");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            GuardRoot(root);
            Journal.TestFolderPath = Path.Combine(root, "part27-journal");
            string original = Path.Combine(root, "media-a.mp4");
            File.WriteAllBytes(original, RandomNumberGenerator.GetBytes(384 * 1024));
            for (int i = 1; i <= 3; i++)
            {
                string destination = Path.Combine(root, $"media-{(char)('a' + i)}.mp4");
                File.Copy(original, destination);
                File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(original));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(original));
            }

            var scan = Scanner.Run(root, true, null, CancellationToken.None);
            check(scan.FileCount == 4 && scan.Groups.Count == 1 &&
                  scan.Groups[0].Files.Count == 4 &&
                  scan.Groups[0].BlockReason(false) is null,
                "R2.17 part27: real scanner discovers four independent, identical generated MP4 files");
            var before = scan.Groups.Single().Files.ToDictionary(s => s.Path,
                StringComparer.OrdinalIgnoreCase);
            string groupHash = scan.Groups.Single().Hash;
            var empty = CompletedGroupLedger.Inspect(root);
            check(empty.Entries.Count == 0 && !Directory.Exists(Safety.StorePath(root)),
                "R2.17 part27: read-only group inspection creates no files or store");

            string id = CompletedGroupLedger.MergeSynthetic(root, scan.Groups.Single());
            var completed = CompletedGroupLedger.Inspect(root);
            string master = Path.Combine(Safety.StorePath(root), groupHash + ".mp4");
            bool merged = completed.VerifiedCompleted == 1 &&
                completed.Entries.Single().AliasCount == 3 &&
                completed.Entries.Single().DetachedCount == 0 &&
                NativeFile.GetIdentity(master).LinkCount == 5 &&
                scan.Groups.Single().Files.All(s =>
                    NativeFile.SameFile(s.Path, master) &&
                    Hash(s.Path) == groupHash) &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0;
            check(merged,
                "R2.17 part27: real Core consolidates four generated files with durable group-wide original metadata");

            string foreign = Path.Combine(root, "part27-known-extra-hardlink.tmp");
            NativeFile.NewHardLink(foreign, master);
            var suspicious = CompletedGroupLedger.Inspect(root);
            check(suspicious.NeedsReview == 1 &&
                  suspicious.VerifiedCompleted == 0 &&
                  NativeFile.GetIdentity(master).LinkCount == 6,
                "R2.17 part27: unrecorded extra NTFS hardlink makes whole group untrusted");
            string evidence = Path.Combine(root, "part27-undo-evidence");
            Directory.CreateDirectory(evidence);
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
                "R2.17 part27: external alias forbids group Undo before mutation");
            check(before.Keys.All(path => NativeFile.SameFile(path, master) &&
                  Hash(path) == groupHash),
                "R2.17 part27: unsafe Undo refusal preserves all shared originals");
            if (!NativeFile.SameFile(foreign, master) || Hash(foreign) != groupHash)
                throw new InvalidDataException("Test-owned extra link changed before explicit cleanup");
            File.Delete(foreign);
            check(CompletedGroupLedger.Inspect(root).VerifiedCompleted == 1,
                "R2.17 part27: known generated extra-link cleanup restores exact trusted group evidence");

            string signal = Path.Combine(root, "part27-killed.checkpoint");
            int exit = KillWorker(root, id, signal);
            var paused = CompletedGroupLedger.Inspect(root);
            bool realKill = exit != 0 && File.Exists(signal) &&
                File.ReadAllText(signal) == "first-alias-checkpoint-durable" &&
                paused.ExplicitResumeRequired == 1 &&
                paused.Entries.Single().DetachedCount == 1 &&
                NativeFile.GetIdentity(master).LinkCount == 4 &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0;
            check(realKill,
                "R2.17 part27: actual child kill after durable first Undo checkpoint leaves recoverable partial group");
            int recovered = CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence);
            var restored = CompletedGroupLedger.Inspect(root);
            bool originals = restored.VerifiedRestored == 1 &&
                restored.VerifiedCompleted == 0 && restored.NeedsReview == 0 &&
                restored.Entries.Single().DetachedCount == 3 &&
                NativeFile.GetIdentity(master).LinkCount == 2 &&
                Hash(master) == groupHash;
            foreach (var originalSnapshot in before.Values)
            {
                var now = Safety.Capture(originalSnapshot.Path);
                originals &= now.Identity.LinkCount == (originalSnapshot.Path ==
                    scan.Groups.Single().Representative.Path ? 2u : 1u) &&
                    now.Length == originalSnapshot.Length &&
                    now.CreationUtc == originalSnapshot.CreationUtc &&
                    now.ModifiedUtc == originalSnapshot.ModifiedUtc &&
                    now.Attributes == originalSnapshot.Attributes &&
                    now.SecurityDescriptor.AsSpan().SequenceEqual(
                        originalSnapshot.SecurityDescriptor) &&
                    Hash(now.Path) == groupHash;
            }
            check(recovered == 3 && originals,
                "R2.17 part27: explicit restart completes remaining two Undo aliases; all original bytes/metadata and separate NTFS identities verified");
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
                "R2.17 part27: final restored group cannot be replayed as another Undo");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-group-ledger-resume.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part27-group-ledger-v1",
                    generatedOnly = true,
                    sourceFileCount = 4,
                    realCoreGroupMerged = merged,
                    savedGroupLedgerVerifiedAfterRestart = completed.VerifiedCompleted == 1,
                    unexpectedExtraLinkRefusedBeforeUndo = suspicious.NeedsReview == 1,
                    actualUndoWorkerProcessKilled = realKill,
                    durableDetachedAliasesAtInterruption = paused.Entries.Single().DetachedCount,
                    explicitResumeFinalDetachedAliases = recovered,
                    allBytesTimesOwnerDaclAndNtfsIdsVerified = originals,
                    completedHistoryRestored = restored.VerifiedRestored == 1,
                    productionMergeEnabled = false,
                    productionUndoEnabled = CompletedGroupLedger.ProductionMutationEnabled,
                    actualUserFilesTouched = false,
                    userCapacityFreedBytes = 0,
                    insideAliasCrashRecoveryProven = false,
                    powerCutDurabilityProven = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART27 synthetic four-name group manifest and killed-worker Undo resume PASS");
        }
        finally { Journal.TestFolderPath = oldJournal; }
    }
}
