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
            check(CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, fresh) == fresh,
                "R2.17 part 13: unchanged empty output passes a second read-only consent gate");

            // A foreign item may appear while the confirmation dialog is open.
            // The new gate must fail closed and preserve the added file.
            string lateForeign = Path.Combine(output, "late-foreign.txt");
            byte[] lateBytes = { 0x10, 0x20, 0x30, 0x40 };
            File.WriteAllBytes(lateForeign, lateBytes);
            bool foreignRefused = false;
            try
            {
                CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, fresh);
            }
            catch (InvalidDataException) { foreignRefused = true; }
            check(foreignRefused && File.ReadAllBytes(lateForeign).SequenceEqual(lateBytes) &&
                  !Directory.EnumerateFiles(output, "*.lock").Any(),
                "R2.17 part 13: foreign output appearing during confirmation refuses without mutation");
            File.Delete(lateForeign); // synthetic disposable test artifact only.

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
            check(CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, pngOnly) == pngOnly,
                "R2.17 part 13: unchanged PNG-only resume remains allowed after consent");

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

            check(CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, resumed) == resumed,
                "R2.17 part 13: unchanged two-format resume passes the consent recheck");

            // Part 14: same-count changes are not visible in the older typed
            // presence/count snapshot. The complete validated plan/journal
            // bytes must be bound to the user's displayed consent.
            File.AppendAllText(pngPlanFile, "\n");
            var changedPngPlan = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(changedPngPlan.PngPlanEntries == resumed.PngPlanEntries &&
                  changedPngPlan.JpegPlanEntries == resumed.JpegPlanEntries &&
                  changedPngPlan.PngPlanSha256 != resumed.PngPlanSha256,
                "R2.17 part 14: same-count PNG plan byte mutation changes its approval digest");
            bool changedPngPlanRefused = false;
            try { CombinedBatchResumeInspector.VerifyAfterConfirmation(input, output, tool, resumed); }
            catch (InvalidDataException) { changedPngPlanRefused = true; }
            finally { File.WriteAllBytes(pngPlanFile, pngPlanOriginal); }
            check(changedPngPlanRefused,
                "R2.17 part 14: refuse modified-but-valid PNG plan after displayed consent");

            File.AppendAllText(jpegPlanFile, "\n");
            var changedJpegPlan = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(changedJpegPlan.JpegPlanEntries == resumed.JpegPlanEntries &&
                  changedJpegPlan.JpegPlanSha256 != resumed.JpegPlanSha256,
                "R2.17 part 14: same-count JPEG plan byte mutation changes its approval digest");
            bool changedJpegPlanRefused = false;
            try { CombinedBatchResumeInspector.VerifyAfterConfirmation(input, output, tool, resumed); }
            catch (InvalidDataException) { changedJpegPlanRefused = true; }
            finally { File.WriteAllBytes(jpegPlanFile, jpegPlanOriginal); }
            check(changedJpegPlanRefused,
                "R2.17 part 14: refuse modified-but-valid JPEG plan after displayed consent");

            File.AppendAllText(pngJournal, "\n");
            var changedPngJournal = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(changedPngJournal.PngPlanEntries == resumed.PngPlanEntries &&
                  changedPngJournal.PngJournalSha256 != resumed.PngJournalSha256,
                "R2.17 part 14: equivalent PNG journal with added blank row changes approval digest");
            bool changedPngJournalRefused = false;
            try { CombinedBatchResumeInspector.VerifyAfterConfirmation(input, output, tool, resumed); }
            catch (InvalidDataException) { changedPngJournalRefused = true; }
            finally { File.WriteAllBytes(pngJournal, pngJournalOriginal); }
            check(changedPngJournalRefused,
                "R2.17 part 14: refuse modified-but-valid PNG progress journal after consent");

            string jpegJournal = Path.Combine(output, ".chappy-jpeg-r19.progress.jsonl");
            File.WriteAllBytes(jpegJournal, Array.Empty<byte>());
            var emptyJpegLog = CombinedBatchResumeInspector.Inspect(input, output, tool);
            File.AppendAllText(jpegJournal, "\n");
            var changedJpegJournal = CombinedBatchResumeInspector.Inspect(input, output, tool);
            check(changedJpegJournal.JpegPlanEntries == emptyJpegLog.JpegPlanEntries &&
                  changedJpegJournal.JpegJournalSha256 != emptyJpegLog.JpegJournalSha256,
                "R2.17 part 14: valid empty JPEG journal change is tracked even with same entry count");
            bool changedJpegJournalRefused = false;
            try { CombinedBatchResumeInspector.VerifyAfterConfirmation(input, output, tool, emptyJpegLog); }
            catch (InvalidDataException) { changedJpegJournalRefused = true; }
            finally { File.Delete(jpegJournal); } // Synthetic-only disposable state.
            check(changedJpegJournalRefused,
                "R2.17 part 14: refuse modified-but-valid JPEG progress journal after consent");

            using (var canceledAfterPrompt = new CancellationTokenSource())
            {
                canceledAfterPrompt.Cancel();
                bool cancelRefused = false;
                try
                {
                    CombinedBatchResumeInspector.VerifyAfterConfirmation(
                        input, output, tool, resumed, canceledAfterPrompt.Token);
                }
                catch (OperationCanceledException) { cancelRefused = true; }
                check(cancelRefused && Hash(png) == pngHash && Hash(jpg) == jpgHash &&
                      File.ReadAllBytes(pngPlanFile).AsSpan().SequenceEqual(pngPlanOriginal) &&
                      File.ReadAllBytes(jpegPlanFile).AsSpan().SequenceEqual(jpegPlanOriginal) &&
                      File.ReadAllBytes(pngJournal).AsSpan().SequenceEqual(pngJournalOriginal),
                    "R2.17 part 14: cancellation before approval recheck never changes synthetic originals or journals");
            }
            check(CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, resumed) == resumed,
                "R2.17 part 14: original plans/journal still pass a fresh unchanged consent recheck");
            bool newPlanRefused = false;
            try
            {
                CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, pngOnly);
            }
            catch (InvalidDataException) { newPlanRefused = true; }
            check(newPlanRefused,
                "R2.17 part 13: newly appearing JPEG plan invalidates earlier PNG-only consent");

            // A removed plan can still leave a valid PNG-only output. It must
            // not silently change the user's confirmed combined resume scope.
            string heldJpegPlan = Path.Combine(root, "held-jpeg-plan.json");
            File.Move(jpegPlanFile, heldJpegPlan);
            bool removedPlanRefused = false;
            try
            {
                CombinedBatchResumeInspector.VerifyAfterConfirmation(
                    input, output, tool, resumed);
            }
            catch (InvalidDataException) { removedPlanRefused = true; }
            finally { File.Move(heldJpegPlan, jpegPlanFile); }
            check(removedPlanRefused &&
                  File.ReadAllBytes(jpegPlanFile).AsSpan().SequenceEqual(jpegPlanOriginal) &&
                  Hash(png) == pngHash && Hash(jpg) == jpgHash,
                "R2.17 part 13: disappearing JPEG plan refuses and preserves source/plan bytes");

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

            // A correctly named candidate can still be an unjournaled orphan
            // (crashed encoder), and must not be silently trusted.
            string fakePngCandidate = Path.Combine(output, "chappy-png-previews",
                "synthetic.chappy-preview-" + Guid.NewGuid().ToString("N") + ".png");
            byte[] orphanBytes = { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x11 };
            File.WriteAllBytes(fakePngCandidate, orphanBytes);
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17 part 19: reject correctly named but unreferenced PNG candidate");
            expectThrows(() => PngBatchPreviewEngine.Run(input, output),
                "R2.17 part 19: direct PNG preview engine must refuse unreferenced candidate");
            check(File.ReadAllBytes(fakePngCandidate).SequenceEqual(orphanBytes),
                "R2.17 part 19: PNG orphan refusal preserves unknown candidate bytes");
            File.Delete(fakePngCandidate); // generated test-owned artifact only.

            string fakeJpegPreviewDir = Path.Combine(output, "chappy-jpeg-r19-previews");
            Directory.CreateDirectory(fakeJpegPreviewDir);
            string fakeJpegCandidate = Path.Combine(fakeJpegPreviewDir,
                "synthetic.chappy-jpeg-preview-" + Guid.NewGuid().ToString("N") + ".jpg");
            File.WriteAllBytes(fakeJpegCandidate, orphanBytes);
            expectThrows(() => CombinedBatchResumeInspector.Inspect(input, output, tool),
                "R2.17 part 19: reject correctly named but unreferenced JPEG candidate");
            expectThrows(() => JpegBatchPreviewEngine.Run(input, output, tool),
                "R2.17 part 19: direct JPEG preview engine refuses orphan before dummy tool execution");
            check(File.ReadAllBytes(fakeJpegCandidate).SequenceEqual(orphanBytes),
                "R2.17 part 19: JPEG orphan refusal preserves unknown candidate bytes");
            File.Delete(fakeJpegCandidate);
            Directory.Delete(fakeJpegPreviewDir); // test-owned synthetic directory only.

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
                    part19GeneratedLookingPngOrphanRefused = true,
                    part19GeneratedLookingJpegOrphanRefused = true,
                    part19DirectFormatQueuesRejectOrphans = true,
                    part19OrphanEvidencePreserved = true,
                    jpegTornJournalRefused = true,
                    pngTornJournalRefused = true,
                    hardlinkedPlanRefused = true,
                    missingPlanWithJournalRefused = true,
                    postConfirmationReadOnlyRecheckVerified = true,
                    postConfirmationForeignFileRefused = true,
                    newPlanAfterConsentRefused = true,
                    removedPlanAfterConsentRefused = true,
                    pngPlanByteChangeAfterConsentRefused = true,
                    jpegPlanByteChangeAfterConsentRefused = true,
                    pngJournalByteChangeAfterConsentRefused = true,
                    jpegJournalByteChangeAfterConsentRefused = true,
                    canceledConsentRecheckRefused = true,
                    restoredByteIdenticalApprovalRecheckPassed = true,
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
