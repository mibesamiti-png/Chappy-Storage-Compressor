using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part26: multiple independent completed-merge ledgers in one real Core run,
// followed by separately re-opened per-group Undo. Only generated NTFS files.
internal static class R217MultiGroupLedgerTests
{
    private static string Hash(string path) =>
        Safety.ComputeSha256(path, CancellationToken.None);

    private static void IndependentCopy(string source, string target)
    {
        File.Copy(source, target);
        File.SetAttributes(target, File.GetAttributes(source));
        File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? oldJournal = Journal.TestFolderPath;
        try
        {
            string root = Path.Combine(parent, "r217-part26-multi-group-generated");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            Journal.TestFolderPath = Path.Combine(root, "part26-journal");

            string png = Path.Combine(root, "a-image.png");
            string jpg = Path.Combine(root, "b-image.jpg");
            string mp4 = Path.Combine(root, "c-video.mp4");
            R217PriorityImageAndMemoryTests.WriteUncompressedPng(png, 512, 320, 1);
            R217PriorityImageAndMemoryTests.WriteJpeg(jpg, 960, 640, 1);
            File.WriteAllBytes(mp4, RandomNumberGenerator.GetBytes(288 * 1024));
            IndependentCopy(png, Path.Combine(root, "a-copy.png"));
            IndependentCopy(jpg, Path.Combine(root, "b-copy.jpg"));
            IndependentCopy(mp4, Path.Combine(root, "c-copy.mp4"));

            // Explicitly unsupported for the v1 completed-ledger layout:
            // a 3-name group must be skipped BEFORE its first destructive step.
            string triple = Path.Combine(root, "d-triple1.mp4");
            File.WriteAllBytes(triple, RandomNumberGenerator.GetBytes(173 * 1024));
            IndependentCopy(triple, Path.Combine(root, "d-triple2.mp4"));
            IndependentCopy(triple, Path.Combine(root, "d-triple3.mp4"));

            ScanResult scan = Scanner.Run(root, true, null, CancellationToken.None);
            var pairs = scan.Groups.Where(g => g.Files.Count == 2).ToArray();
            var unsupported = scan.Groups.Single(g => g.Files.Count == 3);
            check(scan.Groups.Count == 4 && pairs.Length == 3 &&
                  scan.Groups.All(g => g.BlockReason(false) is null),
                "R2.17 part26: real scanner verifies three independent pairs and one unsupported triple");
            var snapshots = scan.Groups.SelectMany(g => g.Files)
                .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var hashes = snapshots.ToDictionary(x => x.Key, x => Hash(x.Key),
                StringComparer.OrdinalIgnoreCase);

            long expected = pairs.Sum(g => g.Files[1].Length);
            long actual = DedupeEngine.Run(root, scan.Groups, false, null,
                CancellationToken.None, testOnlyRecordCompletedMerges: true);
            var ledger = CompletedMergeLedger.Inspect(root);
            bool pairProof = actual == expected && ledger.VerifiedCompleted == 3 &&
                ledger.Entries.Count == 3 && ledger.NeedsReview == 0 &&
                Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0;
            foreach (DuplicateGroup group in pairs)
            {
                string master = Path.Combine(Safety.StorePath(root),
                    group.Hash + Path.GetExtension(group.Representative.Path).ToLowerInvariant());
                pairProof &= File.Exists(master) &&
                    NativeFile.GetIdentity(master).LinkCount == 3 &&
                    group.Files.All(f => NativeFile.SameFile(f.Path, master) &&
                        Hash(f.Path) == hashes[f.Path]);
            }
            bool tripleUntouched = unsupported.Files.All(f =>
                NativeFile.GetIdentity(f.Path).LinkCount == 1 &&
                NativeFile.GetIdentity(f.Path).SameFile(snapshots[f.Path].Identity) &&
                Hash(f.Path) == hashes[f.Path]);
            check(pairProof && tripleUntouched,
                "R2.17 part26: Core writes three verified durable ledgers, skips unsupported triple without mutation or pending journal");

            // This is a new Inspect, not the old Scanner or a cached pre-merge object.
            var reopened = CompletedMergeLedger.Inspect(root);
            check(reopened.VerifiedCompleted == 3 && reopened.CompletedLogicalBytes == expected &&
                  !reopened.CanRunProductionUndo,
                "R2.17 part26: all three distinct completed records independently survive reopen");

            var selected = reopened.Entries.OrderBy(e => e.RelativeAlias,
                StringComparer.OrdinalIgnoreCase).ToArray();
            string badId = selected[0].OperationId;
            FileSnapshot badOriginal = CompletedMergeLedger.LoadOriginalForSyntheticUndo(
                root, badId);
            string badHash = Hash(badOriginal.Path);
            string badMaster = Path.Combine(Safety.StorePath(root),
                badHash + Path.GetExtension(badOriginal.Path).ToLowerInvariant());
            string external = Path.Combine(root, "part26-test-owned-external-link.tmp");
            NativeFile.NewHardLink(external, badMaster);
            var suspicious = CompletedMergeLedger.Inspect(root);
            check(suspicious.NeedsReview == 1 && suspicious.VerifiedCompleted == 2 &&
                  suspicious.Entries.Single(e => e.OperationId == badId).State ==
                      "ManualReview",
                "R2.17 part26: extra alias invalidates only its own ledger, not other groups");
            expectThrows(() => CompletedMergeLedger.LoadOriginalForSyntheticUndo(root, badId),
                "R2.17 part26: tampered group cannot issue an independent Undo snapshot");

            string evidence = Path.Combine(root, "part26-undo-evidence");
            Directory.CreateDirectory(evidence);
            int restoredCount = 0;
            bool metadata = true;
            foreach (var entry in selected.Where(e => e.OperationId != badId))
            {
                FileSnapshot saved = CompletedMergeLedger.LoadOriginalForSyntheticUndo(
                    root, entry.OperationId);
                string hash = Hash(saved.Path);
                string master = Path.Combine(Safety.StorePath(root),
                    hash + Path.GetExtension(saved.Path).ToLowerInvariant());
                var undo = IndependentUndoEngine.Restore(root, saved, master,
                    hash, 3, evidence);
                CompletedMergeLedger.MarkRestoredSynthetic(root, entry.OperationId);
                FileSnapshot after = Safety.Capture(saved.Path);
                metadata &= File.Exists(undo.DurableEvidencePath) &&
                    !NativeFile.SameFile(saved.Path, master) &&
                    after.Identity.LinkCount == 1 &&
                    after.Length == saved.Length &&
                    after.CreationUtc == saved.CreationUtc &&
                    after.ModifiedUtc == saved.ModifiedUtc &&
                    after.Attributes == saved.Attributes &&
                    after.SecurityDescriptor.AsSpan().SequenceEqual(saved.SecurityDescriptor) &&
                    Hash(saved.Path) == hashes[saved.Path] &&
                    NativeFile.GetIdentity(master).LinkCount == 2;
                restoredCount++;
            }
            check(restoredCount == 2 && metadata &&
                  CompletedMergeLedger.Inspect(root).VerifiedRestored == 2,
                "R2.17 part26: unaffected groups independently restore from persisted history while a different group needs review");

            // Never delete an unexpected user alias; this one was created and
            // identity-verified by the synthetic test itself.
            check(NativeFile.SameFile(external, badMaster) &&
                  Hash(external) == badHash,
                "R2.17 part26: extra test hardlink is identified before test-only cleanup");
            File.Delete(external);
            FileSnapshot finalSaved = CompletedMergeLedger.LoadOriginalForSyntheticUndo(
                root, badId);
            var finalUndo = IndependentUndoEngine.Restore(root, finalSaved,
                badMaster, badHash, 3, evidence);
            CompletedMergeLedger.MarkRestoredSynthetic(root, badId);
            FileSnapshot finalAfter = Safety.Capture(finalSaved.Path);
            metadata &= File.Exists(finalUndo.DurableEvidencePath) &&
                finalAfter.Identity.LinkCount == 1 &&
                !NativeFile.SameFile(finalSaved.Path, badMaster) &&
                finalAfter.CreationUtc == finalSaved.CreationUtc &&
                finalAfter.ModifiedUtc == finalSaved.ModifiedUtc &&
                finalAfter.SecurityDescriptor.AsSpan().SequenceEqual(
                    finalSaved.SecurityDescriptor);
            restoredCount++;

            var finished = CompletedMergeLedger.Inspect(root);
            check(restoredCount == 3 && metadata &&
                  finished.VerifiedRestored == 3 &&
                  finished.VerifiedCompleted == 0 && finished.NeedsReview == 0 &&
                  unsupported.Files.All(f =>
                      NativeFile.GetIdentity(f.Path).LinkCount == 1 &&
                      Hash(f.Path) == hashes[f.Path]) &&
                  Journal.Pending().Count == 0 &&
                  Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 part26: three independent Undo operations preserve bytes, security and NTFS identities; unsupported triple remains independent");
            expectThrows(() => CompletedMergeLedger.LoadOriginalForSyntheticUndo(root, badId),
                "R2.17 part26: restored operation cannot be re-used for a second Undo");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-multigroup-ledger.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part26-multigroup-ledger-v1",
                    generatedOnly = true,
                    verifiedScannerGroups = scan.Groups.Count,
                    durableIndependentPairLedgers = reopened.VerifiedCompleted,
                    unsupportedTripleSkippedBeforeMutation = tripleUntouched,
                    reopenedIndependentLedgerProof = reopened.VerifiedCompleted == 3,
                    externalAliasIsolatedToOneGroup = suspicious.NeedsReview == 1 &&
                        suspicious.VerifiedCompleted == 2,
                    unaffectedUndoGroupsWhileManualReview = 2,
                    originalMetadataAndBytesRestored = metadata,
                    finalVerifiedRestoredGroups = finished.VerifiedRestored,
                    pendingRecoveryJournals = Journal.Pending().Count,
                    productionDedupeEnabled = false,
                    productionUndoEnabled = IndependentUndoEngine.ProductionEnabled,
                    actualUserMediaTouched = false,
                    actualUserBytesRecovered = 0,
                    arbitraryMultiAliasUndoProven = false,
                    powerLossDurabilityProven = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART26 multi-group persisted ledger and guarded independent Undo PASS");
        }
        finally
        {
            Journal.TestFolderPath = oldJournal;
        }
    }
}
