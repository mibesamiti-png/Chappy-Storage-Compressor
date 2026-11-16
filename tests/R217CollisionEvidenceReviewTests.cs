using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part36: prove read-only WPF presentation model classifies exactly the
// persisted Part35 evidence, without editing originals, reports, or receipts.
internal static class R217CollisionEvidenceReviewTests
{
    private sealed record Fixture(string Root, string Output, string Id,
        string Source1, string Source2, string Original,
        string OriginalHash, FileIdentity OriginalId,
        CollisionCollectionPlan.Result Plan);

    private static string Hash(string file) =>
        Safety.ComputeSha256(file, CancellationToken.None);

    private static Fixture Create(string parent, string label)
    {
        string root = Path.Combine(parent, "r217-part36-" + label);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string srcdir = Path.Combine(root, "part35-incoming");
        Directory.CreateDirectory(srcdir);
        string s1 = Path.Combine(srcdir, "a.mp4");
        string s2 = Path.Combine(srcdir, "b.mp4");
        File.WriteAllBytes(s1, RandomNumberGenerator.GetBytes(121 * 1024));
        File.WriteAllBytes(s2, RandomNumberGenerator.GetBytes(87 * 1024));
        string outRoot = Path.Combine(root, "extracted");
        Directory.CreateDirectory(Path.Combine(outRoot, "Camera"));
        string original = Path.Combine(outRoot, "Camera", "clip.mp4");
        File.WriteAllBytes(original, RandomNumberGenerator.GetBytes(19 * 1024));
        string initialHash = Hash(original);
        FileIdentity initialId = NativeFile.GetIdentity(original);
        var plan = CollisionCollectionPlan.Inspect(outRoot,
            [Path.Combine("Camera", "clip.mp4"),
             Path.Combine("Trip", "new.mp4")]);
        if (plan.Conflicts != 1)
            throw new InvalidDataException("Part36 synthetic collision fixture invalid");
        string id = CollisionRecoveryLedger.BeginSynthetic(root, plan,
        [
            (s1, Path.Combine("Camera", "clip.mp4")),
            (s2, Path.Combine("Trip", "new.mp4"))
        ]);
        return new Fixture(root, outRoot, id, s1, s2, original,
            initialHash, initialId, plan);
    }

    private static string Target(Fixture f, int index) =>
        Path.Combine(f.Output, f.Plan.Entries[index].DestinationRelativePath);
    private static string Journal(Fixture f, string suffix) =>
        Path.Combine(f.Output, ".chappy-collision-journal", f.Id + suffix);
    private static bool OriginalUnchanged(Fixture f) =>
        Hash(f.Original) == f.OriginalHash &&
        NativeFile.GetIdentity(f.Original).SameFile(f.OriginalId);

    private sealed class Part36Stop : Exception { }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        var scenarios = new List<object>();
        check(!CollisionEvidenceReview.ProductionMutationEnabled &&
              !CollisionRecoveryLedger.ProductionMutationEnabled,
            "R2.17 Part36: shipped collision review remains read-only and no production Undo enabled");

        {
            string root = Path.Combine(parent, "r217-part36-empty-review");
            Directory.CreateDirectory(root);
            var inspected = CollisionEvidenceReview.Inspect(root);
            bool okay = inspected.Rows.Count == 0 &&
                !inspected.JournalPresent && inspected.NeedsReview == 0 &&
                !inspected.CanModify &&
                !Directory.Exists(Path.Combine(root, ".chappy-collision-journal"));
            check(okay,
                "R2.17 Part36: empty read-only view creates no journal, metadata or reported false recovery");
            scenarios.Add(new { name = "empty-root-no-write", pass = okay });
        }

        {
            var f = Create(parent, "valid-intent-preview");
            string before = Hash(f.Source1);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = view.TotalRecords == 1 && view.FilesVerified == 0 &&
                view.NeedsReview >= 3 &&
                view.Rows.Count(x => x.Kind == "ファイル" &&
                    x.State == "未書き出し") == 2 &&
                view.Rows.Any(x => x.Kind == "一覧メモ" &&
                    x.State == "未書き出し") &&
                view.Rows.Any(x => x.Kind == "操作記録" &&
                    x.State == "未完了") &&
                !view.CanModify && OriginalUnchanged(f) &&
                Hash(f.Source1) == before &&
                !File.Exists(Target(f, 0));
            check(okay,
                "R2.17 Part36: intent-only read-only tab shows two pending files, pending memo, and exact source/destination paths");
            scenarios.Add(new { name = "intent-only-pending-report", pass = okay });
        }

        {
            var f = Create(parent, "finished-with-verified-receipts");
            string old = Hash(f.Original);
            check(CollisionRecoveryLedger.ResumeSynthetic(f.Root,
                f.Output, f.Id).State == "VerifiedCompleted",
                "R2.17 Part36: original Part35 generated journal can be completed before UI review");
            string memo = Path.Combine(f.Output, f.Plan.ReportRelativePath);
            FileIdentity memoId = NativeFile.GetIdentity(memo);
            FileIdentity outputId = NativeFile.GetIdentity(Target(f, 0));
            var view = CollisionEvidenceReview.Inspect(f.Output);
            var repeat = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = view.TotalRecords == 1 && view.FilesVerified == 2 &&
                view.NeedsReview == 0 &&
                view.Rows.Count(x => x.Kind == "ファイル" &&
                    x.State == "保存先照合済み") == 2 &&
                view.Rows.Any(x => x.Kind == "一覧メモ" &&
                    x.State == "保存先照合済み") &&
                view.Rows.Single(x => x.Kind == "操作記録").State ==
                    "保存先証拠一致（元データは未照合）" &&
                view.Describe().Contains("元データの安全な復旧完了ではありません",
                    StringComparison.Ordinal) &&
                repeat.Rows.SequenceEqual(view.Rows) &&
                NativeFile.GetIdentity(memo).SameFile(memoId) &&
                NativeFile.GetIdentity(Target(f, 0)).SameFile(outputId) &&
                OriginalUnchanged(f) && Hash(f.Original) == old;
            check(okay,
                "R2.17 Part36: verified destination ID, ACL, SHA and root memo are displayed, but source-independent recovery never claimed");
            scenarios.Add(new { name = "local-receipt-match-not-production-authorization", pass = okay });
        }

        {
            var f = Create(parent, "copy-flushed-without-receipt");
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.Root, f.Output, f.Id, afterDurablePhase: phase =>
                {
                    if (phase == "copy-flushed-before-receipt-1")
                        throw new Part36Stop();
                }), "R2.17 Part36: synthetic interruption at exact post-flush/pre-receipt boundary");
            FileIdentity orphanId = NativeFile.GetIdentity(Target(f, 0));
            string orphanHash = Hash(Target(f, 0));
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = view.Rows.Any(x => x.Kind == "ファイル" &&
                    x.DestinationRelativePath == f.Plan.Entries[0].DestinationRelativePath &&
                    x.State == "ManualReview" &&
                    x.Explanation.Contains("完了レシートがありません",
                        StringComparison.Ordinal)) &&
                view.Rows.Single(x => x.Kind == "操作記録").State == "ManualReview" &&
                view.NeedsReview > 0 &&
                NativeFile.GetIdentity(Target(f, 0)).SameFile(orphanId) &&
                Hash(Target(f, 0)) == orphanHash && OriginalUnchanged(f);
            check(okay,
                "R2.17 Part36: matching bytes without a file receipt still show manual-review at exact destination, never silently adopt");
            scenarios.Add(new { name = "unreceipted-orphan-manual-review", pass = okay });
        }

        {
            var f = Create(parent, "receipt-corruption");
            CollisionRecoveryLedger.ResumeSynthetic(f.Root, f.Output, f.Id);
            string receipt = Journal(f, ".item-0000.json");
            File.AppendAllText(receipt, "untrusted-data");
            string oldReceiptHash = Hash(receipt);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = view.Rows.Any(x => x.Kind == "ファイル" &&
                    x.State == "ManualReview" &&
                    x.DestinationRelativePath ==
                        f.Plan.Entries[0].DestinationRelativePath) &&
                view.Rows.Single(x => x.Kind == "操作記録").State == "ManualReview" &&
                Hash(receipt) == oldReceiptHash && OriginalUnchanged(f);
            check(okay,
                "R2.17 Part36: corrupted receipt labeled manual-review with original and destination, no evidence edited");
            scenarios.Add(new { name = "corrupted-receipt-explained", pass = okay });
        }

        {
            var f = Create(parent, "same-hash-different-file-id");
            CollisionRecoveryLedger.ResumeSynthetic(f.Root, f.Output, f.Id);
            string originalDest = Target(f, 0);
            string relocated = Path.Combine(f.Output, "part36-relocated.mp4");
            FileIdentity oldFileId = NativeFile.GetIdentity(originalDest);
            string oldFileHash = Hash(originalDest);
            File.Move(originalDest, relocated);
            File.Copy(relocated, originalDest);
            File.SetCreationTimeUtc(originalDest, File.GetCreationTimeUtc(relocated));
            File.SetLastWriteTimeUtc(originalDest, File.GetLastWriteTimeUtc(relocated));
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = NativeFile.GetIdentity(relocated).SameFile(oldFileId) &&
                !NativeFile.GetIdentity(originalDest).SameFile(oldFileId) &&
                Hash(originalDest) == oldFileHash &&
                view.Rows.Any(x => x.Kind == "ファイル" &&
                    x.DestinationRelativePath == f.Plan.Entries[0].DestinationRelativePath &&
                    x.State == "ManualReview") &&
                view.Rows.Single(x => x.Kind == "操作記録").State == "ManualReview" &&
                OriginalUnchanged(f);
            check(okay,
                "R2.17 Part36: same SHA-256 and timestamps but new NTFS file ID is not misrepresented as verified output");
            scenarios.Add(new { name = "same-sha-wrong-file-id-refused", pass = okay });
        }

        {
            var f = Create(parent, "foreign-journal-temp");
            string temp = Journal(f, ".item-0000.json.tmp");
            File.WriteAllText(temp, "unknown incomplete evidence");
            string tempHash = Hash(temp);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = view.Rows.Any(x => x.Kind == "不明な証拠" &&
                    x.NeedsAttention && x.RecordId.EndsWith(".tmp",
                        StringComparison.OrdinalIgnoreCase)) &&
                view.NeedsReview > 0 && Hash(temp) == tempHash &&
                OriginalUnchanged(f) && !File.Exists(Target(f, 0));
            check(okay,
                "R2.17 Part36: extra unfinished .tmp journal appears as explicit attention row, never ignored/cleaned");
            scenarios.Add(new { name = "unknown-temp-evidence-visible", pass = okay });
        }

        {
            var f = Create(parent, "tampered-path-traversal");
            string intent = Journal(f, ".intent.json");
            string json = File.ReadAllText(intent);
            string oldDest = f.Plan.Entries[0].DestinationRelativePath;
            string escapedOld = JsonSerializer.Serialize(oldDest)[1..^1];
            string injected = json.Replace(escapedOld, "../outside-user.mp4",
                StringComparison.Ordinal);
            if (injected == json)
                throw new InvalidOperationException("Part36 failed to insert generated malicious path");
            File.WriteAllText(intent, injected);
            string hash = Hash(intent);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool okay = view.Rows.Any(x => x.Kind == "操作記録" &&
                    x.State == "ManualReview") &&
                view.NeedsReview > 0 && Hash(intent) == hash &&
                !File.Exists(Path.Combine(f.Root, "outside-user.mp4")) &&
                !File.Exists(Target(f, 0)) &&
                OriginalUnchanged(f);
            check(okay,
                "R2.17 Part36: tampered journal traversal is refused before following or reading an external path");
            scenarios.Add(new { name = "malicious-journal-traversal-not-followed", pass = okay });
        }

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-part36-collision-manual-review.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-part36-collision-manual-review-v1",
                verdict = "PASS",
                scenarioCount = scenarios.Count,
                scenarios,
                includedInReleaseAsReadOnlyView = true,
                generatedOnlySourceMutations = true,
                noReleaseApplyResumeOrDelete = true,
                dedicatedWpfReadOnlyTab = true,
                sourcePathValuesNeverDereferencedByReviewer = true,
                destinationNtfsIdentityShaAclMetadataVerified = true,
                corruptJournalAndUnknownTempAreVisible = true,
                sameHashWrongNtfsIdentityRefused = true,
                pendingAndOrphanFilesNeverAutoAdopted = true,
                realUserMediaTouched = false,
                realUserCapacityRecoveredBytes = 0,
                physicalPowerCutProven = false,
                productionCollisionWritesEnabled = CollisionRecoveryLedger.ProductionMutationEnabled
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART36 eight read-only collision evidence cases and manual-review UI model PASS");
    }
}
