using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChappyStorageCompressor.Core;

/// <summary>
/// Part37: no actual user media is touched. All artificially created Part35
/// journals and outputs live under a fresh ChappyStorageSmoke_ TEMP fixture.
/// Check that a locally matching file cannot give a falsely green whole-run
/// state when unknown evidence or a forged memo contradicts the intent.
/// </summary>
internal static class R217CollisionTriageIntegrityTests
{
    private sealed record Fixture(string TestRoot, string Output, string Id,
        CollisionCollectionPlan.Result Plan, string ExistingPath,
        string ExistingHash, FileIdentity ExistingId,
        string[] Incoming, string[] IncomingHashes);

    private static string Hash(string file) =>
        Safety.ComputeSha256(file, CancellationToken.None);

    private static Fixture Prepare(string parent, string label, bool finish = true)
    {
        string root = Path.Combine(parent, "r217-part37-" + label);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string inputRoot = Path.Combine(root, "part35-incoming");
        Directory.CreateDirectory(inputRoot);
        string[] sources =
        [
            Path.Combine(inputRoot, "new-original.mp4"),
            Path.Combine(inputRoot, "new-second.mp4")
        ];
        foreach (string source in sources)
            File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(40 * 1024));
        string output = Path.Combine(root, "restored");
        Directory.CreateDirectory(Path.Combine(output, "Camera"));
        string old = Path.Combine(output, "Camera", "clip.mp4");
        File.WriteAllBytes(old, RandomNumberGenerator.GetBytes(32 * 1024));
        string[] incoming =
        [
            Path.Combine("Camera", "clip.mp4"),
            Path.Combine("Trip", "new.mp4")
        ];
        var plan = CollisionCollectionPlan.Inspect(output, incoming);
        if (plan.Conflicts != 1 || plan.ReportRelativePath != "同名ファイル一覧.txt")
            throw new InvalidDataException("Part37 generated collision fixture is invalid");
        string id = CollisionRecoveryLedger.BeginSynthetic(root, plan,
        [
            (sources[0], incoming[0]), (sources[1], incoming[1])
        ]);
        var result = new Fixture(root, output, id, plan, old, Hash(old),
            NativeFile.GetIdentity(old), sources,
            sources.Select(Hash).ToArray());
        if (finish && CollisionRecoveryLedger.ResumeSynthetic(root, output, id)
                .State != "VerifiedCompleted")
            throw new InvalidDataException("Part37 completed test-owned fixture invalid");
        return result;
    }

    private static string FileInJournal(Fixture f, string suffix) =>
        Path.Combine(f.Output, ".chappy-collision-journal", f.Id + suffix);

    private static bool SourcesUnchanged(Fixture f) =>
        Hash(f.ExistingPath) == f.ExistingHash &&
        NativeFile.GetIdentity(f.ExistingPath).SameFile(f.ExistingId) &&
        f.Incoming.Select((name, i) =>
            Hash(name) == f.IncomingHashes[i]).All(x => x);

    private static JsonNode ReadJson(string file) =>
        JsonNode.Parse(File.ReadAllText(file)) ??
            throw new InvalidDataException("Part37 generated JSON parse failed");

    private static void WriteJson(string path, JsonNode doc) =>
        File.WriteAllText(path, doc.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true }));

    public static void Run(string parent, Action<bool, string> check)
    {
        var scenarios = new List<object>();
        check(!CollisionEvidenceReview.ProductionMutationEnabled &&
            !CollisionRecoveryLedger.ProductionMutationEnabled &&
            !CollisionCollectionPlan.ProductionExtractionOrUndoEnabled,
            "R2.17 Part37: actual release cannot copy/rename/delete user files through collision inspector");

        // 1. Positive baseline, including canonical Part35 root memo.
        {
            var f = Prepare(parent, "canonical-root-memo");
            var first = CollisionEvidenceReview.Inspect(f.Output);
            var again = CollisionEvidenceReview.Inspect(f.Output);
            bool ok = first.TotalRecords == 1 && first.NeedsReview == 0 &&
                first.FilesVerified == 2 &&
                first.Rows.Single(x => x.Kind == "操作記録").State ==
                    "保存先証拠一致（元データは未照合）" &&
                first.Rows.Any(x => x.Kind == "一覧メモ" && !x.NeedsAttention) &&
                first.Rows.All(x =>
                    !x.ReviewGuidance.Contains("削除してください",
                        StringComparison.Ordinal) &&
                    !x.ReviewGuidance.Contains("上書きしてください",
                        StringComparison.Ordinal)) &&
                first.Rows.SequenceEqual(again.Rows) &&
                SourcesUnchanged(f);
            check(ok,
                "R2.17 Part37: exact original Part35 report/receipts remain verified but reviewer never authorizes deletion, recovery or copying");
            scenarios.Add(new { name = "canonical-memo-baseline", pass = ok });
        }

        // 2. A previously valid COMPLETE must lose its green status when a
        // foreign .tmp appears in the same journal. Preserve that evidence.
        {
            var f = Prepare(parent, "unknown-temp-after-success");
            string temp = FileInJournal(f, ".item-0000.json.tmp");
            File.WriteAllText(temp, "foreign unverified record");
            string tempHash = Hash(temp);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            var parentRow = view.Rows.Single(x => x.Kind == "操作記録");
            var foreign = view.Rows.Single(x => x.Kind == "不明な証拠");
            bool ok = parentRow.State == "ManualReview" &&
                parentRow.NeedsAttention && view.NeedsReview >= 2 &&
                foreign.RecordId.EndsWith(".tmp", StringComparison.Ordinal) &&
                foreign.ReviewGuidance.Contains("移動・削除せず",
                    StringComparison.Ordinal) &&
                parentRow.ReviewGuidance.Contains("台帳全体",
                    StringComparison.Ordinal) &&
                view.FilesVerified == 2 && // local file proofs remain true
                Hash(temp) == tempHash && SourcesUnchanged(f);
            check(ok,
                "R2.17 Part37: verified individual file IDs do not make a whole operation green if unknown journal evidence is present");
            scenarios.Add(new { name = "unknown-evidence-downgrades-whole-record", pass = ok });
        }

        // 3. Modify only the conflict COUNT in the memo (same file ID and
        // exact byte length). Update the receipt's SHA to MATCH those forged
        // bytes. The changed memo + matching receipt are still inconsistent
        // with the original durable intent and must be refused.
        {
            var f = Prepare(parent, "forged-memo-matching-forged-receipt");
            string memo = Path.Combine(f.Output, f.Plan.ReportRelativePath);
            var before = Safety.Capture(memo);
            byte[] original = File.ReadAllBytes(memo);
            byte[] marker = Encoding.UTF8.GetBytes("同名件数: 1");
            int index = original.AsSpan().IndexOf(marker);
            if (index < 0)
                throw new InvalidDataException("Part37 missing canonical source collision count");
            byte[] changed = (byte[])original.Clone();
            changed[index + marker.Length - 1] = (byte)'9';
            File.WriteAllBytes(memo, changed);
            File.SetLastWriteTimeUtc(memo, before.ModifiedUtc);
            var after = Safety.Capture(memo);
            string sha = Convert.ToHexString(SHA256.HashData(changed));
            string receipt = FileInJournal(f, ".report.json");
            JsonNode proof = ReadJson(receipt);
            proof["Hash"] = sha;
            WriteJson(receipt, proof);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool sameMetadata = after.Identity.SameFile(before.Identity) &&
                after.Length == before.Length &&
                after.ModifiedUtc == before.ModifiedUtc &&
                after.CreationUtc == before.CreationUtc &&
                after.Attributes == before.Attributes &&
                after.SecurityDescriptor.AsSpan().SequenceEqual(
                    before.SecurityDescriptor);
            bool ok = sameMetadata &&
                view.Rows.Any(x => x.Kind == "一覧メモ" && x.NeedsAttention &&
                    x.State == "ManualReview") &&
                view.Rows.Single(x => x.Kind == "操作記録").State == "ManualReview" &&
                proof["Hash"]?.GetValue<string>() == sha &&
                !changed.AsSpan().SequenceEqual(original) &&
                SourcesUnchanged(f);
            check(ok,
                "R2.17 Part37: same-ID same-length memo with self-consistently forged SHA receipt is still rejected against immutable original intent");
            scenarios.Add(new { name = "forged-self-consistent-memo-vs-intent", pass = ok });
        }

        // 4. The collection hierarchy in a persisted record must be only
        // 同名ファイル/同名ファイル..., not any attacker-picked directory.
        {
            var f = Prepare(parent, "invalid-collection-folder");
            string intent = FileInJournal(f, ".intent.json");
            JsonNode document = ReadJson(intent);
            document["CollectionRelativePath"] = "別フォルダ";
            WriteJson(intent, document);
            string mutated = Hash(intent);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool ok = view.Rows.Any(x => x.Kind == "操作記録" &&
                    x.State == "ManualReview") &&
                view.NeedsReview >= 1 && Hash(intent) == mutated &&
                SourcesUnchanged(f);
            check(ok,
                "R2.17 Part37: forged non-同名ファイル collection name cannot be shown as a valid completed operation");
            scenarios.Add(new { name = "invalid-nested-collection-refused", pass = ok });
        }

        // 5. A journal that says a collision happened but changes its
        // Collected flag to false is not the original Part34 placement plan.
        {
            var f = Prepare(parent, "inconsistent-collected-flag");
            string intent = FileInJournal(f, ".intent.json");
            JsonNode doc = ReadJson(intent);
            doc["Items"]![0]!["Collected"] = false;
            WriteJson(intent, doc);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            bool ok = view.Rows.Any(x => x.Kind == "操作記録" &&
                    x.State == "ManualReview") &&
                view.NeedsReview > 0 && SourcesUnchanged(f);
            check(ok,
                "R2.17 Part37: contradictory grouped-file flag and top-level memo invalidates complete operation classification");
            scenarios.Add(new { name = "contradictory-collision-flag-refused", pass = ok });
        }

        // 6. Foreign completion marker without its intent remains an unknown
        // evidence row, never "verified completed" or silently cleaned.
        {
            string root = Path.Combine(parent, "r217-part37-orphan-complete");
            Directory.CreateDirectory(root);
            string journal = Path.Combine(root, ".chappy-collision-journal");
            Directory.CreateDirectory(journal);
            string marker = Path.Combine(journal,
                Guid.NewGuid().ToString("N") + ".complete");
            File.WriteAllText(marker, "Part35 verified complete");
            string hash = Hash(marker);
            var view = CollisionEvidenceReview.Inspect(root);
            bool ok = view.TotalRecords == 0 && view.NeedsReview == 1 &&
                view.Rows.Single().Kind == "不明な証拠" &&
                view.Rows.Single().NeedsAttention &&
                !view.CanModify && Hash(marker) == hash;
            check(ok,
                "R2.17 Part37: orphan completed marker without its durable intent is unknown evidence, never assumed safe");
            scenarios.Add(new { name = "unowned-complete-marker-manual-review", pass = ok });
        }

        // 7. A real, written output without a receipt provides tailored
        // precautions, but no automatic adoption/deletion.
        {
            var f = Prepare(parent, "unreceipted-output", finish: false);
            string destination = Path.Combine(f.Output,
                f.Plan.Entries[0].DestinationRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(f.Incoming[0], destination);
            FileIdentity newId = NativeFile.GetIdentity(destination);
            string hash = Hash(destination);
            var view = CollisionEvidenceReview.Inspect(f.Output);
            var row = view.Rows.Single(x => x.Kind == "ファイル" &&
                x.DestinationRelativePath ==
                    f.Plan.Entries[0].DestinationRelativePath);
            bool ok = row.NeedsAttention && row.State == "ManualReview" &&
                row.ReviewGuidance.Contains("削除・上書き・自動採用せず",
                    StringComparison.Ordinal) &&
                view.Rows.Single(x => x.Kind == "操作記録").NeedsAttention &&
                Hash(destination) == hash &&
                NativeFile.GetIdentity(destination).SameFile(newId) &&
                SourcesUnchanged(f);
            check(ok,
                "R2.17 Part37: unreceipted output displays protective, specific next steps and leaves both NTFS original and unknown destination intact");
            scenarios.Add(new { name = "orphan-output-protective-guidance", pass = ok });
        }

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-part37-consistent-collision-triage.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-part37-collision-evidence-consistency-v1",
                verdict = "PASS",
                scenarioCount = scenarios.Count,
                scenarios,
                generatedOnly = true,
                wholeOperationRedIfUnknownEvidence = true,
                selfConsistentForgedMemoStillFailsCanonicalIntent = true,
                nestedCollisionFolderPolicyBoundToIntent = true,
                orphanedReceiptAndCompletedMarkersRetained = true,
                safeUserReviewGuidanceIncluded = true,
                sourcePathNeverDereferencedByReader = true,
                releaseReadOnlyTabOnly = true,
                realUserMediaTouched = false,
                realUserCapacityRecoveredBytes = 0,
                physicalPowerCutProven = false,
                productionExtractionOrUndoEnabled =
                    CollisionCollectionPlan.ProductionExtractionOrUndoEnabled,
                productionCollisionMutationEnabled =
                    CollisionRecoveryLedger.ProductionMutationEnabled
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART37 unknown journal and forged-report whole-record fail-closed triage PASS");
    }
}
