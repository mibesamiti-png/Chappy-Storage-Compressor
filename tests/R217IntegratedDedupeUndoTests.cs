using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part23: one real Core call sequence, not a new standalone bootstrap
// experiment. Normal WPF build keeps DedupeEngine and IndependentUndoEngine
// mutation-locked. Every file here is generated under ChappyStorageSmoke_...
internal static class R217IntegratedDedupeUndoTests
{
    private const string SourceCommit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";
    private static string Hash(string path) =>
        Safety.ComputeSha256(path, CancellationToken.None);

    private static void CopyIndependent(string source, string destination)
    {
        File.Copy(source, destination, overwrite: false);
        File.SetAttributes(destination, File.GetAttributes(source));
        File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(source));
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
    }

    private static void CheckReadOnlySafePartitions(string parent, Action<bool, string> check)
    {
        // Real generated NTFS IDs/attrs/time, not fake snapshots or real media.
        // Two regular files plus byte-identical Archive/time-divergent peers:
        // the incompatible names must not discard the safe 2-file subset.
        string root = Path.Combine(parent, "r217-b1-compatible-subgroups");
        Directory.CreateDirectory(root);
        string[] files = ["a.mp4", "b.mp4", "c.mp4", "d.mp4"]
            .Select(name => Path.Combine(root, name)).ToArray();
        File.WriteAllBytes(files[0], RandomNumberGenerator.GetBytes(64 * 1024));
        foreach (string path in files.Skip(1))
            File.Copy(files[0], path);
        var stamp = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        foreach (string path in files)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.SetCreationTimeUtc(path, stamp);
            File.SetLastWriteTimeUtc(path, stamp);
        }
        File.SetAttributes(files[2], FileAttributes.Archive);
        File.SetLastWriteTimeUtc(files[3], stamp.AddMinutes(3));
        string[] sha = files.Select(Hash).ToArray();
        FileIdentity[] ids = files.Select(NativeFile.GetIdentity).ToArray();
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        check(scan.Groups.Count == 1 && scan.Groups[0].Files.Count == 4 &&
            scan.Groups[0].BlockReason(false) is not null,
            "B1: exact-byte scanner keeps all four differently attributed/timed originals in one detection group");
        var strict = scan.Groups[0].PartitionForSafeMerge(false);
        var relaxed = scan.Groups[0].PartitionForSafeMerge(true);
        check(strict.Count == 3 &&
            strict.Count(g => g.Files.Count == 2 && g.BlockReason(false) is null) == 1 &&
            strict.Count(g => g.Files.Count == 1) == 2 &&
            relaxed.Count == 2 &&
            relaxed.Count(g => g.Files.Count == 3 && g.BlockReason(true) is null) == 1 &&
            relaxed.Count(g => g.Files.Count == 1) == 1,
            "B1: incompatible ACL/attributes/time split only the merge estimates; independent filenames are not discarded");
        var plan = DedupeFirstWorkflowPlan.Inspect(scan, false);
        var timePlan = DedupeFirstWorkflowPlan.Inspect(scan, true);
        var report = MediaDuplicateReport.Analyze(scan, false);
        var blankPreview = new CombinedCapacityReport.Summary(0, 0, 0, 0, 0, 0,
            Array.Empty<CombinedCapacityReport.Row>());
        var combined = CombinedSavingsPlanner.Analyze(blankPreview, scan, false);
        check(plan.EligibleGroups == 1 && plan.BlockedGroups == 2 &&
            plan.EstimatedDuplicateBytes == 64 * 1024 &&
            timePlan.EligibleGroups == 1 && timePlan.BlockedGroups == 1 &&
            timePlan.EstimatedDuplicateBytes == 2 * 64 * 1024 &&
            report.EligibleGroups == 1 && report.BlockedGroups == 2 &&
            report.PotentialBytes == 64 * 1024 &&
            combined.VerifiedDuplicateGroups == 1 &&
            combined.SkippedDuplicateGroups == 2 &&
            combined.DedupeOnlyBytes == 64 * 1024 &&
            combined.ActualFreedBytes == 0,
            "B1: actual read-only WPF planners/report/calculator agree on safe subgroup savings without counting excluded peers");
        check(files.Select((file, i) =>
                Hash(file) == sha[i] &&
                NativeFile.GetIdentity(file).SameFile(ids[i]) &&
                NativeFile.GetIdentity(file).LinkCount == 1).All(x => x) &&
            !Directory.Exists(Safety.StorePath(root)),
            "B1: read-only subgroup analysis preserves originals and creates no merge store");
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        CheckReadOnlySafePartitions(parent, check);
        string input = Path.Combine(parent, "r217-part23-dedupe-undo-generated");
        string output = Path.Combine(parent, "r217-part23-preview-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(input, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string evidence = Path.Combine(input, "part23-undo-evidence");
        Directory.CreateDirectory(evidence);
        string png = Path.Combine(input, "base.png");
        string jpg = Path.Combine(input, "base.jpg");
        string mp4 = Path.Combine(input, "base.mp4");
        R217PriorityImageAndMemoryTests.WriteUncompressedPng(png, 512, 320, 0);
        R217PriorityImageAndMemoryTests.WriteJpeg(jpg, 960, 640, 0);
        File.WriteAllBytes(mp4, RandomNumberGenerator.GetBytes(384 * 1024));
        CopyIndependent(png, Path.Combine(input, "copy.png"));
        CopyIndependent(jpg, Path.Combine(input, "copy.jpg"));
        CopyIndependent(jpg, Path.Combine(input, "third.jpeg"));
        CopyIndependent(mp4, Path.Combine(input, "copy.mp4"));

        var before = Scanner.Run(input, true, null, CancellationToken.None);
        check(before.FileCount == 7 && before.Groups.Count == 3 &&
            before.Groups.All(g => g.BlockReason(false) is null),
            "R2.17 part23: real two-stage scanner finds three exact synthetic JPG/PNG/MP4 groups");
        var originalSnapshots = before.Groups.SelectMany(g => g.Files)
            .ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
        var originalHashes = originalSnapshots.ToDictionary(
            pair => pair.Key, pair => Hash(pair.Key),
            StringComparer.OrdinalIgnoreCase);
        var plan = DedupeFirstWorkflowPlan.Inspect(before, false);
        long expectedLogical = before.Groups.Sum(g => g.Savings);
        bool planAccurate = plan.EligibleGroups == 3 && plan.BlockedGroups == 0 &&
            plan.DuplicateAliases == 4 && plan.ImageRepresentatives == 2 &&
            plan.RepeatImageCandidatesAvoided == 3 &&
            plan.EstimatedDuplicateBytes == expectedLogical &&
            plan.UndoAdditionalBytesApprox == expectedLogical &&
            plan.ActualFreedBytes == 0 && !plan.ProductionMutationEnabled;
        check(planAccurate,
            "R2.17 part23: read-only plan computes three groups, four aliases, two representative image previews, and Undo storage cost");

        // A stale byte-identical-looking scan must never receive approval.
        string tampered = Path.Combine(input, "copy.png");
        File.SetLastWriteTimeUtc(tampered, File.GetLastWriteTimeUtc(tampered).AddSeconds(4));
        var stale = DedupeFirstWorkflowPlan.Inspect(before, false);
        bool staleRefused = stale.EligibleGroups == 2 && stale.BlockedGroups == 1 &&
            stale.EstimatedDuplicateBytes < expectedLogical &&
            stale.Groups.Any(g => !g.CanConsolidate);
        check(staleRefused, "R2.17 part23: a changed source timestamp removes that group from read-only executable savings");
        File.SetLastWriteTimeUtc(tampered, originalSnapshots[tampered].ModifiedUtc);
        check(DedupeFirstWorkflowPlan.Inspect(before, false).EligibleGroups == 3,
            "R2.17 part23: original synthetic source metadata restored and read-only eligibility reverified");

        // The ACTUAL existing test-gated Core engine does the merge, rather
        // than pretending success from a synthetic prelinked file.
        Journal.AssertCleanBeforeMerge();
        long merged = DedupeEngine.Run(input, before.Groups, false,
            progress: null, CancellationToken.None);
        bool allLinked = merged == expectedLogical;
        foreach (DuplicateGroup group in before.Groups)
        {
            string master = Path.Combine(Safety.StorePath(input),
                group.Hash + Path.GetExtension(group.Representative.Path).ToLowerInvariant());
            allLinked &= File.Exists(master) &&
                NativeFile.SameFile(group.Representative.Path, master) &&
                NativeFile.GetIdentity(master).LinkCount == group.Files.Count + 1;
            foreach (FileSnapshot item in group.Files)
                allLinked &= NativeFile.SameFile(item.Path, master) &&
                    Hash(item.Path) == originalHashes[item.Path];
        }
        check(allLinked && Journal.Pending().Count == 0 &&
              Journal.UnresolvedRecordFiles().Count == 0,
            "R2.17 part23: REAL DedupeEngine.Run hardlink-consolidates 4 aliases across 3 verified groups and completes its journals");
        var postMergeScan = Scanner.Run(input, true, null, CancellationToken.None);
        check(postMergeScan.Groups.Count == 0,
            "R2.17 part23: post-merge real scanner never counts already-shared hardlinks as fresh duplicates");

        // Only ONE candidate per eligible image group is generated after the
        // real merge. This is a synthetic orchestration proof, NOT a shipped
        // production batch filter or an original-media replacement.
        var pngGroup = before.Groups.Single(g =>
            Path.GetExtension(g.Representative.Path).Equals(".png", StringComparison.OrdinalIgnoreCase));
        var jpegGroup = before.Groups.Single(g =>
            Path.GetExtension(g.Representative.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase));
        var pngPreview = PngLosslessOptimizer.CreatePreview(
            pngGroup.Representative.Path, output);
        bool pngRepresentativeOnly = pngPreview.PreviewPath is not null &&
            File.Exists(pngPreview.PreviewPath) && pngPreview.SavedBytes > 0 &&
            Directory.GetFiles(output, "*.png").Length == 1 &&
            Hash(pngGroup.Representative.Path) == pngGroup.Hash;
        check(pngRepresentativeOnly,
            "R2.17 part23: real PNG lossless optimizer produces one representative candidate after merge, not per alias");

        bool jpegRepresentativeOnly = false;
        string? official = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        if (!string.IsNullOrWhiteSpace(official))
        {
            check(File.Exists(official) &&
                  Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == SourceCommit &&
                  Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
                "R2.17 part23: representative JPEG compression uses official pinned SIMD jpegtran only");
            var jpegPreview = JpegLosslessPreviewEngine.CreatePreview(
                jpegGroup.Representative.Path, output, official);
            jpegRepresentativeOnly = jpegPreview.Candidate is not null &&
                File.Exists(jpegPreview.Candidate) && jpegPreview.EstimatedSavingBytes > 0 &&
                Directory.GetFiles(output, "*.jpg").Length == 1 &&
                JpegMarkerAudit.Inspect(jpegGroup.Representative.Path) ==
                    JpegMarkerAudit.Inspect(jpegPreview.Candidate) &&
                JpegLosslessPreviewEngine.EquivalentDecodedPixels(
                    jpegGroup.Representative.Path, jpegPreview.Candidate) &&
                Hash(jpegGroup.Representative.Path) == jpegGroup.Hash;
            check(jpegRepresentativeOnly,
                "R2.17 part23: official jpegtran creates exactly one RGB/marker-equivalent representative candidate after merge");
        }
        else Console.WriteLine("R2.17 part23 representative JPEG SKIPPED without source-pinned Windows CI tool");

        // Negative: a third-party hardlink alias defeats our expected link
        // count. No stage, journal or source bytes may change.
        DuplicateGroup mp4Group = before.Groups.Single(g =>
            Path.GetExtension(g.Representative.Path).Equals(".mp4", StringComparison.OrdinalIgnoreCase));
        string mp4Master = Path.Combine(Safety.StorePath(input),
            mp4Group.Hash + ".mp4");
        var mp4Alias = mp4Group.Files[1];
        uint expectedMp4Links = (uint)mp4Group.Files.Count + 1;
        string injectedAlias = Path.Combine(input, "test-owned-extra-hardlink.tmp");
        NativeFile.NewHardLink(injectedAlias, mp4Master);
        expectThrows(() => IndependentUndoEngine.Restore(input, mp4Alias,
            mp4Master, mp4Group.Hash, expectedMp4Links, evidence),
            "R2.17 part23: integrated independent Undo refuses unexpected external hardlink alias");
        check(NativeFile.SameFile(mp4Alias.Path, mp4Master) &&
              !Directory.EnumerateFiles(evidence, "*.part23-undo.json").Any() &&
              Hash(mp4Alias.Path) == mp4Group.Hash,
            "R2.17 part23: external link refusal leaves hardlinks, source bytes and Undo evidence unchanged");
        File.Delete(injectedAlias); // only the test-owned synthetic external alias

        bool stageTamperRefused = false;
        string? tamperedStage = null;
        try
        {
            IndependentUndoEngine.Restore(input, mp4Alias, mp4Master,
                mp4Group.Hash, expectedMp4Links, evidence,
                testOnlyBeforeFinalValidation: stage =>
                {
                    tamperedStage = stage;
                    File.AppendAllText(stage, "synthetic-tamper");
                });
        }
        catch (InvalidDataException) { stageTamperRefused = true; }
        bool beforeManualClearance = stageTamperRefused &&
            tamperedStage is not null && File.Exists(tamperedStage) &&
            Directory.EnumerateFiles(evidence, "*.part23-undo.json").Count() == 1 &&
            NativeFile.SameFile(mp4Alias.Path, mp4Master) &&
            Hash(mp4Alias.Path) == mp4Group.Hash;
        check(beforeManualClearance,
            "R2.17 part23: altered staged Undo bytes reject final cutover and retain durable evidence");
        File.Delete(tamperedStage!); // only known synthetic tampered stage
        foreach (var selfRecord in Directory.EnumerateFiles(evidence, "*.part23-undo.json"))
            File.Delete(selfRecord); // test-only explicit manual cleanup; no production auto-repair

        // Actually detach each alias through Core IndependentUndoEngine with
        // its own durable intent, then delete ONLY the known test-owned store
        // link if it is the final remaining alias of the representative.
        int detached = 0;
        bool undoBytesAndMetadata = true;
        foreach (DuplicateGroup group in before.Groups)
        {
            string master = Path.Combine(Safety.StorePath(input),
                group.Hash + Path.GetExtension(group.Representative.Path).ToLowerInvariant());
            foreach (FileSnapshot alias in group.Files.Skip(1))
            {
                uint linksBefore = NativeFile.GetIdentity(master).LinkCount;
                var undo = IndependentUndoEngine.Restore(input, alias, master,
                    group.Hash, linksBefore, evidence);
                detached++;
                FileSnapshot restored = Safety.Capture(alias.Path);
                undoBytesAndMetadata &= File.Exists(undo.DurableEvidencePath) &&
                    undo.SharedIdentityBefore.LinkCount == linksBefore &&
                    undo.IndependentIdentityAfter.LinkCount == 1 &&
                    !undo.IndependentIdentityAfter.SameFile(undo.SharedIdentityBefore) &&
                    restored.Identity.LinkCount == 1 &&
                    restored.Attributes == alias.Attributes &&
                    restored.ModifiedUtc == alias.ModifiedUtc &&
                    restored.CreationUtc == alias.CreationUtc &&
                    restored.SecurityDescriptor.AsSpan().SequenceEqual(alias.SecurityDescriptor) &&
                    Hash(alias.Path) == group.Hash &&
                    !NativeFile.SameFile(alias.Path, master);
            }
            // All test aliases of this group are independent again. The
            // master in .ChappyDedupeStore is a test-owned link we can
            // explicitly remove only with file ID, link count and hash proof.
            if (NativeFile.GetIdentity(master).LinkCount != 2 ||
                !NativeFile.SameFile(master, group.Representative.Path) ||
                Hash(master) != group.Hash)
                throw new InvalidDataException("Synthetic store link deletion not provably safe");
            File.Delete(master);
            undoBytesAndMetadata &= NativeFile.GetIdentity(group.Representative.Path).LinkCount == 1;
        }
        check(detached == 4 && undoBytesAndMetadata &&
              Directory.GetFiles(Safety.StorePath(input)).Length == 0 &&
              Directory.EnumerateFiles(evidence, "*.part23-undo.json").Count() == 4,
            "R2.17 part23: actual Core independent Undo detached all 4 aliases and removed only verified synthetic store links");

        bool fullOriginalsRestored = originalSnapshots.All(pair =>
        {
            var now = Safety.Capture(pair.Key);
            return now.Identity.LinkCount == 1 &&
                !pair.Value.Identity.SameFile(now.Identity) == !Path.GetFileName(pair.Key)
                    .StartsWith("base.", StringComparison.OrdinalIgnoreCase) &&
                now.Length == pair.Value.Length &&
                now.CreationUtc == pair.Value.CreationUtc &&
                now.ModifiedUtc == pair.Value.ModifiedUtc &&
                now.Attributes == pair.Value.Attributes &&
                now.SecurityDescriptor.AsSpan().SequenceEqual(pair.Value.SecurityDescriptor) &&
                Hash(pair.Key) == originalHashes[pair.Key];
        });
        // Source representatives retain their original NTFS identities;
        // their detached aliases correctly have NEW independent file IDs.
        check(fullOriginalsRestored,
            "R2.17 part23: all 7 generated media retain original bytes, timestamps, owner/DACL and individual link count one");

        string twin = mp4Group.Representative.Path;
        string restoredAlias = mp4Group.Files[1].Path;
        byte[] firstBytes = File.ReadAllBytes(restoredAlias);
        using (var writer = new FileStream(restoredAlias, FileMode.Append,
            FileAccess.Write, FileShare.None))
            writer.WriteByte(0x23);
        bool editsIndependent = Hash(twin) == mp4Group.Hash &&
            Hash(restoredAlias) != mp4Group.Hash &&
            !NativeFile.SameFile(twin, restoredAlias);
        check(editsIndependent,
            "R2.17 part23: editing restored MP4 alias no longer mutates former master or its NTFS file identity");
        File.WriteAllBytes(restoredAlias, firstBytes); // generated test media only
        File.SetAttributes(restoredAlias, mp4Alias.Attributes);
        File.SetCreationTimeUtc(restoredAlias, mp4Alias.CreationUtc);
        File.SetLastWriteTimeUtc(restoredAlias, mp4Alias.ModifiedUtc);
        check(Hash(restoredAlias) == mp4Group.Hash &&
              File.GetLastWriteTimeUtc(restoredAlias) == mp4Alias.ModifiedUtc,
            "R2.17 part23: independent-write test-only edit restored exact content and mtime");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-dedupe-undo-integration.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-dedupe-undo-integration-v1",
                generatedOnly = true,
                realScannerDuplicateGroups = before.Groups.Count,
                validatedAliasCount = plan.DuplicateAliases,
                readOnlyPlanAccurate = planAccurate,
                staleTimestampGroupExcluded = staleRefused,
                actualCoreDedupeEngineExecuted = allLinked,
                actualCoreDedupeLogicalSavingBytes = merged,
                postMergeScannerAvoidsExistingLinks = postMergeScan.Groups.Count == 0,
                duplicateFirstRepresentativePngCandidateOnly = pngRepresentativeOnly,
                duplicateFirstRepresentativeOfficialJpegCandidateOnly = jpegRepresentativeOnly,
                externalHardlinkUndoRefusedWithoutMutation = true,
                tamperedStageRefusedWithDurableEvidence = beforeManualClearance,
                detachedIndependentAliases = detached,
                actualCoreIndependentUndoBytesMetadataAndIdVerified = undoBytesAndMetadata,
                allOriginalsRestoredAfterUndo = fullOriginalsRestored,
                independentWriteDoesNotAffectFormerMaster = editsIndependent,
                undoEvidenceFilesRetained = 4,
                productionMergeEnabled = false,
                productionIndependentUndoEnabled = IndependentUndoEngine.ProductionEnabled,
                productionOriginalReplacementEnabled = false,
                fullCrashConsistentProductionUndoProven = false,
                completedMergeLedgerAvailableToProduction = false,
                realMediaTouched = false,
                actualUserBytesFreed = 0,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART23 END-TO-END synthetic Core: Scanner -> REAL DedupeEngine -> " +
            "one PNG/JPEG representative candidate -> REAL independent Undo; PASS");
    }
}
