using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Actual Core DedupeEngine completed-ledger write + independent re-open,
// including forced external worker death after the original backup was deleted.
// No real media path is ever passed to this test class.
internal static class R217CompletedMergeLedgerTests
{
    private const string ChildFlag = "--r217-part24-generated-only-ledger-kill";
    private const string ChildEnv = "CHAPPY_PART24_LEDGER_KILL_GENERATED_ONLY";
    private static string Hash(string file) =>
        Safety.ComputeSha256(file, CancellationToken.None);

    public static bool IsChild(string[] args) =>
        args.Length > 0 && args[0] == ChildFlag;

    private static void AssertGenerated(string root)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(full, ".chappy-part23-generated-only")) ||
            File.ReadAllText(Path.Combine(full, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part24 worker rejected real or unapproved path");
    }

    private static (ScanResult Scan, DuplicateGroup Group, string Hash) BuildPair(
        string root, bool create)
    {
        AssertGenerated(root);
        string a = Path.Combine(root, "original.mp4");
        string b = Path.Combine(root, "duplicate.mp4");
        if (create)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(512 * 1024);
            File.WriteAllBytes(a, bytes);
            File.WriteAllBytes(b, bytes);
            File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
            File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
        }
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        if (scan.Groups.Count != 1 || scan.Groups[0].Files.Count != 2 ||
            scan.Groups[0].BlockReason(false) is not null)
            throw new InvalidDataException("Part24 synthetic pair not two independent exact MP4 files");
        return (scan, scan.Groups[0], scan.Groups[0].Hash);
    }

    private static string PrepareRoot(string parent, string name)
    {
        string root = Path.Combine(parent, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        return root;
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 3 || args[0] != ChildFlag ||
            Environment.GetEnvironmentVariable(ChildEnv) != "generated-only")
            throw new InvalidOperationException("No generated-only ledger crash authorization");
        string root = Path.GetFullPath(args[1]);
        string checkpoint = Path.GetFullPath(args[2]);
        AssertGenerated(root);
        if (!checkpoint.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Checkpoint is outside the synthetic fixture");
        Journal.TestFolderPath = Path.Combine(root, "part24-journal");
        var pair = BuildPair(root, false);
        FaultInjection.OnCheckpoint = phase =>
        {
            if (phase != "backup-deleted") return;
            using (var f = new FileStream(checkpoint, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(
                    "real DedupeEngine backup deleted, ledger prepared, no final commit");
                f.Write(payload);
                f.Flush(flushToDisk: true);
            }
            using var self = Process.GetCurrentProcess();
            self.Kill(); // abrupt exit, no rollback/finally. DO NOT kill unrelated processes.
            Thread.Sleep(Timeout.Infinite);
        };
        DedupeEngine.Run(root, pair.Scan.Groups, false, null, CancellationToken.None,
            testOnlyRecordCompletedMerges: true);
        throw new InvalidOperationException("Part24 test did not reach the genuine crash checkpoint");
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? previous = Journal.TestFolderPath;
        try
        {
            string normal = PrepareRoot(parent, "r217-part24-normal-ledger");
            Journal.TestFolderPath = Path.Combine(normal, "part24-journal");
            var pair = BuildPair(normal, true);
            FileSnapshot originalAlias = pair.Group.Files[1];
            string aliasPath = originalAlias.Path;
            string representative = pair.Group.Representative.Path;
            long beforeMtime = File.GetLastWriteTimeUtc(aliasPath).Ticks;
            string hash = pair.Hash;
            var absent = CompletedMergeLedger.Inspect(normal);
            check(absent.Entries.Count == 0 && !Directory.Exists(Safety.StorePath(normal)),
                "R2.17 part24: production-compatible ledger inspection of empty synthetic root never creates store");
            long logical = DedupeEngine.Run(normal, pair.Scan.Groups, false, null,
                CancellationToken.None, testOnlyRecordCompletedMerges: true);
            var ledger = CompletedMergeLedger.Inspect(normal);
            string master = Path.Combine(Safety.StorePath(normal), hash + ".mp4");
            bool validComplete = ledger.Entries.Count == 1 &&
                ledger.VerifiedCompleted == 1 && ledger.NeedsReview == 0 &&
                logical == originalAlias.Length && !ledger.CanRunProductionUndo &&
                NativeFile.SameFile(aliasPath, master) &&
                NativeFile.GetIdentity(master).LinkCount == 3 &&
                Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0;
            check(validComplete,
                "R2.17 part24: genuine Core merge wrote independently reopenable completed metadata before closing pending journal");
            string id = ledger.Entries.Single().OperationId;
            string completedPath = Path.Combine(Safety.StorePath(normal),
                ".chappy-completed-merge", id + ".completed.json");
            check(File.Exists(completedPath) &&
                  CompletedMergeLedger.Inspect(normal).VerifiedCompleted == 1,
                "R2.17 part24: after simulated app restart, read-only completed ledger verifies live NTFS shared identity");

            string outsider = Path.Combine(normal, "known-test-external-name.tmp");
            NativeFile.NewHardLink(outsider, master);
            var withExtra = CompletedMergeLedger.Inspect(normal);
            check(withExtra.VerifiedCompleted == 0 && withExtra.NeedsReview == 1 &&
                File.Exists(completedPath) && Hash(aliasPath) == hash,
                "R2.17 part24: external alias changes NTFS link count; ledger fails closed preserving evidence");
            expectThrows(() => CompletedMergeLedger.LoadOriginalForSyntheticUndo(normal, id),
                "R2.17 part24: stale ledger with additional hardlink forbids independent Undo");
            File.Delete(outsider); // only exact generated extra alias
            check(CompletedMergeLedger.Inspect(normal).VerifiedCompleted == 1,
                "R2.17 part24: test restores expected exact link count without altering original");

            byte[] savedLedger = File.ReadAllBytes(completedPath);
            File.AppendAllText(completedPath, "synthetic-tamper");
            var tampered = CompletedMergeLedger.Inspect(normal);
            check(tampered.NeedsReview == 1 && tampered.VerifiedCompleted == 0 &&
                Hash(aliasPath) == hash && File.Exists(completedPath),
                "R2.17 part24: malformed completed ledger is never guessed or removed");
            File.WriteAllBytes(completedPath, savedLedger); // restore exact test-owned evidence
            File.SetLastWriteTimeUtc(completedPath,
                File.GetLastWriteTimeUtc(completedPath));
            check(CompletedMergeLedger.Inspect(normal).VerifiedCompleted == 1,
                "R2.17 part24: read-only ledger revalidates after explicitly restored disposable bytes");

            string evidence = Path.Combine(normal, "part24-undo-evidence");
            Directory.CreateDirectory(evidence);
            FileSnapshot recoveredOriginal = CompletedMergeLedger.LoadOriginalForSyntheticUndo(
                normal, id);
            check(recoveredOriginal.Identity.SameFile(originalAlias.Identity) &&
                  recoveredOriginal.CreationUtc == originalAlias.CreationUtc &&
                  recoveredOriginal.ModifiedUtc == originalAlias.ModifiedUtc &&
                  recoveredOriginal.SecurityDescriptor.AsSpan().SequenceEqual(
                      originalAlias.SecurityDescriptor),
                "R2.17 part24: independent Undo reconstructs pre-merge file ID/ctime/mtime/owner-DACL FROM saved ledger, not in-memory scan");
            var undone = IndependentUndoEngine.Restore(normal, recoveredOriginal,
                master, hash, 3, evidence);
            CompletedMergeLedger.MarkRestoredSynthetic(normal, id);
            bool restored = CompletedMergeLedger.Inspect(normal).VerifiedRestored == 1 &&
                NativeFile.GetIdentity(aliasPath).LinkCount == 1 &&
                !NativeFile.SameFile(aliasPath, representative) &&
                Hash(aliasPath) == hash && Hash(representative) == hash &&
                File.GetLastWriteTimeUtc(aliasPath).Ticks == beforeMtime &&
                File.Exists(undone.DurableEvidencePath);
            check(restored,
                "R2.17 part24: test-only independent Undo from reopened ledger restores original metadata and independent NTFS ID");

            string crashRoot = PrepareRoot(parent, "r217-part24-abrupt-ledger");
            Journal.TestFolderPath = Path.Combine(crashRoot, "part24-journal");
            var crashBefore = BuildPair(crashRoot, true);
            string crashAlias = crashBefore.Group.Files[1].Path;
            string crashRepresentative = crashBefore.Group.Representative.Path;
            string crashHash = crashBefore.Hash;
            var beforeAlias = crashBefore.Group.Files[1];
            string checkpoint = Path.Combine(crashRoot, "part24-killed.checkpoint");
            string runtime = Environment.ProcessPath ??
                throw new InvalidOperationException("Cannot locate test runner executable");
            var psi = new ProcessStartInfo
            {
                FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(runtime)!
            };
            if (Path.GetFileName(runtime).Equals("dotnet.exe",
                    StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(typeof(R217CompletedMergeLedgerTests).Assembly.Location);
            foreach (var arg in new[] { ChildFlag, crashRoot, checkpoint })
                psi.ArgumentList.Add(arg);
            psi.Environment[ChildEnv] = "generated-only";
            using var child = Process.Start(psi) ??
                throw new IOException("Part24 generated-only crash child did not start");
            try
            {
                if (!child.WaitForExit(60000))
                    throw new TimeoutException("Part24 crash child failed to exit");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                }
            }
            string crashMaster = Path.Combine(Safety.StorePath(crashRoot),
                crashHash + ".mp4");
            var crashedLedger = CompletedMergeLedger.Inspect(crashRoot);
            bool genuineCrash = child.ExitCode != 0 &&
                File.Exists(checkpoint) &&
                File.ReadAllText(checkpoint).Contains("backup deleted", StringComparison.Ordinal) &&
                crashedLedger.Entries.Count == 1 &&
                crashedLedger.Entries[0].State == "PreparedPendingRecovery" &&
                crashedLedger.VerifiedCompleted == 0 &&
                Journal.Pending().Count == 1 &&
                Journal.UnresolvedRecordFiles().Count == 0 &&
                NativeFile.SameFile(crashAlias, crashMaster) &&
                NativeFile.GetIdentity(crashMaster).LinkCount == 3 &&
                Hash(crashAlias) == crashHash && Hash(crashRepresentative) == crashHash;
            check(genuineCrash,
                "R2.17 part24: actual child-process kill AFTER independent backup deletion leaves PREPARED durable history and pending recovery journal");
            string crashedId = crashedLedger.Entries.Single().OperationId;
            expectThrows(() => CompletedMergeLedger.ConfirmPreparedSynthetic(crashRoot, crashedId),
                "R2.17 part24: prepared ledger cannot auto-promote while crash recovery journal remains");
            var crashPending = Journal.Pending().Single();
            var recovery = RecoveryService.Inspect(crashPending);
            check(recovery.Action == RecoveryAction.ClearVerifiedJournal,
                "R2.17 part24: real existing Core RecoveryService independently classifies post-delete pending journal as verified");
            RecoveryService.Apply(crashPending.OperationId,
                RecoveryAction.ClearVerifiedJournal,
                crashPending.OriginalPath, crashPending.MasterPath);
            var toConfirm = CompletedMergeLedger.Inspect(crashRoot);
            check(toConfirm.PreparedNeedsConfirmation == 1 &&
                  toConfirm.VerifiedCompleted == 0 &&
                  toConfirm.NeedsReview == 0,
                "R2.17 part24: after explicit journal recovery, prepared ledger awaits separate explicit confirmation");
            CompletedMergeLedger.ConfirmPreparedSynthetic(crashRoot, crashedId);
            var afterConfirm = CompletedMergeLedger.Inspect(crashRoot);
            check(afterConfirm.VerifiedCompleted == 1 &&
                  afterConfirm.PreparedNeedsConfirmation == 0,
                "R2.17 part24: restart/recheck manually promotes exact prepared evidence only after clearing independent recovery journal");
            string crashEvidence = Path.Combine(crashRoot, "part24-undo-evidence");
            Directory.CreateDirectory(crashEvidence);
            var reopened = CompletedMergeLedger.LoadOriginalForSyntheticUndo(
                crashRoot, crashedId);
            check(reopened.Identity.SameFile(beforeAlias.Identity) &&
                  reopened.SecurityDescriptor.AsSpan().SequenceEqual(
                      beforeAlias.SecurityDescriptor),
                "R2.17 part24: pre-merge identity/security reconstructed from actual crash-surviving ledger");
            var afterCrashUndo = IndependentUndoEngine.Restore(crashRoot,
                reopened, crashMaster, crashHash, 3, crashEvidence);
            CompletedMergeLedger.MarkRestoredSynthetic(crashRoot, crashedId);
            bool crashIndependence = CompletedMergeLedger.Inspect(crashRoot).VerifiedRestored == 1 &&
                File.Exists(afterCrashUndo.DurableEvidencePath) &&
                !NativeFile.SameFile(crashAlias, crashRepresentative) &&
                NativeFile.GetIdentity(crashAlias).LinkCount == 1 &&
                Hash(crashAlias) == crashHash &&
                Hash(crashRepresentative) == crashHash;
            check(crashIndependence,
                "R2.17 part24: forced-death surviving original metadata drives actual independent Undo and restorable ledger status");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-completed-merge-ledger.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-completed-ledger-v1",
                    generatedOnly = true,
                    actualCoreDedupeEngineWritesLedger = validComplete,
                    normalRealMergeLogicalSavingBytes = logical,
                    restartedReadOnlyLedgerReverified = true,
                    externalHardlinkCausedFailClosedManualReview = withExtra.NeedsReview == 1,
                    malformedLedgerPreservedAndRefused = tampered.NeedsReview == 1,
                    preMergeMetadataReconstructedWithoutOriginalScan = true,
                    normalIndependentUndoFromPersistentLedger = restored,
                    actualChildProcessKilledAfterBackupDeletion = genuineCrash,
                    durablePreparedBeforeCrashObserved = crashedLedger.Entries.Single().State ==
                        "PreparedPendingRecovery",
                    refusedPrematureLedgerPromotion = true,
                    realCorePendingRecoveryJournalReviewedAndCleared = true,
                    explicitPreparedToCompletedRevalidationVerified =
                        afterConfirm.VerifiedCompleted == 1,
                    crashSurvivingLedgerUsedForIndependentUndo = crashIndependence,
                    allSyntheticOriginalHashesPreserved = Hash(aliasPath) == hash &&
                        Hash(representative) == hash &&
                        Hash(crashAlias) == crashHash &&
                        Hash(crashRepresentative) == crashHash,
                    productionReadsLedgerWithoutMutation = true,
                    productionDedupeEnabled = false,
                    productionIndependentUndoEnabled = IndependentUndoEngine.ProductionEnabled,
                    productionOriginalReplacementEnabled = false,
                    powerLossAndArbitraryAclRecoveryProven = false,
                    actualUserMediaTouched = false,
                    actualUserBytesRecovered = 0,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART24 durable completed merge ledger, genuine child kill and independent Undo PASS");
        }
        finally { Journal.TestFolderPath = previous; }
    }
}
