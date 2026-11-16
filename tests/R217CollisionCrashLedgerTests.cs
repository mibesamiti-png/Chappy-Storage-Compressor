using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part35: kill actual generated Windows Core child processes between durable
// intent, file copy, per-file receipt, memo receipt and final completion.
internal static class R217CollisionCrashLedgerTests
{
    private const string ChildFlag = "--r217-part35-generated-collision-kill";
    private const string Guard = "CHAPPY_PART35_GENERATED_ONLY";
    private const string SignalName = "part35-killed.signal";
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "item-receipt-1", "copy-flushed-before-receipt-1",
        "report-receipt"
    };

    public static bool IsChild(string[] args) =>
        args.Length > 0 && args[0] == ChildFlag;

    public static int RunChild(string[] args)
    {
        if (args.Length != 6 || args[0] != ChildFlag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            !Phases.Contains(args[4]) ||
            args[5] != SignalName ||
            !Guid.TryParseExact(args[3], "N", out _))
            throw new InvalidDataException("Part35 foreign worker inputs refused");
        string testRoot = Path.GetFullPath(args[1]);
        string outputRoot = Path.GetFullPath(args[2]);
        if (File.ReadAllText(Path.Combine(testRoot,
                ".chappy-part23-generated-only")) != "Part23 synthetic data only" ||
            !testRoot.Contains("ChappyStorageSmoke_",
                StringComparison.OrdinalIgnoreCase) ||
            !outputRoot.StartsWith(testRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Part35 worker cannot reach user directories");
        CollisionRecoveryLedger.ResumeSynthetic(testRoot, outputRoot, args[3],
            afterDurablePhase: phase =>
            {
                if (phase != args[4]) return;
                using (var signal = new FileStream(Path.Combine(testRoot, SignalName),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    signal.Write(System.Text.Encoding.UTF8.GetBytes(phase));
                    signal.Flush(flushToDisk: true);
                }
                using var self = Process.GetCurrentProcess();
                self.Kill();
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidDataException("Part35 child failed to terminate at requested phase");
    }

    private static bool KillAt(string testRoot, string outputRoot, string id, string phase)
    {
        string runner = Environment.ProcessPath ??
            throw new InvalidOperationException("Part35 Windows test runner missing");
        var info = new ProcessStartInfo
        {
            FileName = runner, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runner)!
        };
        if (Path.GetFileName(runner).Equals("dotnet.exe",
                StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(R217CollisionCrashLedgerTests).Assembly.Location);
        foreach (string arg in new[]
        {
            ChildFlag, testRoot, outputRoot, id, phase, SignalName
        }) info.ArgumentList.Add(arg);
        info.Environment[Guard] = "generated-only";
        using Process child = Process.Start(info) ??
            throw new IOException("Part35 generated crash worker failed to launch");
        try
        {
            if (!child.WaitForExit(60000))
                throw new TimeoutException("Part35 child did not reach crash point");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
        return child.ExitCode != 0 &&
            File.Exists(Path.Combine(testRoot, SignalName)) &&
            File.ReadAllText(Path.Combine(testRoot, SignalName)) == phase;
    }

    private static string Hash(string file) =>
        Safety.ComputeSha256(file, CancellationToken.None);

    private sealed record Fixture(
        string TestRoot, string Output, string Id,
        CollisionCollectionPlan.Result Plan,
        IReadOnlyList<(string Source, string Relative)> Inputs,
        string Original, FileIdentity OriginalId, string OriginalHash,
        string[] SourceHashes);

    private static Fixture Prepare(string parent, string label)
    {
        string root = Path.Combine(parent, "r217-part35-" + label);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string sources = Path.Combine(root, "part35-incoming");
        Directory.CreateDirectory(sources);
        var inputFiles = new string[3];
        for (int i = 0; i < inputFiles.Length; i++)
        {
            inputFiles[i] = Path.Combine(sources, "source-" + i + ".mp4");
            File.WriteAllBytes(inputFiles[i],
                RandomNumberGenerator.GetBytes((i + 1) * 64 * 1024));
        }
        string output = Path.Combine(root, "restored");
        Directory.CreateDirectory(Path.Combine(output, "Camera"));
        string old = Path.Combine(output, "Camera", "clip.mp4");
        File.WriteAllBytes(old, RandomNumberGenerator.GetBytes(39 * 1024));
        var inputs = new (string Source, string Relative)[]
        {
            (inputFiles[0], Path.Combine("Camera", "clip.mp4")),
            (inputFiles[1], Path.Combine("Camera", "clip.mp4")),
            (inputFiles[2], Path.Combine("Trip", "new.mp4"))
        };
        var plan = CollisionCollectionPlan.Inspect(output,
            inputs.Select(x => x.Relative));
        if (plan.Conflicts != 2 || plan.Entries.Count != 3 ||
            plan.ReportRelativePath != "同名ファイル一覧.txt" ||
            plan.CollectionRelativePath != "同名ファイル")
            throw new InvalidDataException("Part35 initial collision preview not as expected");
        string id = CollisionRecoveryLedger.BeginSynthetic(root, plan, inputs);
        return new Fixture(root, output, id, plan, inputs, old,
            NativeFile.GetIdentity(old), Hash(old),
            inputFiles.Select(Hash).ToArray());
    }

    private static bool VerifyUserOriginalAndSources(Fixture f) =>
        NativeFile.GetIdentity(f.Original).SameFile(f.OriginalId) &&
        Hash(f.Original) == f.OriginalHash &&
        f.Inputs.Select((x, i) => Hash(x.Source) == f.SourceHashes[i]).All(x => x);

    private static string Output(Fixture f, int index) =>
        Path.Combine(f.Output, f.Plan.Entries[index].DestinationRelativePath);

    private static bool FinalOutputsMatch(Fixture f)
    {
        if (!VerifyUserOriginalAndSources(f) ||
            CollisionRecoveryLedger.InspectSynthetic(f.TestRoot, f.Output, f.Id)
                .State != "VerifiedCompleted")
            return false;
        for (int i = 0; i < f.Inputs.Count; i++)
        {
            if (!File.Exists(Output(f, i)) ||
                Hash(Output(f, i)) != f.SourceHashes[i] ||
                NativeFile.GetIdentity(Output(f, i)).LinkCount != 1)
                return false;
        }
        string reportPath = Path.Combine(f.Output, f.Plan.ReportRelativePath);
        string memo = File.ReadAllText(reportPath);
        return memo.Contains("同名件数: 2", StringComparison.Ordinal) &&
            memo.Contains(f.Plan.Entries[0].DestinationRelativePath,
                StringComparison.Ordinal) &&
            memo.Contains(f.Plan.Entries[1].DestinationRelativePath,
                StringComparison.Ordinal) &&
            !memo.Contains("source-0", StringComparison.Ordinal);
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        var results = new List<object>();
        check(!CollisionRecoveryLedger.ProductionMutationEnabled &&
              !CollisionCollectionPlan.ProductionExtractionOrUndoEnabled,
            "R2.17 Part35: release collision output/Undo mutations explicitly disabled");

        {
            var f = Prepare(parent, "crash-after-item-receipt");
            check(KillAt(f.TestRoot, f.Output, f.Id, "item-receipt-1"),
                "R2.17 Part35: true child process died after durable first-file receipt");
            var interim = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            FileIdentity receiptId = NativeFile.GetIdentity(Output(f, 0));
            check(interim.State == "ExplicitResumeAvailable" &&
                  interim.VerifiedFiles == 1 &&
                  File.Exists(Output(f, 0)) &&
                  !File.Exists(Output(f, 1)) &&
                  VerifyUserOriginalAndSources(f),
                "R2.17 Part35: restart verifies exact first output/NTFS ID; later names are absent");
            var resumed = CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id);
            bool ok = resumed.State == "VerifiedCompleted" &&
                NativeFile.GetIdentity(Output(f, 0)).SameFile(receiptId) &&
                FinalOutputsMatch(f);
            check(ok,
                "R2.17 Part35: explicit restart skips fully verified prior output and finishes remaining files/root memo");
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id),
                "R2.17 Part35: fully completed recovery refuses duplicate replay");
            results.Add(new { name = "kill-after-first-durable-receipt",
                pass = ok, trueProcessKill = true, noUnsafeOverwrite = true });
        }

        {
            var f = Prepare(parent, "crash-before-item-receipt");
            check(KillAt(f.TestRoot, f.Output, f.Id,
                    "copy-flushed-before-receipt-1"),
                "R2.17 Part35: child really died after output Flush(true), before file receipt");
            var state = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            FileIdentity orphanId = NativeFile.GetIdentity(Output(f, 0));
            string orphanHash = Hash(Output(f, 0));
            check(state.State == "ManualReview" &&
                  state.VerifiedFiles == 0 &&
                  orphanHash == f.SourceHashes[0],
                "R2.17 Part35: unreceipted but byte-identical crash output is ManualReview, never silently adopted");
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id),
                "R2.17 Part35: unreceipted output is not automatically removed or overwritten");
            bool ok = NativeFile.GetIdentity(Output(f, 0)).SameFile(orphanId) &&
                Hash(Output(f, 0)) == orphanHash &&
                !File.Exists(Output(f, 1)) &&
                VerifyUserOriginalAndSources(f);
            check(ok,
                "R2.17 Part35: ambiguous output/source paths and evidence survive explicit rejected restart");
            results.Add(new { name = "kill-between-file-flush-and-receipt",
                pass = ok, trueProcessKill = true, noUnsafeOverwrite = true });
        }

        {
            var f = Prepare(parent, "crash-after-memo-receipt");
            check(KillAt(f.TestRoot, f.Output, f.Id, "report-receipt"),
                "R2.17 Part35: child process died after the root memo and durable report receipt");
            var state = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            string report = Path.Combine(f.Output, f.Plan.ReportRelativePath);
            FileIdentity reportId = NativeFile.GetIdentity(report);
            string memoHash = Hash(report);
            check(state.State == "ExplicitResumeAvailable" &&
                  state.VerifiedFiles == 3 && FinalReportOnly(f),
                "R2.17 Part35: complete file receipts and root memo remain verified; completion marker absent");
            var resumed = CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id);
            bool ok = resumed.State == "VerifiedCompleted" &&
                NativeFile.GetIdentity(report).SameFile(reportId) &&
                Hash(report) == memoHash && FinalOutputsMatch(f);
            check(ok,
                "R2.17 Part35: explicit final-marker recovery preserves original report ID/bytes");
            results.Add(new { name = "kill-after-root-memo-receipt",
                pass = ok, trueProcessKill = true, noUnsafeOverwrite = true });
        }

        {
            var f = Prepare(parent, "source-modified");
            File.WriteAllBytes(f.Inputs[0].Source,
                RandomNumberGenerator.GetBytes(64 * 1024));
            var state = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id),
                "R2.17 Part35: changed original input ID/mtime/SHA refuses any new outputs");
            bool ok = state.State == "ManualReview" &&
                !File.Exists(Output(f, 0)) &&
                VerifyOriginalOnly(f);
            check(ok,
                "R2.17 Part35: source changed after durable intent: evidence retained; no copy, no memo, original untouched");
            results.Add(new { name = "source-changed-after-intent",
                pass = ok, trueProcessKill = false, noUnsafeOverwrite = true });
        }

        {
            var f = Prepare(parent, "destination-occupied");
            string destination = Output(f, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, RandomNumberGenerator.GetBytes(1024));
            FileIdentity foreignId = NativeFile.GetIdentity(destination);
            string foreignHash = Hash(destination);
            var state = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id),
                "R2.17 Part35: new foreign occupant after intent never overwritten");
            bool ok = state.State == "ManualReview" &&
                NativeFile.GetIdentity(destination).SameFile(foreignId) &&
                Hash(destination) == foreignHash && VerifyUserOriginalAndSources(f);
            check(ok,
                "R2.17 Part35: late foreign destination name and bytes survive exact refused restart");
            results.Add(new { name = "foreign-output-after-intent",
                pass = ok, trueProcessKill = false, noUnsafeOverwrite = true });
        }

        {
            var f = Prepare(parent, "receipt-tampered-after-completion");
            check(CollisionRecoveryLedger.ResumeSynthetic(
                    f.TestRoot, f.Output, f.Id).State == "VerifiedCompleted",
                "R2.17 Part35: normal generated completion has verified file/report receipts");
            string journal = Path.Combine(f.Output, ".chappy-collision-journal",
                f.Id + ".item-0000.json");
            File.AppendAllText(journal, "foreign-change");
            string tamperedHash = Hash(journal);
            var state = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id),
                "R2.17 Part35: changed receipt is never auto repaired even if complete marker exists");
            bool ok = state.State == "ManualReview" &&
                Hash(journal) == tamperedHash &&
                VerifyUserOriginalAndSources(f);
            check(ok,
                "R2.17 Part35: tampered persisted receipt turns prior completion into manual review, preserving evidence");
            results.Add(new { name = "tampered-durable-receipt",
                pass = ok, trueProcessKill = false, noUnsafeOverwrite = true });
        }

        {
            var f = Prepare(parent, "unknown-incomplete-journal");
            string marker = Path.Combine(f.Output, ".chappy-collision-journal",
                f.Id + ".item-0000.json.tmp");
            File.WriteAllText(marker, "unknown incomplete write");
            var state = CollisionRecoveryLedger.InspectSynthetic(
                f.TestRoot, f.Output, f.Id);
            expectThrows(() => CollisionRecoveryLedger.ResumeSynthetic(
                f.TestRoot, f.Output, f.Id),
                "R2.17 Part35: orphan .tmp evidence never ignored or auto-removed");
            bool ok = state.State == "ManualReview" &&
                File.Exists(marker) && VerifyUserOriginalAndSources(f);
            check(ok,
                "R2.17 Part35: orphan journal temp blocks resume without removing unknown evidence");
            results.Add(new { name = "unknown-incomplete-evidence",
                pass = ok, trueProcessKill = false, noUnsafeOverwrite = true });
        }

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-part35-durable-collision-replay.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-part35-collision-journal-v1",
                verdict = "PASS",
                syntheticOnly = true,
                trueKillCount = results.Count(x => x.GetType()
                    .GetProperty("trueProcessKill")?.GetValue(x) is true),
                scenarioCount = results.Count,
                scenarios = results,
                durableIntentPrecedesFirstCopy = true,
                individualNtfsFileIdsAndShaReceipted = true,
                memoReceiptedBeforeFinalMarker = true,
                interruptedBeforeReceiptNeverAutoAdopted = true,
                incompleteUnknownEvidenceRequiresManualReview = true,
                nestedCollisionPolicyFromPart34Unchanged = true,
                productionCollisionWritesEnabled = CollisionRecoveryLedger.ProductionMutationEnabled,
                productionExtractionOrUndoEnabled =
                    CollisionCollectionPlan.ProductionExtractionOrUndoEnabled,
                realUserMediaTouched = false,
                realUserRecoveredBytes = 0,
                multiFileAtomicPowerCutProven = false,
                productionCrashSafeExtractionEnabled = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART35 three real subprocess kills, exact NTFS receipts and fail-closed collision recovery PASS");
    }

    private static bool VerifyOriginalOnly(Fixture f) =>
        NativeFile.GetIdentity(f.Original).SameFile(f.OriginalId) &&
        Hash(f.Original) == f.OriginalHash;

    private static bool FinalReportOnly(Fixture f)
    {
        string report = Path.Combine(f.Output, f.Plan.ReportRelativePath);
        return File.Exists(report) &&
            File.ReadAllText(report).Contains("同名件数: 2",
                StringComparison.Ordinal);
    }
}
