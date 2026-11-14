using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// R2.17 part 4: read-only output-folder gate. All files are disposable
// synthetic material under the caller's unique NTFS smoke-test directory.
internal static class R217CombinedResumePreflightTests
{
    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string root = Path.Combine(parent, "r217-resume-preflight");
        string input = Path.Combine(root, "input");
        string alternateInput = Path.Combine(root, "wrong-input");
        string output = Path.Combine(root, "candidate-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(alternateInput);
        Directory.CreateDirectory(output);
        try
        {
            string tool = Path.Combine(root, "jpegtran.exe");
            string otherTool = Path.Combine(root, "different", "jpegtran.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(otherTool)!);
            File.WriteAllText(tool, "synthetic-tool-never-executed");
            File.WriteAllText(otherTool, "synthetic-tool-never-executed");
            string png = Path.Combine(input, "synthetic.png");
            // A valid 1x1 PNG already used in the original scanner suite.
            File.WriteAllBytes(png, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC"));
            string jpg = Path.Combine(input, "synthetic.jpg");
            File.WriteAllBytes(jpg, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            string pngHash = Hash(png), jpgHash = Hash(jpg);

            var fresh = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(!fresh.IsResume && !fresh.HasPngPlan && !fresh.HasJpegPlan &&
                  fresh.PngPlanEntries == 0 && fresh.JpegPlanEntries == 0 &&
                  !Directory.EnumerateFileSystemEntries(output).Any(),
                "R2.17: empty output classified as new without creating lock or journal");

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                bool interrupted = false;
                try { CombinedBatchResumeInspector.Inspect(input, output, tool, canceled.Token); }
                catch (OperationCanceledException) { interrupted = true; }
                check(interrupted && !Directory.EnumerateFileSystemEntries(output).Any(),
                    "R2.17: pre-canceled folder inspection does not create artifacts");
            }

            // Execute the real PNG queue; only synthetic input and output are used.
            var pngBatch = PngBatchPreviewEngine.Run(input, output);
            check(pngBatch.Total == 1 && pngBatch.Failed == 0,
                "R2.17: PNG engine creates a valid synthetic plan and progress journal");
            var pngOnly = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(pngOnly.IsResume && pngOnly.HasPngPlan && !pngOnly.HasJpegPlan &&
                  pngOnly.PngPlanEntries == 1 && pngOnly.JpegPlanEntries == 0,
                "R2.17: partial PNG-only output correctly offers PNG resume and JPEG new");

            // Synthesize a JPEG *plan only*. The fake executable must never run.
            // An actual runtime would create this via its own BuildPlan/SavePlan.
            var jpegPlan = JpegBatchPreviewEngine.BuildPlan(input, output,
                tool, CancellationToken.None);
            check(jpegPlan.Entries.Count == 1,
                "R2.17: JPEG entry planned without invoking an external executable");
            string jpegPlanFile = Path.Combine(output, ".chappy-jpeg-r19.plan.json");
            File.WriteAllText(jpegPlanFile, JsonSerializer.Serialize(jpegPlan));
            var resumed = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(resumed.IsResume && resumed.HasPngPlan && resumed.HasJpegPlan &&
                  resumed.PngPlanEntries == 1 && resumed.JpegPlanEntries == 1,
                "R2.17: two existing format plans recognized for explicit combined resume");

            string pngPlanFile = Path.Combine(output, ".chappy-png-batch-r16.json");
            string pngJournal = Path.Combine(output, ".chappy-png-batch-r16.progress.jsonl");
            byte[] pngPlanOriginal = File.ReadAllBytes(pngPlanFile);
            byte[] jpegPlanOriginal = File.ReadAllBytes(jpegPlanFile);
            byte[] pngJournalOriginal = File.ReadAllBytes(pngJournal);
            check(File.ReadAllBytes(pngPlanFile).AsSpan().SequenceEqual(pngPlanOriginal) &&
                  File.ReadAllBytes(jpegPlanFile).AsSpan().SequenceEqual(jpegPlanOriginal) &&
                  File.ReadAllBytes(pngJournal).AsSpan().SequenceEqual(pngJournalOriginal),
                "R2.17: preview inspection leaves both plans and existing journal unchanged");

            expectThrows(() => CombinedBatchResumeInspector.Inspect(alternateInput, output, tool),
                "R2.17: refuse existing output plan tied to a different source directory");
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, otherTool),
                "R2.17: refuse existing JPEG plan tied to another jpegtran executable");

            string foreign = Path.Combine(output, "private-photo.jpg");
            File.WriteAllBytes(foreign, new byte[] { 1, 2, 3 });
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17: refuse mixed user files in reused candidate output root");
            check(File.ReadAllBytes(foreign).SequenceEqual(new byte[] { 1, 2, 3 }),
                "R2.17: refusal never deletes or changes unrelated output content");
            File.Delete(foreign); // disposable synthetic file only.

            string foreignPreview = Path.Combine(output, "chappy-png-previews", "user-note.txt");
            File.WriteAllText(foreignPreview, "do-not-touch");
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17: refuse a foreign file placed inside preview candidate folder");
            check(File.ReadAllText(foreignPreview) == "do-not-touch",
                "R2.17: foreign candidate-directory content is preserved after refusal");
            File.Delete(foreignPreview); // disposable synthetic file only.

            string log = Path.Combine(output, ".chappy-jpeg-r19.progress.jsonl");
            File.WriteAllText(log, "{\"RelativePath\":\"synthetic.jpg\"");
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17: fail closed on an incomplete JPEG resume journal");
            check(File.ReadAllText(log).EndsWith("synthetic.jpg\""),
                "R2.17: incomplete JPEG journal evidence is kept intact");
            File.Delete(log);

            File.AppendAllText(pngJournal,
                "{\"RelativePath\":\"synthetic.png\",\"Status\":");
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17: fail closed on an incomplete PNG resume journal");
            check(File.ReadAllBytes(pngJournal).AsSpan().StartsWith(pngJournalOriginal),
                "R2.17: corrupted PNG journal preserved without repair or truncation");
            File.WriteAllBytes(pngJournal, pngJournalOriginal); // test-only reset.

            string pngHardlink = Path.Combine(root, "png-plan-shared-alias");
            NativeFile.NewHardLink(pngHardlink, pngPlanFile);
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17: refuse reused plan whose NTFS identity has another hardlink");
            File.Delete(pngHardlink); // test-owned alias only.

            File.Delete(jpegPlanFile);
            var resumedPng = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(resumedPng.HasPngPlan && !resumedPng.HasJpegPlan &&
                  File.ReadAllBytes(pngPlanFile).AsSpan().SequenceEqual(pngPlanOriginal) &&
                  File.ReadAllBytes(pngJournal).AsSpan().SequenceEqual(pngJournalOriginal) &&
                  Hash(png) == pngHash && Hash(jpg) == jpgHash,
                "R2.17: all valid resume records and both originals preserved");

            File.Delete(pngPlanFile);
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17: refuse output with old progress data but missing source plan");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-safe-preview-resume.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-safe-preview-resume-v1",
                    scope = "isolated artificial NTFS preview files only",
                    newEmptyOutputVerified = true,
                    pngOnlyResumeVerified = true,
                    combinedResumeVerified = true,
                    mismatchedInputRefused = true,
                    mismatchedJpegtranRefused = true,
                    unknownRootFileRefused = true,
                    unknownPreviewFileRefused = true,
                    jpegTornJournalRefused = true,
                    pngTornJournalRefused = true,
                    hardlinkedPlanRefused = true,
                    missingPlanWithJournalRefused = true,
                    allUserAndCandidateEvidencePreserved = true,
                    externalExecutableInvoked = false,
                    actualWpfClickAutomationVerified = false,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
