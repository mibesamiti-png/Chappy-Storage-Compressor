using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part30: classification-only cross-ledger proof. All mutations below prepare
// artificial CI media/evidence and explicitly remove ONLY test-owned artifacts.
internal static class R217RecoveryEvidenceOverviewTests
{
    public static void Run(string parent, Action<bool, string> check)
    {
        string root = Path.Combine(parent, "r217-part30-read-only-overview");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string? previousJournal = Journal.TestFolderPath;
        Journal.TestFolderPath = Path.Combine(root, "isolated-journal");
        try
        {
            string a = Path.Combine(root, "video-a.mp4");
            File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(192 * 1024));
            for (int i = 1; i <= 2; i++)
            {
                string child = Path.Combine(root, "video-" +
                    (i == 1 ? "b" : "c") + ".mp4");
                File.Copy(a, child);
                File.SetCreationTimeUtc(child, File.GetCreationTimeUtc(a));
                File.SetLastWriteTimeUtc(child, File.GetLastWriteTimeUtc(a));
            }
            var scan = Scanner.Run(root, true, null, CancellationToken.None);
            check(scan.Groups.Count == 1 && scan.Groups[0].Files.Count == 3 &&
                scan.Groups[0].BlockReason(false) is null,
                "R2.17 Part30: disposable, independently owned 3-filename NTFS test group");

            var before = scan.Groups[0].Files.ToDictionary(s => s.Path,
                s => (Identity: NativeFile.GetIdentity(s.Path),
                    Hash: Safety.ComputeSha256(s.Path, CancellationToken.None)));
            string store = Safety.StorePath(root);
            var empty = RecoveryEvidenceOverview.Inspect(root);
            check(empty.Rows.Count == 0 && empty.AllEvidenceVerified &&
                  !empty.ProductionMergeOrUndoAllowed &&
                  !RecoveryEvidenceOverview.ProductionMutationEnabled &&
                  !Directory.Exists(store) &&
                  before.All(p => NativeFile.GetIdentity(p.Key).SameFile(p.Value.Identity)),
                "R2.17 Part30: empty read-only inspection creates no NTFS store, never enables production write");

            string groupFolder = Path.Combine(store, ".chappy-group-completed");
            Directory.CreateDirectory(groupFolder);
            string orphanId = Guid.NewGuid().ToString("N");
            string orphan = Path.Combine(groupFolder, orphanId + ".checkpoint.json");
            File.WriteAllText(orphan, "{\"Schema\":1,\"Id\":\"" + orphanId +
                "\",\"Detached\":[]}");
            var unknown = RecoveryEvidenceOverview.Inspect(root);
            check(unknown.RequiresAttention == 1 && unknown.GroupHistory == 1 &&
                unknown.Rows.Any(e => e.Source == "複数ファイルのグループ履歴" &&
                    e.NeedsAttention && e.State == "ManualReview"),
                "R2.17 Part30: orphaned group Undo checkpoint becomes manual-review evidence");
            File.Delete(orphan); // this GUID-scoped synthetic checkpoint only
            check(RecoveryEvidenceOverview.Inspect(root).RequiresAttention == 0,
                "R2.17 Part30: explicit generated orphan cleanup restores clear read-only evidence");

            string op = CompletedGroupLedger.MergeSynthetic(root, scan.Groups.Single());
            string master = Path.Combine(store, scan.Groups.Single().Hash + ".mp4");
            var merged = RecoveryEvidenceOverview.Inspect(root);
            check(merged.GroupHistory == 1 && merged.RequiresAttention == 0 &&
                merged.Rows.Single().State == "VerifiedCompleted" &&
                !merged.ProductionMergeOrUndoAllowed &&
                NativeFile.GetIdentity(master).LinkCount == 4 &&
                scan.Groups.Single().Files.All(x => NativeFile.SameFile(x.Path, master)),
                "R2.17 Part30: real generated three-file completed group displays verified, non-actionable history");

            string unexpected = Path.Combine(root, "part30-test-owned-external-hardlink.tmp");
            NativeFile.NewHardLink(unexpected, master);
            var tampered = RecoveryEvidenceOverview.Inspect(root);
            check(tampered.GroupHistory == 1 &&
                tampered.RequiresAttention == 1 &&
                tampered.Rows.Single().State == "ManualReview" &&
                NativeFile.GetIdentity(master).LinkCount == 5 &&
                scan.Groups.Single().Files.All(x => NativeFile.SameFile(x.Path, master)),
                "R2.17 Part30: unexpected shared external alias is visible and cannot be mistaken for safe recovery");
            if (!NativeFile.SameFile(unexpected, master) ||
                Safety.ComputeSha256(unexpected, CancellationToken.None) !=
                scan.Groups.Single().Hash)
                throw new InvalidDataException("Test-owned external alias changed before cleanup");
            File.Delete(unexpected);
            check(RecoveryEvidenceOverview.Inspect(root).AllEvidenceVerified,
                "R2.17 Part30: removing only known generated extra alias permits exact revalidation");

            string pairFolder = Path.Combine(store, ".chappy-completed-merge");
            Directory.CreateDirectory(pairFolder);
            string badPair = Path.Combine(pairFolder, "untrusted-incomplete.tmp");
            File.WriteAllText(badPair, "not-a-valid-completed-ledger");
            var pairBad = RecoveryEvidenceOverview.Inspect(root);
            check(pairBad.RequiresAttention == 1 &&
                pairBad.Rows.Any(x => x.Source == "2件単位の統合・Undo履歴" &&
                    x.State == "Malformed" && x.NeedsAttention),
                "R2.17 Part30: unfinished two-file ledger cannot be hidden by verified group history");
            File.Delete(badPair);

            string crashFolder = Path.Combine(store, ".chappy-independent-undo-recovery");
            Directory.CreateDirectory(crashFolder);
            string unknownCrash = Path.Combine(crashFolder, "untrusted.bad.json");
            File.WriteAllText(unknownCrash, "{}");
            var crashBad = RecoveryEvidenceOverview.Inspect(root);
            check(crashBad.RequiresAttention == 1 &&
                crashBad.UndoCrashHistory == 1 &&
                crashBad.Rows.Any(x => x.Source == "Undo異常終了の記録" &&
                    x.State == "ManualReview"),
                "R2.17 Part30: malformed interrupted-Undo record is highlighted, not deleted");
            File.Delete(unknownCrash);

            Directory.CreateDirectory(Journal.FolderPath);
            string brokenPending = Path.Combine(Journal.FolderPath, "not-committed.tmp");
            File.WriteAllText(brokenPending, "test-only pending record");
            var pending = RecoveryEvidenceOverview.Inspect(root);
            check(pending.PendingLegacy == 1 && pending.RequiresAttention == 1 &&
                pending.Rows.Any(x => x.Source == "未完了の操作記録" &&
                    x.NeedsAttention),
                "R2.17 Part30: malformed legacy pending journal blocks any all-clear claim");
            File.Delete(brokenPending);

            // Force a previously verified group into its interrupted
            // between-alias state; the overview itself must not resume it.
            string evidence = Path.Combine(root, "part30-undo-evidence");
            Directory.CreateDirectory(evidence);
            try
            {
                CompletedGroupLedger.ResumeUndoSynthetic(root, op, evidence,
                    afterDurableAlias: i =>
                    {
                        if (i == 1) throw new TestInterruption();
                    });
            }
            catch (TestInterruption) { }
            var interrupted = RecoveryEvidenceOverview.Inspect(root);
            check(interrupted.RequiresAttention == 1 &&
                interrupted.Rows.Single().State == "ExplicitResumeRequired" &&
                NativeFile.GetIdentity(master).LinkCount == 3,
                "R2.17 Part30: durably interrupted group is shown as action-required without automatic continuation");
            var twice = RecoveryEvidenceOverview.Inspect(root);
            check(twice.RequiresAttention == 1 &&
                NativeFile.GetIdentity(master).LinkCount == 3,
                "R2.17 Part30: repeating the UI's read-only inspection never retries Undo");
            int restored = CompletedGroupLedger.ResumeUndoSynthetic(root, op, evidence);
            var done = RecoveryEvidenceOverview.Inspect(root);
            check(restored == 2 && done.AllEvidenceVerified &&
                done.GroupHistory == 1 && done.Rows.Single().State == "VerifiedRestored" &&
                NativeFile.GetIdentity(master).LinkCount == 2 &&
                scan.Groups.Single().Files.All(x =>
                    Safety.ComputeSha256(x.Path, CancellationToken.None) ==
                    scan.Groups.Single().Hash),
                "R2.17 Part30: explicit existing test-only Undo still works; dashboard merely reports final verified state");
            check(!Directory.EnumerateFiles(root, "part30-test-owned-*").Any() &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0 &&
                done.Describe().Contains("本番の重複統合・独立Undo・原本置換は無効",
                    StringComparison.Ordinal),
                "R2.17 Part30: historical inspector preserves strict production mutation lock and no orphan test files");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-part30-recovery-overview.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part30-recovery-overview-v1",
                    verdict = "PASS",
                    generatedOnly = true,
                    readOnlyEmptyDidNotCreateStore = true,
                    orphanCheckpointManualReview = true,
                    validGroupReportedButNotActionable = true,
                    changedHardlinkCountRefused = true,
                    malformedTwoFileLedgerReported = true,
                    malformedUndoCrashJournalReported = true,
                    incompleteLegacyJournalReported = true,
                    interruptedGroupRequiresExplicitResume = true,
                    repeatingInspectionNeverMutates = true,
                    verifiedAfterExplicitTestOnlyUndo = true,
                    productionMergeEnabled = false,
                    productionUndoEnabled = false,
                    realUserFilesTouched = false,
                    realUserCapacityFreedBytes = 0,
                    actualPowerCutProven = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART30 read-only cross-ledger recovery safety overview PASS");
        }
        finally { Journal.TestFolderPath = previousJournal; }
    }

    private sealed class TestInterruption : Exception { }
}
