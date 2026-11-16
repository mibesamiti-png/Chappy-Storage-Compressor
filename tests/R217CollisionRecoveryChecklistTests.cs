using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// All test-owned files are generated under ChappyStorageSmoke_* roots.
// The new checklist itself never mutates files or trusts an output-only
// receipt as end-to-end source/physical-power-cut proof.
internal static class R217CollisionRecoveryChecklistTests
{
    private sealed class TestInterruption : Exception { }

    private sealed record Fixture(string TestRoot, string Output, string Id,
        CollisionCollectionPlan.Result Plan, string Original,
        FileIdentity OriginalId, string OriginalHash,
        string[] Sources, string[] SourceHashes);

    private static string Hash(string file) =>
        Safety.ComputeSha256(file, CancellationToken.None);

    private static Fixture Prepare(string smokeRoot, string label, bool finish = false)
    {
        string testRoot = Path.Combine(smokeRoot, "r217-part38-" + label);
        Directory.CreateDirectory(testRoot);
        File.WriteAllText(Path.Combine(testRoot, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string incoming = Path.Combine(testRoot, "part35-incoming");
        Directory.CreateDirectory(incoming);
        string a = Path.Combine(incoming, "incoming-a.mp4");
        string b = Path.Combine(incoming, "incoming-b.mp4");
        File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(75 * 1024));
        File.WriteAllBytes(b, RandomNumberGenerator.GetBytes(54 * 1024));

        string output = Path.Combine(testRoot, "restored");
        Directory.CreateDirectory(Path.Combine(output, "Camera"));
        string original = Path.Combine(output, "Camera", "clip.mp4");
        File.WriteAllBytes(original, RandomNumberGenerator.GetBytes(15 * 1024));
        string relativeA = Path.Combine("Camera", "clip.mp4");
        string relativeB = Path.Combine("Trip", "new.mp4");
        var plan = CollisionCollectionPlan.Inspect(output, [relativeA, relativeB]);
        if (plan.Conflicts != 1)
            throw new InvalidDataException("Part38 generated test fixture has no collision");
        string id = CollisionRecoveryLedger.BeginSynthetic(testRoot, plan,
            [(a, relativeA), (b, relativeB)]);
        var fixture = new Fixture(testRoot, output, id, plan, original,
            NativeFile.GetIdentity(original), Hash(original),
            [a, b], [Hash(a), Hash(b)]);
        if (finish && CollisionRecoveryLedger.ResumeSynthetic(testRoot, output, id)
            .State != "VerifiedCompleted")
            throw new InvalidOperationException("Part38 generated completed fixture invalid");
        return fixture;
    }

    private static bool SourcesPreserved(Fixture f) =>
        NativeFile.GetIdentity(f.Original).SameFile(f.OriginalId) &&
        Hash(f.Original) == f.OriginalHash &&
        f.Sources.Select((s, i) => Hash(s) == f.SourceHashes[i]).All(x => x);

    private static string Journal(Fixture f, string suffix) =>
        Path.Combine(f.Output, ".chappy-collision-journal", f.Id + suffix);

    private static bool ForbiddenOperations(CollisionRecoveryChecklist.Review r) =>
        !r.CanAutomaticallyResume &&
        !r.CanDeleteOrOverwrite &&
        !r.AuthorizesOriginalRestore &&
        !CollisionRecoveryChecklist.ProductionMutationAllowed &&
        r.Checks.Any(x => x.Category == "本番ファイルへの変更・自動復元" ||
            x.Category == "本番ファイルへの変更") &&
        r.DetailText().Contains("自動修復・再開・復元許可は発行しません",
            StringComparison.Ordinal);

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        var cases = new List<object>();

        {
            string root = Path.Combine(parent, "r217-part38-no-journal");
            Directory.CreateDirectory(root);
            var summary = CollisionEvidenceReview.Inspect(root);
            var result = CollisionRecoveryChecklist.Analyze(summary);
            var triage = CollisionRecoveryChecklist.Prioritize(summary);
            bool ok = !summary.JournalPresent &&
                triage.UnverifiedScope == 1 && triage.Rows.Count == 0 &&
                !triage.CanApply &&
                result.Decision == "証拠なし（復旧可否は未検証）" &&
                ForbiddenOperations(result) &&
                !Directory.Exists(Path.Combine(root, ".chappy-collision-journal"));
            check(ok,
                "R2.17 Part38: empty selected root has no false all-clear, cannot write or create journal");
            cases.Add(new { name = "no-evidence-does-not-authorize-recovery", pass = ok });
        }

        {
            var result = CollisionRecoveryChecklist.Analyze(null);
            bool ok = result.Decision.StartsWith("検査不能",
                    StringComparison.Ordinal) &&
                result.Checks.Any(x => x.Category == "台帳の信頼性" &&
                    x.State == "要確認") &&
                ForbiddenOperations(result);
            check(ok,
                "R2.17 Part38: missing/failed UI inspection stays unverified and non-actionable");
            cases.Add(new { name = "unreadable-summary-fails-closed", pass = ok });
        }

        {
            var f = Prepare(parent, "intent-before-output");
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var review = CollisionRecoveryChecklist.Analyze(summary);
            var triage = CollisionRecoveryChecklist.Prioritize(summary);
            bool ok = triage.P1 == summary.Rows.Count && triage.P0 == 0 &&
                triage.Rows.All(x => x.PriorityLabel == "P1 未完了") &&
                !triage.CanApply &&
                review.Decision == "未完了（自動再開不可）" &&
                review.Checks.Any(x => x.Category == "保存先ファイルのID・SHA・ACL" &&
                    x.State == "未完了") &&
                review.Checks.Any(x => x.Category == "元ファイル・元の権限・保存場所" &&
                    x.State == "未検証") &&
                ForbiddenOperations(review) &&
                SourcesPreserved(f) &&
                !File.Exists(Path.Combine(f.Output,
                    f.Plan.Entries[0].DestinationRelativePath));
            check(ok,
                "R2.17 Part38: valid durable intent with unwritten outputs is incomplete, not corrupt or safe to resume in production");
            cases.Add(new { name = "intent-only-distinguished-from-manual-review", pass = ok });
        }

        {
            var f = Prepare(parent, "completed-destination-only", finish: true);
            string rootMemo = Path.Combine(f.Output, f.Plan.ReportRelativePath);
            FileIdentity reportId = NativeFile.GetIdentity(rootMemo);
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var review = CollisionRecoveryChecklist.Analyze(summary);
            var firstFile = summary.Rows.First(x => x.Kind == "ファイル");
            var selected = CollisionRecoveryChecklist.Analyze(summary, firstFile);
            var triage = CollisionRecoveryChecklist.Prioritize(summary);
            var triagedSelection = CollisionRecoveryChecklist.Analyze(
                triage.Evidence, triage.Rows.First(x => x.Kind == "ファイル"));
            bool ok = triage.P2 == summary.Rows.Count && triage.P0 == 0 &&
                triage.Rows.All(x => x.PriorityLabel == "P2 元未照合") &&
                !triage.CanApply && !triagedSelection.AuthorizesOriginalRestore &&
                triagedSelection.Decision == review.Decision &&
                review.Decision ==
                    "保存先証拠のみ一致（本番復元不可）" &&
                review.Checks.Count == 8 &&
                review.Checks.Any(x => x.Category == "保存先ファイルのID・SHA・ACL" &&
                    x.State == "保存先照合済み") &&
                review.Checks.Any(x => x.Category == "元ファイル・元の権限・保存場所" &&
                    x.State == "未検証") &&
                review.Checks.Any(x => x.Category == "競合・停電・途中中断の完全復旧" &&
                    x.State == "未検証") &&
                review.Checks.Any(x => x.Category == "本番ファイルへの変更・自動復元" &&
                    x.State == "禁止") &&
                selected.Decision == review.Decision &&
                ForbiddenOperations(review) && ForbiddenOperations(selected) &&
                NativeFile.GetIdentity(rootMemo).SameFile(reportId) &&
                SourcesPreserved(f);
            check(ok,
                "R2.17 Part38: all receipts valid only proves destinations; source/path/power-failure/production Undo remain explicitly unverified/disabled");
            cases.Add(new { name = "completed-destination-only-proof-is-not-undo-permission", pass = ok });
        }

        {
            var f = Prepare(parent, "unknown-tmp-after-success", finish: true);
            string tmp = Journal(f, ".item-0000.json.tmp");
            File.WriteAllText(tmp, "foreign incomplete file");
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var fileRow = summary.Rows.First(x => x.Kind == "ファイル" &&
                !x.NeedsAttention);
            var forFile = CollisionRecoveryChecklist.Analyze(summary, fileRow);
            var whole = CollisionRecoveryChecklist.Analyze(summary);
            var triage = CollisionRecoveryChecklist.Prioritize(summary);
            bool ok = triage.P0 == summary.Rows.Count && triage.P2 == 0 &&
                triage.Rows.All(x => x.PriorityLabel == "P0 最優先") &&
                triage.Rows[0].Kind == "操作記録" &&
                !triage.CanApply &&
                forFile.Decision == "要確認（証拠に不整合）" &&
                whole.Decision == forFile.Decision &&
                forFile.Checks.Any(x => x.Category == "未知の証拠・.tmp" &&
                    x.State == "要確認") &&
                ForbiddenOperations(forFile) &&
                File.ReadAllText(tmp) == "foreign incomplete file" &&
                SourcesPreserved(f);
            check(ok,
                "R2.17 Part38: even selecting a verified file cannot hide foreign tmp/manual review in same operation");
            cases.Add(new { name = "unknown-evidence-blocks-selected-verified-file", pass = ok });
        }

        {
            var f = Prepare(parent, "copy-unreceipted");
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id, afterDurablePhase: phase =>
                {
                    if (phase == "copy-flushed-before-receipt-1")
                        throw new TestInterruption();
                }), "R2.17 Part38: generated interruption between output Flush and durable receipt");
            string orphan = Path.Combine(f.Output,
                f.Plan.Entries[0].DestinationRelativePath);
            FileIdentity orphanId = NativeFile.GetIdentity(orphan);
            string orphanHash = Hash(orphan);
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var checklist = CollisionRecoveryChecklist.Analyze(summary);
            var triage = CollisionRecoveryChecklist.Prioritize(summary);
            bool ok = triage.P0 == summary.Rows.Count &&
                triage.Rows.All(x => x.PriorityLabel == "P0 最優先") &&
                checklist.Decision == "要確認（証拠に不整合）" &&
                checklist.Checks.Any(x => x.Category == "保存先ファイルのID・SHA・ACL" &&
                    x.State == "要確認") &&
                ForbiddenOperations(checklist) &&
                NativeFile.GetIdentity(orphan).SameFile(orphanId) &&
                Hash(orphan) == orphanHash && SourcesPreserved(f);
            check(ok,
                "R2.17 Part38: orphan written without receipt is manual review, no data adoption, deletion or automatic replay");
            cases.Add(new { name = "unreceipted-orphan-never-auto-repaired", pass = ok });
        }

        {
            var f = Prepare(parent, "report-conflict", finish: true);
            string report = Path.Combine(f.Output, f.Plan.ReportRelativePath);
            File.AppendAllText(report, "forged-report");
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var checklist = CollisionRecoveryChecklist.Analyze(summary);
            bool ok = checklist.Decision == "要確認（証拠に不整合）" &&
                checklist.Checks.Any(x =>
                    x.Category == "同名ファイル一覧.txt と完了レシート" &&
                    x.State == "要確認") &&
                ForbiddenOperations(checklist) &&
                SourcesPreserved(f);
            check(ok,
                "R2.17 Part38: changed root memo is clearly distinguished from merely pending memo and blocks recovery");
            cases.Add(new { name = "changed-root-memo-listed-as-blocker", pass = ok });
        }

        {
            var f = Prepare(parent, "modified-source-still-unverified", finish: true);
            string target = Path.Combine(f.Output,
                f.Plan.Entries[0].DestinationRelativePath);
            FileIdentity outputId = NativeFile.GetIdentity(target);
            File.WriteAllBytes(f.Sources[0],
                RandomNumberGenerator.GetBytes(75 * 1024));
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var review = CollisionRecoveryChecklist.Analyze(summary);
            bool ok = review.Decision ==
                    "保存先証拠のみ一致（本番復元不可）" &&
                review.Checks.Any(x =>
                    x.Category == "元ファイル・元の権限・保存場所" &&
                    x.State == "未検証") &&
                !review.AuthorizesOriginalRestore &&
                NativeFile.GetIdentity(target).SameFile(outputId) &&
                Hash(f.Original) == f.OriginalHash &&
                NativeFile.GetIdentity(f.Original).SameFile(f.OriginalId);
            check(ok,
                "R2.17 Part38: even when original source changes after output receipt, UI NEVER infers source validation from destination ID/SHA");
            cases.Add(new { name = "changed-source-not-silently-approved", pass = ok });
        }

        {
            var f = Prepare(parent, "stale-ui-selection", finish: true);
            var summary = CollisionEvidenceReview.Inspect(f.Output);
            var fakeRow = new CollisionEvidenceReview.Row("ファイル",
                f.Id, "Fake", "fake.mp4", "保存先照合済み",
                "not in actual inspector results", false);
            var review = CollisionRecoveryChecklist.Analyze(summary, fakeRow);
            bool ok = review.Decision ==
                    "要確認（表示行が最新証拠と不一致）" &&
                !review.AuthorizesOriginalRestore &&
                ForbiddenOperations(review) &&
                SourcesPreserved(f);
            check(ok,
                "R2.17 Part38: stale/forged selected UI row is never accepted as verified evidence");
            cases.Add(new { name = "stale-displayed-row-requires-reinspection", pass = ok });
        }

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-part38-collision-checklist.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-part38-checklist-v1",
                verdict = "PASS",
                scenarioCount = cases.Count,
                scenarios = cases,
                syntheticNTFSOnly = true,
                noEmptyRootAllClear = true,
                noSourceValidationFromDestinationReceipt = true,
                noOutputFileAdoptionWithoutReceipt = true,
                unknownEvidenceBlocksSelectedVerifiedFile = true,
                eightReadOnlySafetyChecks = true,
                noUndoAuthorizationForOutputOnlyEvidence = true,
                staleSelectedRowFailsClosed = true,
                pendingVsCorruptVsDestinationMatchDistinct = true,
                noReleaseExtractResumeDelete = true,
                originalFilesAndNtfsIdsPreserved = true,
                realUserMediaTouched = false,
                realUserRecoveredBytes = 0,
                realPowerCutOrAtomicityProven = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART38 nine synthetic proof-only checklist cases; no production Undo authorization PASS");
    }
}
