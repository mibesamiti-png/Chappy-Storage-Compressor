using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// A guarded generated-only worker crashes while the exact official native
// jpegtran child is observed alive. Does not execute on actual user media.
internal static class R217JpegParentDeathTests
{
    private const string Flag = "--r217-part21-generated-only-jpeg-parent-death";
    private const string Guard = "CHAPPY_R217_PART21_PARENT_CRASH";
    private const string OfficialCommit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";

    public static bool IsCrashChild(string[] args) =>
        args.Length > 0 && args[0] == Flag;

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
            throw new InvalidOperationException("Part21 parent-death worker refused unsafe arguments");
        string? pinned = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(pinned) ||
            !Path.GetFullPath(pinned).Equals(Path.GetFullPath(args[3]),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Part21 requires exact verified official executable");

        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        foreach (string path in new[] { args[1], args[2], args[4] })
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Part21 refused a non-generated output or signal");
        }

        JpegBatchPreviewEngine.Run(args[1], args[2], args[3],
            testOnlyNativeProcessId: pid =>
            {
                // Called by the REAL JPEG engine after Process.Start, before
                // WaitForExitAsync. An observed live PID is required before the
                // guarded managed worker kills ITSELF (not the native child).
                using var native = Process.GetProcessById(pid);
                if (native.HasExited)
                    throw new InvalidOperationException("Native child exited before crash checkpoint");
                long startedUtcTicks = native.StartTime.ToUniversalTime().Ticks;
                File.WriteAllText(args[4], pid.ToString(CultureInfo.InvariantCulture) +
                    "|" + startedUtcTicks.ToString(CultureInfo.InvariantCulture));
                using var self = Process.GetCurrentProcess();
                self.Kill();
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidOperationException("Part21 native parent-death checkpoint missed");
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? tool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(tool))
        {
            Console.WriteLine("R2.17 part21 native-parent-death SKIPPED outside source-pinned CI");
            return;
        }
        check(File.Exists(tool) &&
            Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == OfficialCommit &&
            Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17 part21: isolated parent-death test requires source-pinned official SIMD jpegtran");

        string input = Path.Combine(parent, "r217-part21-parent-death-input");
        string output = Path.Combine(parent, "r217-part21-parent-death-output");
        string checkpoint = Path.Combine(parent, "r217-part21-native-pid.txt");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);

        // Sufficiently large generated image for the native process to remain
        // alive when Process.Start returns; neither runtime nor CI speed is
        // inferred from this artificial 5120x3072 test image.
        string original = Path.Combine(input, "large-generated.jpg");
        R217PriorityImageAndMemoryTests.WriteJpeg(original, 5120, 3072, 1);
        string originalHash = Hash(original);
        long originalTicks = File.GetLastWriteTimeUtc(original).Ticks;

        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("Part21 Core executable not found");
        var start = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(R217JpegParentDeathTests).Assembly.Location);
        foreach (string arg in new[] { Flag, input, output, tool, checkpoint })
            start.ArgumentList.Add(arg);
        start.Environment[Guard] = "generated-only";

        using var worker = Process.Start(start) ??
            throw new IOException("Part21 managed test worker could not start");
        try
        {
            if (!worker.WaitForExit(60000))
                throw new TimeoutException("Part21 managed test worker did not exit");
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit();
            }
        }
        bool parsed = File.Exists(checkpoint);
        string[] checkpointParts = parsed ? File.ReadAllText(checkpoint).Trim().Split('|') :
            Array.Empty<string>();
        parsed = checkpointParts.Length == 2 &&
            int.TryParse(checkpointParts[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out _) &&
            long.TryParse(checkpointParts[1], NumberStyles.None,
                CultureInfo.InvariantCulture, out _);
        int nativePid = parsed ? int.Parse(checkpointParts[0], CultureInfo.InvariantCulture) : 0;
        long nativeStartTicks = parsed ? long.Parse(checkpointParts[1], CultureInfo.InvariantCulture) : 0;
        bool managedDiedWhileNativeLive = worker.ExitCode != 0 && parsed && nativePid > 0 &&
            nativeStartTicks > 0;
        check(managedDiedWhileNativeLive,
            "R2.17 part21: actual managed worker died after observing official jpegtran PID alive");

        // A native child is not automatically killed when its Windows parent
        // exits. Observe its exit with a finite timeout. Kill only after
        // confirming the exact pinned executable AND process creation time.
        bool observedAfterParent = false, nativeFinished = false, emergencyKill = false;
        try
        {
            using var native = Process.GetProcessById(nativePid);
            if (native.StartTime.ToUniversalTime().Ticks != nativeStartTicks)
                throw new InvalidDataException("Native PID was reused: refuse to touch another process");
            observedAfterParent = !native.HasExited;
            if (observedAfterParent && !native.WaitForExit(45000))
            {
                string executingPath = Path.GetFullPath(native.MainModule?.FileName ??
                    throw new InvalidDataException("Cannot confirm native process identity"));
                if (!executingPath.Equals(Path.GetFullPath(tool),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Refuse to kill unrecognized native process");
                native.Kill(entireProcessTree: true);
                native.WaitForExit();
                emergencyKill = true;
            }
            nativeFinished = native.HasExited;
        }
        catch (ArgumentException)
        {
            // The exact native PID already finished and exited before the
            // test parent got a handle. This is normal for short-lived jobs.
            nativeFinished = true;
        }
        check(nativeFinished && !emergencyKill,
            "R2.17 part21: exact native JPEG child eventually exits without leaking an unmanaged process");

        string plan = Path.Combine(output, ".chappy-jpeg-r19.plan.json");
        string journal = Path.Combine(output, ".chappy-jpeg-r19.progress.jsonl");
        string previewFolder = Path.Combine(output, "chappy-jpeg-r19-previews");
        string[] leftovers = Directory.Exists(previewFolder) ?
            Directory.GetFiles(previewFolder, "*.jpg") : Array.Empty<string>();
        check(File.Exists(plan) && !File.Exists(journal) && leftovers.Length <= 1 &&
              Hash(original) == originalHash &&
              File.GetLastWriteTimeUtc(original).Ticks == originalTicks,
            "R2.17 part21: saved plan remains, no journal or source mutation after parent death");

        bool orphanRefused = false, directRefused = false, preserved = false;
        long leftoverBytes = leftovers.Length == 1 ? new FileInfo(leftovers[0]).Length : 0;
        if (leftovers.Length == 1)
        {
            string leftover = leftovers[0];
            string leftoverHash = Hash(leftover), planHash = Hash(plan);
            try { CombinedBatchResumeInspector.Inspect(input, output, tool); }
            catch (InvalidDataException e) when (e.Message.Contains("参照のない候補",
                StringComparison.Ordinal)) { orphanRefused = true; }
            try { JpegBatchPreviewEngine.Run(input, output, tool); }
            catch (InvalidDataException e) when (e.Message.Contains("参照のない候補",
                StringComparison.Ordinal)) { directRefused = true; }
            preserved = orphanRefused && directRefused &&
                Hash(leftover) == leftoverHash && Hash(plan) == planHash &&
                !File.Exists(journal) && Hash(original) == originalHash;
            check(preserved, "R2.17 part21: crash-left native JPEG candidate rejected by both resume paths with bytes intact");
            // Explicit disposal of the test's ONLY known synthetic orphan.
            File.Delete(leftover);
        }
        else
        {
            var safe = CombinedBatchResumeInspector.Inspect(input, output, tool);
            preserved = safe.HasJpegPlan && safe.JpegPlanEntries == 1;
            check(preserved,
                "R2.17 part21: no native leftover means same plan may safely resume without auto-deletion");
        }

        var result = JpegBatchPreviewEngine.Run(input, output, tool);
        string[] validCandidates = Directory.GetFiles(previewFolder, "*.jpg");
        bool retrySafe = result.Total == 1 && result.Completed == 1 &&
            result.Failed == 0 && validCandidates.Length == 1 &&
            File.Exists(journal) && new FileInfo(journal).Length > 0 &&
            JpegMarkerAudit.Inspect(original) == JpegMarkerAudit.Inspect(validCandidates[0]) &&
            JpegLosslessPreviewEngine.EquivalentDecodedPixels(original, validCandidates[0]) &&
            Hash(original) == originalHash &&
            File.GetLastWriteTimeUtc(original).Ticks == originalTicks;
        check(retrySafe,
            "R2.17 part21: real JPEG batch resumes after managed parent died with former native child alive");
        string candidateHash = Hash(validCandidates[0]), journalHash = Hash(journal);
        var repeat = JpegBatchPreviewEngine.Run(input, output, tool);
        bool idempotent = repeat.Completed == 1 &&
            Directory.GetFiles(previewFolder, "*.jpg").Length == 1 &&
            Hash(validCandidates[0]) == candidateHash && Hash(journal) == journalHash;
        check(idempotent, "R2.17 part21: native-parent-death retry is idempotent");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-native-parent-death.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-native-parent-death-v1",
                sourcePinnedCommit = OfficialCommit,
                generatedOnly = true,
                actualManagedParentDeathWhileNativeWasObservedAlive = managedDiedWhileNativeLive,
                nativePidAndStartTimeRecorded = parsed,
                nativeObservedAliveAfterParentDeath = observedAfterParent,
                nativeExitedWithoutEmergencyKill = nativeFinished && !emergencyKill,
                oneOrZeroNativePreviewFilesAfterCrash = leftovers.Length <= 1,
                unjournaledNativeLeftoverCount = leftovers.Length,
                unjournaledNativeLeftoverBytes = leftoverBytes,
                orphanRefusalVerifiedWhenPresent = preserved,
                candidateAndPlanPreservedOnRefusal = preserved,
                noJournalBeforeExplicitRecovery = true,
                normalRetryPixelAndMarkerEquivalent = retrySafe,
                secondResumeIdempotent = idempotent,
                sourceHashAndMtimeUnchanged = Hash(original) == originalHash &&
                    File.GetLastWriteTimeUtc(original).Ticks == originalTicks,
                actualNativeEncodingProgressAtKillNotProven = true,
                physicallyKilledProductionWpfApp = false,
                autoDeleteUnknownOrphan = false,
                realMediaTouched = false,
                productionOriginalReplacementEnabled = false,
                productionHardlinkMergeEnabled = false,
                actualFreedBytes = 0,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART21 native parent death: native observed after parent=" +
            observedAfterParent + ", orphanCount=" + leftovers.Length +
            ", orphanBytes=" + leftoverBytes + "; PASS");
    }
}
