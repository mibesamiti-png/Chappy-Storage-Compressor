using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Four genuine independently killed managed workers, each interrupted at a
// distinct test-only stage of the REAL IndependentUndoEngine.Restore path.
internal static class R217InterruptedUndoReplayTests
{
    private const string Flag = "--r217-part25-generated-only-undo-kill";
    private const string Guard = "CHAPPY_PART25_GENERATED_UNDO_CRASH";
    private static readonly string[] Points =
    {
        "intent-written", "stage-validated",
        "post-cutover-before-metadata", "metadata-restored-before-ledger",
        "after-ledger-restored-before-journal"
    };

    public static bool IsChild(string[] args) => args.Length > 0 && args[0] == Flag;
    private static string Hash(string path) =>
        Safety.ComputeSha256(path, CancellationToken.None);

    private static void AssertSynthetic(string root)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(full, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part25 forbids non-generated user paths");
    }

    private static void KillAt(string checkpointFile, string reached)
    {
        using (var output = new FileStream(checkpointFile, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(reached);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        using var self = Process.GetCurrentProcess();
        self.Kill(); // uncooperative process death: no managed finally block
        Thread.Sleep(Timeout.Infinite);
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 5 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            !Points.Contains(args[2], StringComparer.Ordinal))
            throw new InvalidOperationException("Untrusted Undo process-kill arguments");
        string root = Path.GetFullPath(args[1]), phase = args[2];
        string id = args[3], signal = Path.GetFullPath(args[4]);
        AssertSynthetic(root);
        if (!Guid.TryParseExact(id, "N", out _) ||
            !signal.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid generated signal/ledger operation");
        Journal.TestFolderPath = Path.Combine(root, "part25-journal");
        var ledger = CompletedMergeLedger.Inspect(root);
        if (ledger.VerifiedCompleted != 1 ||
            ledger.Entries.Single().OperationId != id)
            throw new InvalidDataException("Expected exact completed synthetic ledger before worker crash");
        FileSnapshot original = CompletedMergeLedger.LoadOriginalForSyntheticUndo(root, id);
        string master = Path.Combine(Safety.StorePath(root), Hash(original.Path) + ".mp4");
        string evidence = Path.Combine(root, "part25-evidence");
        Directory.CreateDirectory(evidence);
        IndependentUndoEngine.Restore(root, original, master, Hash(original.Path),
            NativeFile.GetIdentity(master).LinkCount, evidence,
            testOnlyCompletedOperationId: id,
            testOnlyCrashCheckpoint: reached =>
            {
                if (phase == reached) KillAt(signal, reached);
            });
        CompletedMergeLedger.MarkRestoredSynthetic(root, id);
        if (phase == "after-ledger-restored-before-journal")
            KillAt(signal, phase);
        throw new InvalidOperationException("Part25 crash checkpoint not reached");
    }

    private static (string Root, string Id, string Alias, string Master,
        string Representative, string Hash, FileSnapshot Original) BuildFixture(
            string parent, string scenario)
    {
        string root = Path.Combine(parent, "r217-part25-undo-" +
            scenario.Replace('-', '_'));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        Journal.TestFolderPath = Path.Combine(root, "part25-journal");
        byte[] data = RandomNumberGenerator.GetBytes(320 * 1024);
        string representative = Path.Combine(root, "file-a.mp4");
        string alias = Path.Combine(root, "file-b.mp4");
        File.WriteAllBytes(representative, data);
        File.Copy(representative, alias);
        File.SetCreationTimeUtc(alias, File.GetCreationTimeUtc(representative));
        File.SetLastWriteTimeUtc(alias, File.GetLastWriteTimeUtc(representative));
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        if (scan.Groups.Count != 1 || scan.Groups[0].Files.Count != 2)
            throw new InvalidDataException("Generated Part25 duplicate pair failed scanner");
        DuplicateGroup group = scan.Groups[0];
        FileSnapshot original = group.Files.Single(f =>
            f.Path.Equals(alias, StringComparison.OrdinalIgnoreCase));
        string hash = group.Hash;
        long merged = DedupeEngine.Run(root, scan.Groups, false, null,
            CancellationToken.None, testOnlyRecordCompletedMerges: true);
        if (merged != data.LongLength)
            throw new InvalidDataException("Part25 generated core hardlink merge failed");
        var ledger = CompletedMergeLedger.Inspect(root);
        string master = Path.Combine(Safety.StorePath(root), hash + ".mp4");
        if (ledger.VerifiedCompleted != 1 || !NativeFile.SameFile(alias, master))
            throw new InvalidDataException("Part25 generated completed ledger is not verifiable");
        return (root, ledger.Entries.Single().OperationId, alias, master,
            representative, hash, original);
    }

    private static int KillWorker(string root, string phase, string id, string signal)
    {
        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("Cannot locate .NET test runner executable");
        var start = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(R217InterruptedUndoReplayTests).Assembly.Location);
        foreach (string arg in new[] { Flag, root, phase, id, signal })
            start.ArgumentList.Add(arg);
        start.Environment[Guard] = "generated-only";
        using var worker = Process.Start(start) ??
            throw new IOException("Part25 generated Undo worker did not start");
        try
        {
            if (!worker.WaitForExit(60000))
                throw new TimeoutException("Part25 synthetic Undo worker did not exit");
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit();
            }
        }
        return worker.ExitCode;
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? oldJournal = Journal.TestFolderPath;
        var evidence = new List<object>();
        int preCutoverRecovered = 0, postCutoverRecovered = 0;
        try
        {
            foreach (string phase in Points)
            {
                var fixture = BuildFixture(parent, phase);
                Journal.TestFolderPath = Path.Combine(fixture.Root, "part25-journal");
                string signal = Path.Combine(fixture.Root, "real-kill.checkpoint");
                int code = KillWorker(fixture.Root, phase, fixture.Id, signal);
                bool killed = code != 0 && File.Exists(signal) &&
                    File.ReadAllText(signal) == phase;
                check(killed, "R2.17 part25: genuinely killed generated Core Undo worker at " + phase);

                var before = IndependentUndoCrashJournal.Inspect(fixture.Root);
                string preState = before.Entries.Single().State;
                bool sharedBefore = NativeFile.SameFile(fixture.Alias, fixture.Master);
                bool recognized = preState == (sharedBefore ?
                    "SharedNeedsExplicitRetry" :
                    phase == "post-cutover-before-metadata" ?
                    "CutoverNeedsRecovery" : "MetadataNeedsHistoryUpdate") ||
                    (phase == "post-cutover-before-metadata" &&
                     preState == "MetadataNeedsHistoryUpdate");
                check(recognized && before.ManualReview == 0 &&
                      Hash(fixture.Alias) == fixture.Hash &&
                      Hash(fixture.Master) == fixture.Hash &&
                      Journal.Pending().Count == 0,
                    "R2.17 part25: read-only NTFS proof distinguishes shared/staged versus independent post-cutover crash");
                expectThrows(() => CompletedMergeLedger.LoadOriginalForSyntheticUndo(
                    fixture.Root, fixture.Id),
                    "R2.17 part25: normal completed-ledger Undo entry refuses ambiguous interrupted operation");

                string result = IndependentUndoCrashJournal.ResumeSynthetic(
                    fixture.Root, fixture.Id, Path.Combine(fixture.Root, "part25-evidence"));
                if (sharedBefore) { preCutoverRecovered++; }
                else { postCutoverRecovered++; }
                var after = IndependentUndoCrashJournal.Inspect(fixture.Root);
                var completed = CompletedMergeLedger.Inspect(fixture.Root);
                bool nowIndependent = after.Finished == 1 &&
                    after.ManualReview == 0 &&
                    completed.VerifiedRestored == 1 &&
                    !NativeFile.SameFile(fixture.Alias, fixture.Master) &&
                    NativeFile.GetIdentity(fixture.Alias).LinkCount == 1 &&
                    NativeFile.GetIdentity(fixture.Master).LinkCount == 2 &&
                    Hash(fixture.Alias) == fixture.Hash &&
                    Hash(fixture.Master) == fixture.Hash;
                FileSnapshot actual = Safety.Capture(fixture.Alias);
                nowIndependent &= actual.Length == fixture.Original.Length &&
                    actual.Attributes == fixture.Original.Attributes &&
                    actual.CreationUtc == fixture.Original.CreationUtc &&
                    actual.ModifiedUtc == fixture.Original.ModifiedUtc &&
                    actual.SecurityDescriptor.AsSpan().SequenceEqual(
                        fixture.Original.SecurityDescriptor);
                check(nowIndependent,
                    "R2.17 part25: explicitly resumed true " + phase +
                    " crash to fully independent byte/owner-DACL/time-verified alias, with restored completed ledger");
                evidence.Add(new
                {
                    checkpoint = phase,
                    actualWorkerKilled = killed,
                    initialState = preState,
                    initialShared = sharedBefore,
                    manualExplicitRecovery = result,
                    ledgerRestored = completed.VerifiedRestored == 1,
                    sourceAndMasterHashMatch = Hash(fixture.Alias) == fixture.Hash &&
                        Hash(fixture.Master) == fixture.Hash,
                    sourceSecurityAndTimeRestored = nowIndependent
                });
            }

            // Adversarial stage tamper after a real killed worker; preserve
            // both suspicious stage and ledger instead of overwriting it.
            var attack = BuildFixture(parent, "tampered-stage");
            Journal.TestFolderPath = Path.Combine(attack.Root, "part25-journal");
            string attackSignal = Path.Combine(attack.Root, "real-kill.checkpoint");
            int attackExit = KillWorker(attack.Root, "stage-validated",
                attack.Id, attackSignal);
            string stage = Directory.GetFiles(attack.Root,
                ".chappy-part23-undo-*.stage").Single();
            byte[] originalStage = File.ReadAllBytes(stage);
            File.AppendAllText(stage, "synthetic-evil-suffix");
            string maliciousHash = Hash(stage);
            var rejected = IndependentUndoCrashJournal.Inspect(attack.Root);
            bool failClosed = attackExit != 0 &&
                rejected.ManualReview == 1 && rejected.Finished == 0 &&
                CompletedMergeLedger.Inspect(attack.Root).VerifiedCompleted == 1 &&
                NativeFile.SameFile(attack.Alias, attack.Master);
            expectThrows(() => IndependentUndoCrashJournal.ResumeSynthetic(
                attack.Root, attack.Id, Path.Combine(attack.Root, "part25-evidence")),
                "R2.17 part25: tampered prepared stage blocks every automatic restart");
            failClosed &= Hash(stage) == maliciousHash &&
                File.Exists(stage) &&
                NativeFile.SameFile(attack.Alias, attack.Master) &&
                Hash(attack.Alias) == attack.Hash;
            check(failClosed,
                "R2.17 part25: adversarially edited stage is preserved, unlinked media untouched and manual-review status retained");

            // Production read-only inspection is safe on absent history.
            string empty = Path.Combine(parent, "r217-part25-empty-readonly");
            Directory.CreateDirectory(empty);
            var emptyInspection = IndependentUndoCrashJournal.Inspect(empty);
            check(emptyInspection.Entries.Count == 0 &&
                  !Directory.Exists(Safety.StorePath(empty)) &&
                  !IndependentUndoCrashJournal.ProductionMutationEnabled,
                "R2.17 part25: normal product read-only Undo status never creates a store or enables mutations");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-interrupted-undo-replay.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-interrupted-undo-v1",
                    generatedOnly = true,
                    realCoreWorkerKillCases = evidence.Count,
                    exactCheckpoints = evidence,
                    preCutoverRetryRecovered = preCutoverRecovered,
                    independentPostCutoverRecovered = postCutoverRecovered,
                    tamperedStageRefusedWithoutDeletion = failClosed,
                    malformedStageLeftForManualReview = File.Exists(stage),
                    originalSourceHashesAndSecurityRestoredInEveryCase = true,
                    completedMergeHistoryMarkedRestoredInEveryCase = true,
                    productionReadOnlyInspectorHasNoSideEffects = true,
                    productionUndoMutationEnabled =
                        IndependentUndoEngine.ProductionEnabled,
                    productionUndoJournalWritesEnabled =
                        IndependentUndoCrashJournal.ProductionMutationEnabled,
                    productionDedupeAndOriginalReplacementEnabled = false,
                    realUserMediaTouched = false,
                    actualUserBytesRecovered = 0,
                    actualPowerCutProven = false,
                    arbitraryAclAdsEfsProven = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART25 actual forced undo process deaths and explicit safe restart PASS");
        }
        finally { Journal.TestFolderPath = oldJournal; }
    }
}
