using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Abruptly terminated disposable child and independent parent oracle.
// This test-only executable never acts on a real-media directory.
internal static class R217PreviewCrashTests
{
    private const string Flag = "--r217-part19-generated-only-png-crash";
    private const string Env = "CHAPPY_R217_PART19_SYNTHETIC_CRASH";
    private static string Hash(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    public static bool IsCrashChild(string[] args) => args.Length > 0 && args[0] == Flag;

    public static int RunCrashChild(string[] args)
    {
        if (args.Length != 4 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Env) != "generated-only")
            throw new InvalidOperationException("No generated-only crash child authorization");
        string temp = Path.GetFullPath(Path.GetTempPath());
        foreach (string path in args.Skip(1))
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Non-synthetic path rejected");
        }
        PngBatchPreviewEngine.Run(args[1], args[2],
            testOnlyAfterInflatedWrite: bytes =>
            {
                File.WriteAllText(args[3], bytes.ToString(CultureInfo.InvariantCulture));
                using var self = Process.GetCurrentProcess();
                self.Kill(); // NOT cooperative cancellation; no finally unwinding
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidOperationException("PNG crash checkpoint was missed");
    }

    public static void Run(string parent, string input, Action<bool, string> check)
    {
        string output = Path.Combine(parent, "r217-part19-abrupt-output");
        string checkpointPath = Path.Combine(parent, "r217-part19-crash-checkpoint.txt");
        Directory.CreateDirectory(output);
        string source = Path.Combine(input, "source.png");
        string originalHash = Hash(source);
        long originalTicks = File.GetLastWriteTimeUtc(source).Ticks;
        string fakeTool = Path.Combine(parent, "part19-not-executable-jpegtran.exe");
        File.WriteAllText(fakeTool, "dummy path only, never run");

        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("No current test runner executable");
        var psi = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(R217PreviewCrashTests).Assembly.Location);
        foreach (string arg in new[] { Flag, input, output, checkpointPath })
            psi.ArgumentList.Add(arg);
        psi.Environment[Env] = "generated-only";
        using var child = Process.Start(psi) ??
            throw new IOException("Synthetic crash child could not start");
        try
        {
            if (!child.WaitForExit(45000))
                throw new TimeoutException("Crash child did not exit within 45s");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
        bool signal = File.Exists(checkpointPath) &&
            long.TryParse(File.ReadAllText(checkpointPath),
                NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) &&
            bytes > 0;
        string plan = Path.Combine(output, ".chappy-png-batch-r16.json");
        string journal = Path.Combine(output, ".chappy-png-batch-r16.progress.jsonl");
        string previewDir = Path.Combine(output, "chappy-png-previews");
        string[] scraps = Directory.Exists(previewDir) ?
            Directory.GetFiles(previewDir, "*.png") : Array.Empty<string>();
        bool crashConfirmed = child.ExitCode != 0 && signal &&
            File.Exists(plan) && !File.Exists(journal) &&
            scraps.Length == 1 && new FileInfo(scraps[0]).Length > 0 &&
            Hash(source) == originalHash &&
            File.GetLastWriteTimeUtc(source).Ticks == originalTicks;
        check(crashConfirmed,
            "R2.17 part 19: actual abrupt PNG worker death leaves one unjournaled candidate");
        string orphan = scraps[0];
        string originalOrphanHash = Hash(orphan), originalPlanHash = Hash(plan);
        int members = Directory.EnumerateFileSystemEntries(output).Count();
        bool inspectorRefused = false, directRefused = false;
        try { CombinedBatchResumeInspector.Inspect(input, output, fakeTool); }
        catch (InvalidDataException e) when (e.Message.Contains("参照のない候補"))
        { inspectorRefused = true; }
        try { PngBatchPreviewEngine.Run(input, output); }
        catch (InvalidDataException e) when (e.Message.Contains("参照のない候補"))
        { directRefused = true; }
        bool evidencePreserved = inspectorRefused && directRefused &&
            Hash(orphan) == originalOrphanHash && Hash(plan) == originalPlanHash &&
            !File.Exists(journal) &&
            Directory.EnumerateFileSystemEntries(output).Count() == members &&
            Hash(source) == originalHash &&
            File.GetLastWriteTimeUtc(source).Ticks == originalTicks;
        check(evidencePreserved,
            "R2.17 part 19: both read-only consent and direct PNG resume fail closed without deleting orphan");

        // Test-owned explicit manual cleanup only. Production never deletes
        // unknown or unreferenced candidate files automatically.
        File.Delete(orphan);
        var resumedPlan = CombinedBatchResumeInspector.Inspect(input, output, fakeTool);
        check(resumedPlan.HasPngPlan && !resumedPlan.HasJpegPlan &&
              resumedPlan.PngPlanEntries == 1 && Hash(plan) == originalPlanHash,
            "R2.17 part 19: after test-only manual orphan review, existing plan is reusable");
        var final = PngBatchPreviewEngine.Run(input, output);
        string[] candidates = Directory.GetFiles(previewDir, "*.png");
        bool recovered = final.Total == 1 && final.Failed == 0 &&
            final.Optimized == 1 && candidates.Length == 1 &&
            File.Exists(journal) && new FileInfo(journal).Length > 0 &&
            PngInspector.Inspect(candidates[0]).CandidateForFutureLosslessOptimization &&
            CombinedBatchResumeInspector.Inspect(input, output, fakeTool).HasPngPlan &&
            Hash(source) == originalHash &&
            File.GetLastWriteTimeUtc(source).Ticks == originalTicks;
        check(recovered, "R2.17 part 19: safe PNG resume after explicit synthetic-only orphan cleanup");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-preview-process-crash.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-preview-process-crash-v1",
                generatedOnly = true,
                actualChildProcessAbruptTermination = crashConfirmed,
                childReachedRealInflatedWriteCheckpoint = signal,
                childExitCodeNonzero = child.ExitCode != 0,
                leftoverUnjournaledPngCount = scraps.Length,
                combinedResumeOrphanRefused = inspectorRefused,
                directPngResumeOrphanRefused = directRefused,
                refusalPreservedOrphanAndPlanBytes = evidencePreserved,
                manualTestOnlyClearanceApplied = true,
                originalPlanReusedAfterClearance = resumedPlan.HasPngPlan,
                finalSyntheticPngPreviewSucceeded = recovered,
                sourceSha256AndMtimeUnchanged = Hash(source) == originalHash &&
                    File.GetLastWriteTimeUtc(source).Ticks == originalTicks,
                automaticOrphanDeletionPerformed = false,
                jpegNativeChildCrashOrphanTested = false,
                fullWpfAppProcessTerminated = false,
                physicalDesktopUiTested = false,
                productionOriginalReplacementEnabled = false,
                productionHardlinkMergeEnabled = false,
                realUserMediaTouched = false,
                actualRecoveredBytes = 0,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART19 PROCESS CRASH PASS: orphan blocked, evidence intact, manual test clearance and resume");
    }
}
