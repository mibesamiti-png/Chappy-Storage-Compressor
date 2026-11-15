using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Generated-only deterministic race tests: mutations after WPF consent,
// immediately before format-engine entry, and after a durable plan is saved
// but before the first candidate. No personal/user files are opened.
internal static class R217PreviewStartRaceTests
{
    private static string Hash(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string root = Path.Combine(parent, "r217-part22-start-race");
        string input = Path.Combine(root, "generated-input");
        string pngOutput = Path.Combine(root, "png-output");
        string jpegOutput = Path.Combine(root, "jpeg-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(pngOutput);
        Directory.CreateDirectory(jpegOutput);
        string png = Path.Combine(input, "source.png");
        string jpeg = Path.Combine(input, "source.jpg");
        R217PriorityImageAndMemoryTests.WriteUncompressedPng(png, 512, 320, 0);
        R217PriorityImageAndMemoryTests.WriteJpeg(jpeg, 960, 640, 0);
        string pngHash = Hash(png), jpegHash = Hash(jpeg);
        long pngTicks = File.GetLastWriteTimeUtc(png).Ticks;
        long jpegTicks = File.GetLastWriteTimeUtc(jpeg).Ticks;
        string? official = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        string dummy = Path.Combine(root, "jpegtran.exe");
        File.WriteAllText(dummy, "synthetic dummy: never executed");
        string tool = string.IsNullOrWhiteSpace(official) ? dummy : official;
        var approvedPng = PersonalMediaBatchPreflight.Inspect(input, pngOutput);
        var approvedJpeg = PersonalMediaBatchPreflight.Inspect(input, jpegOutput);
        check(approvedPng.PngFiles == 1 && approvedPng.JpegFiles == 1 &&
              approvedPng.InventorySha256 == approvedJpeg.InventorySha256,
            "R2.17 part 22: two independent output roots share the exact generated source approval");

        byte[] stranger = { 0x21, 0x11, 0xCA, 0xFE, 0x35 };
        string latePng = Path.Combine(pngOutput, "foreign-after-consent.txt");
        File.WriteAllBytes(latePng, stranger);
        bool pngPrelockRefused = false;
        try { PngBatchPreviewEngine.Run(input, pngOutput, approvedInput: approvedPng); }
        catch (InvalidDataException e) when (e.Message.Contains("未確認のファイル")) { pngPrelockRefused = true; }
        check(pngPrelockRefused &&
              Directory.EnumerateFileSystemEntries(pngOutput).Count() == 1 &&
              File.ReadAllBytes(latePng).SequenceEqual(stranger) &&
              Hash(png) == pngHash,
            "R2.17 part 22: direct PNG rejects foreign output after consent BEFORE any lock or plan write");
        File.Delete(latePng);

        string lateJpeg = Path.Combine(jpegOutput, "foreign-after-consent.txt");
        File.WriteAllBytes(lateJpeg, stranger);
        bool jpegPrelockRefused = false;
        try { JpegBatchPreviewEngine.Run(input, jpegOutput, tool, approvedInput: approvedJpeg); }
        catch (InvalidDataException e) when (e.Message.Contains("未確認のファイル")) { jpegPrelockRefused = true; }
        check(jpegPrelockRefused &&
              Directory.EnumerateFileSystemEntries(jpegOutput).Count() == 1 &&
              File.ReadAllBytes(lateJpeg).SequenceEqual(stranger) &&
              Hash(jpeg) == jpegHash,
            "R2.17 part 22: direct JPEG rejects foreign output after consent BEFORE any lock or plan write");
        File.Delete(lateJpeg);

        // A more difficult race: after a genuine plan is already on disk,
        // but BEFORE the candidate directory or file is created.
        string afterPngPlan = Path.Combine(pngOutput, "foreign-after-png-plan.txt");
        bool pngPostPlanRefused = false;
        try
        {
            PngBatchPreviewEngine.Run(input, pngOutput, approvedInput: approvedPng,
                testOnlyBeforeCandidateSetup: () => File.WriteAllBytes(afterPngPlan, stranger));
        }
        catch (InvalidDataException e) when (e.Message.Contains("未確認のファイル"))
        { pngPostPlanRefused = true; }
        string pngPlan = Path.Combine(pngOutput, ".chappy-png-batch-r16.json");
        string pngJournal = Path.Combine(pngOutput, ".chappy-png-batch-r16.progress.jsonl");
        bool pngPlanPreserved = pngPostPlanRefused && File.Exists(pngPlan) &&
            !File.Exists(pngJournal) &&
            !Directory.Exists(Path.Combine(pngOutput, "chappy-png-previews")) &&
            File.ReadAllBytes(afterPngPlan).SequenceEqual(stranger) &&
            Hash(png) == pngHash;
        check(pngPlanPreserved,
            "R2.17 part 22: injected foreign output AFTER PNG plan is saved refuses before any candidate creation");
        string pngPlanHash = Hash(pngPlan);
        bool pngReadOnlyRefused = false;
        try { CombinedBatchResumeInspector.Inspect(input, pngOutput, tool); }
        catch (InvalidDataException) { pngReadOnlyRefused = true; }
        check(pngReadOnlyRefused && Hash(pngPlan) == pngPlanHash &&
              File.ReadAllBytes(afterPngPlan).SequenceEqual(stranger),
            "R2.17 part 22: combined inspector also refuses post-plan PNG foreign file without touching evidence");
        File.Delete(afterPngPlan); // only exact synthetic foreign file created above
        var pngRetry = PngBatchPreviewEngine.Run(input, pngOutput, approvedInput: approvedPng);
        bool pngRetrySuccess = pngRetry.Total == 1 && pngRetry.Failed == 0 &&
            pngRetry.Optimized == 1 && File.Exists(pngJournal) &&
            Directory.GetFiles(Path.Combine(pngOutput, "chappy-png-previews"), "*.png").Length == 1 &&
            Hash(pngPlan) == pngPlanHash && Hash(png) == pngHash &&
            File.GetLastWriteTimeUtc(png).Ticks == pngTicks;
        check(pngRetrySuccess,
            "R2.17 part 22: PNG resumes safely after explicit test-only clearance and reuses exact saved plan");

        // Source metadata can ALSO change after plan save while the approved
        // WPF inventory is unchanged in memory. Engines must verify internally.
        string sourceRaceOutput = Path.Combine(root, "source-race-output");
        Directory.CreateDirectory(sourceRaceOutput);
        var sourceApproved = PersonalMediaBatchPreflight.Inspect(input, sourceRaceOutput);
        string addedSource = Path.Combine(input, "arrived-after-approval.png");
        bool sourcePostPlanRefused = false;
        try
        {
            PngBatchPreviewEngine.Run(input, sourceRaceOutput,
                approvedInput: sourceApproved, testOnlyBeforeCandidateSetup:
                () => File.Copy(png, addedSource));
        }
        catch (InvalidDataException e) when (e.Message.Contains("入力画像"))
        { sourcePostPlanRefused = true; }
        string sourceRacePlan = Path.Combine(sourceRaceOutput, ".chappy-png-batch-r16.json");
        check(sourcePostPlanRefused && File.Exists(sourceRacePlan) &&
              !Directory.Exists(Path.Combine(sourceRaceOutput, "chappy-png-previews")) &&
              Hash(png) == pngHash && Hash(addedSource) == pngHash,
            "R2.17 part 22: newly added source AFTER PNG plan save is refused before candidate creation");
        File.Delete(addedSource); // generated-only test source
        var sourceRetry = PngBatchPreviewEngine.Run(input, sourceRaceOutput,
            approvedInput: sourceApproved);
        bool sourceAfterPlanRecovered = sourceRetry.Total == 1 &&
            sourceRetry.Optimized == 1 && sourceRetry.Failed == 0 &&
            Directory.GetFiles(Path.Combine(sourceRaceOutput, "chappy-png-previews"), "*.png").Length == 1 &&
            Hash(png) == pngHash;
        check(sourceAfterPlanRecovered,
            "R2.17 part 22: PNG saved plan safely resumes once synthetic after-approval source is removed");

        bool jpegPostPlanRefused = false, jpegPlanPreserved = false;
        bool jpegReadOnlyRefused = false, jpegRetryVerified = false;
        bool jpegSourcePostPlanRefused = false, jpegSourcePostPlanRecovered = false;
        if (!string.IsNullOrWhiteSpace(official))
        {
            check(File.Exists(official) &&
                  Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") ==
                      "7b4993786d856cbccabfc0ed2f923ba000e326cc" &&
                  Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
                "R2.17 part 22: JPEG post-plan race test only uses pinned official SIMD tool");
            string afterJpegPlan = Path.Combine(jpegOutput, "foreign-after-jpeg-plan.txt");
            try
            {
                JpegBatchPreviewEngine.Run(input, jpegOutput, tool,
                    approvedInput: approvedJpeg,
                    testOnlyBeforeCandidateSetup: () => File.WriteAllBytes(afterJpegPlan, stranger));
            }
            catch (InvalidDataException e) when (e.Message.Contains("未確認のファイル"))
            { jpegPostPlanRefused = true; }
            string jpegPlan = Path.Combine(jpegOutput, ".chappy-jpeg-r19.plan.json");
            string jpegJournal = Path.Combine(jpegOutput, ".chappy-jpeg-r19.progress.jsonl");
            jpegPlanPreserved = jpegPostPlanRefused && File.Exists(jpegPlan) &&
                !File.Exists(jpegJournal) &&
                !Directory.Exists(Path.Combine(jpegOutput, "chappy-jpeg-r19-previews")) &&
                File.ReadAllBytes(afterJpegPlan).SequenceEqual(stranger) &&
                Hash(jpeg) == jpegHash;
            check(jpegPlanPreserved,
                "R2.17 part 22: foreign JPEG output AFTER saved plan refuses with no native tool or candidate");
            string jpegPlanHash = Hash(jpegPlan);
            try { CombinedBatchResumeInspector.Inspect(input, jpegOutput, tool); }
            catch (InvalidDataException) { jpegReadOnlyRefused = true; }
            check(jpegReadOnlyRefused && Hash(jpegPlan) == jpegPlanHash &&
                  File.ReadAllBytes(afterJpegPlan).SequenceEqual(stranger),
                "R2.17 part 22: read-only combined output recheck refuses late JPEG foreign file");
            File.Delete(afterJpegPlan);

            var jpegRetry = JpegBatchPreviewEngine.Run(input, jpegOutput, tool,
                approvedInput: approvedJpeg);
            string[] jpegCandidates = Directory.GetFiles(
                Path.Combine(jpegOutput, "chappy-jpeg-r19-previews"), "*.jpg");
            jpegRetryVerified = jpegRetry.Total == 1 && jpegRetry.Completed == 1 &&
                jpegRetry.Failed == 0 && jpegCandidates.Length == 1 &&
                File.Exists(jpegJournal) && Hash(jpegPlan) == jpegPlanHash &&
                JpegMarkerAudit.Inspect(jpeg) == JpegMarkerAudit.Inspect(jpegCandidates[0]) &&
                JpegLosslessPreviewEngine.EquivalentDecodedPixels(jpeg, jpegCandidates[0]) &&
                Hash(jpeg) == jpegHash &&
                File.GetLastWriteTimeUtc(jpeg).Ticks == jpegTicks;
            check(jpegRetryVerified,
                "R2.17 part 22: after test-only removal, JPEG original plan resumes with official codec and pixel/marker equality");

            string jpegSourceOutput = Path.Combine(root, "jpeg-source-race-output");
            Directory.CreateDirectory(jpegSourceOutput);
            var sourceJpegApproved = PersonalMediaBatchPreflight.Inspect(input, jpegSourceOutput);
            DateTime originalTime = File.GetLastWriteTimeUtc(jpeg);
            try
            {
                JpegBatchPreviewEngine.Run(input, jpegSourceOutput, tool,
                    approvedInput: sourceJpegApproved,
                    testOnlyBeforeCandidateSetup: () =>
                        File.SetLastWriteTimeUtc(jpeg, originalTime.AddSeconds(4)));
            }
            catch (InvalidDataException e) when (e.Message.Contains("入力画像"))
            { jpegSourcePostPlanRefused = true; }
            finally { File.SetLastWriteTimeUtc(jpeg, originalTime); }
            check(jpegSourcePostPlanRefused &&
                  File.Exists(Path.Combine(jpegSourceOutput, ".chappy-jpeg-r19.plan.json")) &&
                  !Directory.Exists(Path.Combine(jpegSourceOutput, "chappy-jpeg-r19-previews")) &&
                  Hash(jpeg) == jpegHash &&
                  File.GetLastWriteTimeUtc(jpeg).Ticks == jpegTicks,
                "R2.17 part 22: retimestamped JPEG source after plan save is refused before native tool starts");
            var jpegSourceRetry = JpegBatchPreviewEngine.Run(input, jpegSourceOutput, tool,
                approvedInput: sourceJpegApproved);
            string[] retryJpegs = Directory.GetFiles(
                Path.Combine(jpegSourceOutput, "chappy-jpeg-r19-previews"), "*.jpg");
            jpegSourcePostPlanRecovered = jpegSourceRetry.Completed == 1 &&
                retryJpegs.Length == 1 &&
                JpegLosslessPreviewEngine.EquivalentDecodedPixels(jpeg, retryJpegs[0]) &&
                Hash(jpeg) == jpegHash;
            check(jpegSourcePostPlanRecovered,
                "R2.17 part 22: original JPEG source timestamp restored, real official candidate generated on saved-plan retry");
        }

        bool allSourcesUnchanged = Hash(png) == pngHash && Hash(jpeg) == jpegHash &&
            File.GetLastWriteTimeUtc(png).Ticks == pngTicks &&
            File.GetLastWriteTimeUtc(jpeg).Ticks == jpegTicks;
        check(allSourcesUnchanged, "R2.17 part 22: all independent synthetic originals retain exact hashes and mtimes");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-preview-start-race.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-preview-start-race-v1",
                generatedOnly = true,
                pngForeignRootBeforeLockRefusedWithoutWriting = pngPrelockRefused,
                jpegForeignRootBeforeLockRefusedWithoutWriting = jpegPrelockRefused,
                pngForeignAfterPlanBeforeCandidateRefused = pngPlanPreserved,
                pngCombinedReadOnlyAlsoRefused = pngReadOnlyRefused,
                pngRecoveredFromSamePlanAfterExplicitTestOnlyClearance = pngRetrySuccess,
                pngSourceChangedAfterPlanRefused = sourcePostPlanRefused,
                pngSourceRestoredAndSavedPlanResumed = sourceAfterPlanRecovered,
                jpegForeignAfterPlanBeforeCandidateRefused = jpegPlanPreserved,
                jpegCombinedReadOnlyAlsoRefused = jpegReadOnlyRefused,
                jpegRecoveredFromSamePlanWithOfficialPixelsAndMarkers = jpegRetryVerified,
                jpegSourceChangedAfterPlanRefused = jpegSourcePostPlanRefused,
                jpegSourceRestoredAndSavedPlanResumed = jpegSourcePostPlanRecovered,
                allOriginalSha256AndMtimeUnchanged = allSourcesUnchanged,
                noAutomaticForeignFileDeletion = true,
                noFilesystemAtomicSnapshotClaimed = true,
                fullProductionWpfAppCrashVerified = false,
                productionOriginalReplacementEnabled = false,
                productionHardlinkMergeEnabled = false,
                realUserMediaTouched = false,
                actualFreedBytes = 0,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART22 START RACE PASS: foreign and source mutation refused after approval and after real plan");
    }
}
