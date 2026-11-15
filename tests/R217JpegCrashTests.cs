using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part 20: generated-only JPEG process death AFTER a real verified jpegtran
// candidate, BEFORE a durable batch journal record references that candidate.
// A separate test covers force-killing the native jpegtran child in-flight.
internal static class R217JpegCrashTests
{
    private const string Flag = "--r217-part20-generated-only-jpeg-crash";
    private const string Guard = "CHAPPY_R217_PART20_JPEG_CRASH";
    private const string OfficialCommit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";

    public static bool IsCrashChild(string[] args) => args.Length > 0 && args[0] == Flag;

    private static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    public static int RunCrashChild(string[] args)
    {
        if (args.Length != 5 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") != OfficialCommit ||
            Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") != "true")
            throw new InvalidOperationException("Part20 child refused untrusted arguments or tool");

        string? pinnedTool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(pinnedTool) ||
            !Path.GetFullPath(args[3]).Equals(Path.GetFullPath(pinnedTool),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Part20 child requires the exact CI-pinned official jpegtran");

        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        foreach (string path in new[] { args[1], args[2], args[4] })
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Part20 crash child refused non-generated path");
        }

        JpegBatchPreviewEngine.Run(args[1], args[2], args[3],
            testOnlyAfterVerifiedCandidateBeforeJournal: candidate =>
            {
                // The official tool has ALREADY EXITED successfully, and the
                // engine has confirmed markers + RGB pixels. This is a true
                // post-encode / pre-journal process kill, not a JPEG in-flight kill.
                File.WriteAllText(args[4], Path.GetFullPath(candidate));
                using var self = Process.GetCurrentProcess();
                self.Kill();
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidOperationException("Part20 post-verify crash checkpoint missed");
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? tool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(tool))
        {
            Console.WriteLine("R2.17 part20 JPEG crash SKIPPED outside official pinned Windows CI");
            return;
        }
        check(File.Exists(tool) &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == OfficialCommit &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17 part20: native JPEG crash tests require official pinned SIMD jpegtran");

        string input = Path.Combine(parent, "r217-part20-jpeg-input");
        string output = Path.Combine(parent, "r217-part20-jpeg-output");
        string checkpoint = Path.Combine(parent, "r217-part20-jpeg-crash-checkpoint.txt");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        string original = Path.Combine(input, "real-generated.jpg");
        R217PriorityImageAndMemoryTests.WriteJpeg(original, 960, 640, 0);
        string originalHash = Hash(original);
        long originalTicks = File.GetLastWriteTimeUtc(original).Ticks;

        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("Part20 Core test runtime missing");
        var start = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(R217JpegCrashTests).Assembly.Location);
        foreach (string arg in new[] { Flag, input, output, tool, checkpoint })
            start.ArgumentList.Add(arg);
        start.Environment[Guard] = "generated-only";
        using var worker = Process.Start(start) ??
            throw new IOException("Part20 JPEG crash child failed to start");
        try
        {
            if (!worker.WaitForExit(60000))
                throw new TimeoutException("Part20 generated JPEG crash child exceeded 60s");
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit();
            }
        }

        string plan = Path.Combine(output, ".chappy-jpeg-r19.plan.json");
        string journal = Path.Combine(output, ".chappy-jpeg-r19.progress.jsonl");
        string previewFolder = Path.Combine(output, "chappy-jpeg-r19-previews");
        string[] orphans = Directory.Exists(previewFolder) ?
            Directory.GetFiles(previewFolder, "*.jpg") : Array.Empty<string>();
        string savedCheckpoint = File.Exists(checkpoint) ?
            File.ReadAllText(checkpoint).Trim() : "";
        bool crashedAfterRealCandidate = worker.ExitCode != 0 &&
            orphans.Length == 1 && File.Exists(plan) && !File.Exists(journal) &&
            !string.IsNullOrEmpty(savedCheckpoint) &&
            Path.GetFullPath(savedCheckpoint).Equals(
                Path.GetFullPath(orphans[0]), StringComparison.OrdinalIgnoreCase) &&
            new FileInfo(orphans[0]).Length > 0 &&
            Hash(original) == originalHash &&
            File.GetLastWriteTimeUtc(original).Ticks == originalTicks;
        check(crashedAfterRealCandidate,
            "R2.17 part20: genuine source-pinned JPEG candidate created, process killed before journal");

        string orphan = orphans[0];
        string orphanHash = Hash(orphan);
        string planHash = Hash(plan);
        long orphanLength = new FileInfo(orphan).Length;
        bool candidateIndependentlyValidated =
            JpegMarkerAudit.Inspect(original) == JpegMarkerAudit.Inspect(orphan) &&
            JpegLosslessPreviewEngine.EquivalentDecodedPixels(original, orphan) &&
            orphanLength < new FileInfo(original).Length;
        check(candidateIndependentlyValidated,
            "R2.17 part20: unjournaled crash candidate is real optimized JPEG with byte-exact protected markers and RGB pixels");

        bool combinedRefused = false, directRefused = false;
        try { CombinedBatchResumeInspector.Inspect(input, output, tool); }
        catch (InvalidDataException e) when (e.Message.Contains("参照のない候補",
            StringComparison.Ordinal)) { combinedRefused = true; }
        try { JpegBatchPreviewEngine.Run(input, output, tool); }
        catch (InvalidDataException e) when (e.Message.Contains("参照のない候補",
            StringComparison.Ordinal)) { directRefused = true; }
        bool refusedWithoutMutation = combinedRefused && directRefused &&
            Hash(original) == originalHash &&
            File.GetLastWriteTimeUtc(original).Ticks == originalTicks &&
            Hash(orphan) == orphanHash && new FileInfo(orphan).Length == orphanLength &&
            Hash(plan) == planHash && !File.Exists(journal) &&
            Directory.GetFiles(previewFolder, "*.jpg").Length == 1;
        check(refusedWithoutMutation,
            "R2.17 part20: combined and direct JPEG resume refuse real orphan and preserve source, plan, candidate");

        // Only delete our own exact disposable candidate, never any user file.
        File.Delete(orphan);
        var available = CombinedBatchResumeInspector.Inspect(input, output, tool);
        check(available.HasJpegPlan && available.JpegPlanEntries == 1 &&
              !available.HasPngPlan && Hash(plan) == planHash,
            "R2.17 part20: original JPEG plan remains valid after explicitly reviewing synthetic orphan");

        var resume = JpegBatchPreviewEngine.Run(input, output, tool);
        string[] finished = Directory.GetFiles(previewFolder, "*.jpg");
        bool recovered = resume.Total == 1 && resume.Completed == 1 &&
            resume.Failed == 0 && finished.Length == 1 &&
            File.Exists(journal) && new FileInfo(journal).Length > 0 &&
            JpegMarkerAudit.Inspect(original) == JpegMarkerAudit.Inspect(finished[0]) &&
            JpegLosslessPreviewEngine.EquivalentDecodedPixels(original, finished[0]) &&
            CombinedBatchResumeInspector.Inspect(input, output, tool).HasJpegPlan &&
            Hash(original) == originalHash &&
            File.GetLastWriteTimeUtc(original).Ticks == originalTicks;
        check(recovered,
            "R2.17 part20: after explicit test-only orphan removal JPEG journal resumes with validated real candidate");

        string finalCandidateHash = Hash(finished[0]);
        string finalJournalHash = Hash(journal);
        var again = JpegBatchPreviewEngine.Run(input, output, tool);
        bool idempotent = again.Completed == 1 &&
            Directory.GetFiles(previewFolder, "*.jpg").Length == 1 &&
            Hash(finished[0]) == finalCandidateHash &&
            Hash(journal) == finalJournalHash &&
            Hash(original) == originalHash &&
            File.GetLastWriteTimeUtc(original).Ticks == originalTicks;
        check(idempotent, "R2.17 part20: second JPEG resume does not duplicate candidates or journal entries");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-jpeg-crash-recovery.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-jpeg-crash-recovery-v1",
                officialSourceCommit = OfficialCommit,
                syntheticOnly = true,
                actualManagedJpegWorkerTerminated = crashedAfterRealCandidate,
                officialNativeJpegtranCompletedBeforeWorkerCrash = candidateIndependentlyValidated,
                unjournaledRealJpegCandidateCount = orphans.Length,
                unjournaledRealJpegCandidateBytes = orphanLength,
                combinedJpegResumeRefused = combinedRefused,
                directJpegResumeRefused = directRefused,
                refusalEvidenceByteExact = refusedWithoutMutation,
                testOnlyManualClearanceBeforeResume = true,
                savedJpegPlanReused = available.HasJpegPlan,
                recoveredRealJpegPreviewAndJournal = recovered,
                secondJpegResumeIdempotent = idempotent,
                sourceHashAndMtimePreserved = Hash(original) == originalHash &&
                    File.GetLastWriteTimeUtc(original).Ticks == originalTicks,
                killedNativeJpegtranDuringActiveEncoding = false,
                productionWpfAppKilled = false,
                automaticOrphanDeletion = false,
                productionOriginalReplacementEnabled = false,
                productionHardlinkMergeEnabled = false,
                realUserMediaTouched = false,
                actualRecoveredBytes = 0,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART20 JPEG CRASH PASS: genuine jpegtran output orphan refused, manual-only safe resume");
    }
}
