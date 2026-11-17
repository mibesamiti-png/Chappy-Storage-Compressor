using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using ChappyStorageCompressor.Core;

// Part27: real Core four-name synthetic merge, group-level persisted history,
// actual killed independent-Undo worker AFTER its first durable alias checkpoint.
internal static class R217GroupLedgerResumeTests
{
    private const string Flag = "--r217-part27-generated-group-undo-kill";
    private const string Guard = "CHAPPY_PART27_GROUP_UNDO_GENERATED_ONLY";
    private static readonly string[] BatchCrashCheckpoints =
    [
        "journal-recorded", "backup-renamed", "new-link-created",
        "pre-backup-delete", "backup-deleted",
        "group-core-verified", "group-completed"
    ];
    private static string Hash(string path) => Safety.ComputeSha256(path, CancellationToken.None);

    // The SAME real Core generated files are measured at each crash boundary.
    // GetCompressedFileSizeW returns the allocated size of the data stream,
    // not the logical file length. Count each NTFS volume/file ID exactly ONCE
    // so shared aliases and store master cannot inflate physical usage.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "GetCompressedFileSizeW", SetLastError = true)]
    private static extern uint GetAllocatedLow(string name, out uint high);

    private static long AllocatedBytes(string path)
    {
        uint low = GetAllocatedLow(path, out uint high);
        int error = Marshal.GetLastWin32Error();
        if (low == uint.MaxValue && error != 0)
            throw new Win32Exception(error, "NTFS allocation unavailable: " + path);
        return checked((long)(((ulong)high << 32) | low));
    }

    private static (long Bytes, int UniqueIds) UniqueAllocated(
        IEnumerable<string> sourcePaths)
    {
        var ids = new HashSet<(uint Volume, ulong Index)>();
        long total = 0;
        foreach (string path in sourcePaths.Where(File.Exists))
        {
            FileIdentity id = NativeFile.GetIdentity(path);
            if (ids.Add((id.VolumeSerial, id.FileIndex)))
                total = checked(total + AllocatedBytes(path));
        }
        return (total, ids.Count);
    }



    public static bool IsChild(string[] args) => args.Length > 0 && args[0] == Flag;

    private static void GuardRoot(string root)
    {
        string full = Safety.ValidateRoot(root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(full, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part27 worker denied non-generated root");
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 4 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only")
            throw new InvalidOperationException("Part27 worker not authorized");
        string? crashPoint = args[2] == "merge-batch" ? "backup-deleted" :
            args[2].StartsWith("merge-batch:", StringComparison.Ordinal)
                ? args[2]["merge-batch:".Length..] : null;
        if (crashPoint is not null &&
            !BatchCrashCheckpoints.Contains(crashPoint, StringComparer.Ordinal))
            throw new InvalidOperationException("Unknown generated Core crash checkpoint");
        bool inspectUndo = args[2].StartsWith("review-undo:", StringComparison.Ordinal);
        string reviewId = inspectUndo ? args[2]["review-undo:".Length..] : "";
        if (inspectUndo && !Guid.TryParseExact(reviewId, "N", out _))
            throw new InvalidOperationException("Invalid generated restart review receipt");
        if (!inspectUndo && crashPoint is null &&
            !Guid.TryParseExact(args[2], "N", out _))
            throw new InvalidOperationException("Unknown generated Undo receipt");
        string root = Path.GetFullPath(args[1]);
        GuardRoot(root);
        string signal = Path.GetFullPath(args[3]);
        if (!signal.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Part27 checkpoint outside generated root");
        Journal.TestFolderPath = Path.Combine(root, "part27-journal");
        if (inspectUndo)
        {
            // A NEW process, with no scanner/manifest objects passed through
            // memory, must reidentify all original metadata using on-disk
            // durable receipts. Its only write is the generated test signal.
            var candidate = CompletedGroupLedger.InspectUndoCandidates(root)
                .Single(x => x.OperationId == reviewId);
            if (!candidate.ReviewReady || candidate.OriginalFiles.Count < 2)
                throw new InvalidDataException("Restarted Undo review failed to certify group");
            string json = JsonSerializer.Serialize(new
            {
                candidate.OperationId,
                candidate.State,
                candidate.AlreadyIndependentAliases,
                candidate.RemainingAliases,
                candidate.RequiredAdditionalBytes,
                candidate.ProductionUndoAuthorized,
                originalSources = candidate.OriginalFiles.Select(x => new
                {
                    x.Path, x.OriginalVolumeSerial, x.OriginalFileIndex,
                    x.OriginalLinks, x.CreationUtc, x.ModifiedUtc, x.Attributes,
                    x.Sha256, x.OwnerDaclFingerprint
                })
            });
            using (var stream = new FileStream(signal, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            return 0;
        }
        if (crashPoint is not null)
        {
            // Genuine OS process termination, never an exception unwinding
            // or a simulated power loss. The first group must have committed
            // before stopping at each critical transition of the second group.
            var batch = Scanner.Run(root, true, null, CancellationToken.None)
                .Groups.OrderBy(g => g.Representative.Length).ToList();
            if (batch.Count != 3 || batch[0].Files.Count != 2 ||
                batch[1].Files.Count != 3 || batch[2].Files.Count != 2)
                throw new InvalidDataException("Generated-only batch kill fixture mismatch");
            int reached = 0;
            FaultInjection.OnCheckpoint = checkpoint =>
            {
                if (checkpoint != crashPoint || ++reached != 2)
                    return;
                using (var stream = new FileStream(signal, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    stream.Write(System.Text.Encoding.UTF8.GetBytes(
                        "second-group-" + crashPoint));
                    stream.Flush(flushToDisk: true);
                }
                using var self = Process.GetCurrentProcess();
                self.Kill(); // exact generated worker, not user processes
                Thread.Sleep(Timeout.Infinite);
            };
            CompletedGroupLedger.MergeVerifiedGroups(root, batch, CancellationToken.None);
            throw new InvalidOperationException("Generated merge batch kill was not reached");
        }
        string evidence = Path.Combine(root, "part27-undo-evidence");
        CompletedGroupLedger.ResumeUndoSynthetic(root, args[2], evidence,
            afterDurableAlias: count =>
            {
                if (count != 1) return;
                using (var stream = new FileStream(signal, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                        "first-alias-checkpoint-durable");
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                using var self = Process.GetCurrentProcess();
                self.Kill(); // only the explicitly authorized generated worker
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidOperationException("Part27 synthetic process kill checkpoint not reached");
    }

    private static int KillWorker(string root, string id, string signal)
    {
        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("Missing test runner executable");
        var info = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(R217GroupLedgerResumeTests).Assembly.Location);
        foreach (var arg in new[] { Flag, root, id, signal }) info.ArgumentList.Add(arg);
        info.Environment[Guard] = "generated-only";
        using var child = Process.Start(info) ??
            throw new IOException("Part27 generated process failed to start");
        try
        {
            if (!child.WaitForExit(60000))
                throw new TimeoutException("Part27 generated worker failed to exit");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
        }
        return child.ExitCode;
    }

    private static void VerifyRestartUndoReview(string root, string id,
        IReadOnlyDictionary<string, FileSnapshot> before,
        int expectedDetached, Action<bool, string> check)
    {
        string reportPath = Path.Combine(root,
            "part27-undo-review-" + expectedDetached + ".json");
        int exitCode = KillWorker(root, "review-undo:" + id, reportPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(reportPath));
        JsonElement data = document.RootElement;
        bool state = exitCode == 0 &&
            data.GetProperty("OperationId").GetString() == id &&
            data.GetProperty("State").GetString() ==
                (expectedDetached == 0 ? "VerifiedCompleted" :
                    "ExplicitResumeRequired") &&
            data.GetProperty("AlreadyIndependentAliases").GetInt32() == expectedDetached &&
            data.GetProperty("RemainingAliases").GetInt32() == 3 - expectedDetached &&
            data.GetProperty("ProductionUndoAuthorized").GetBoolean() == false &&
            data.GetProperty("RequiredAdditionalBytes").GetInt64() ==
                (3 - expectedDetached) * before.Values.First().Length +
                CapacityGuard.TargetReserve;
        var sourceRows = data.GetProperty("originalSources").EnumerateArray().ToArray();
        bool exact = state && sourceRows.Length == 4 &&
            sourceRows.All(row =>
            {
                string path = row.GetProperty("Path").GetString() ?? "";
                if (!before.TryGetValue(path, out FileSnapshot? original))
                    return false;
                return row.GetProperty("OriginalVolumeSerial").GetUInt32() ==
                        original.Identity.VolumeSerial &&
                    row.GetProperty("OriginalFileIndex").GetUInt64() ==
                        original.Identity.FileIndex &&
                    row.GetProperty("OriginalLinks").GetUInt32() ==
                        original.Identity.LinkCount &&
                    row.GetProperty("CreationUtc").GetDateTime() ==
                        original.CreationUtc &&
                    row.GetProperty("ModifiedUtc").GetDateTime() ==
                        original.ModifiedUtc &&
                    row.GetProperty("Attributes").GetInt32() ==
                        (int)original.Attributes &&
                    row.GetProperty("Sha256").GetString() ==
                        Safety.ComputeSha256(path, CancellationToken.None) &&
                    row.GetProperty("OwnerDaclFingerprint").GetString() ==
                        Convert.ToHexString(SHA256.HashData(original.SecurityDescriptor));
            });
        check(exact &&
            CompletedGroupLedger.InspectUndoCandidates(root).Single(x =>
                x.OperationId == id).RemainingAliases == 3 - expectedDetached,
            "G3-01: independent restarted child reidentifies all 4 ORIGINAL IDs, SHA, metadata and exact remaining Undo aliases without changing originals, detached=" +
                expectedDetached);
        if (!exact)
            throw new InvalidDataException("G3-01 genuine child restart receipt invalid");
    }

    // B2: crash *after* the real Core has consolidated all names but *before*
    // group manifest promotion. A prepared record is not implicitly complete.
    // Explicit approval must re-check every original ID, alias and SHA.
    private static void VerifyPreparedGroupPromotion(string parent,
        Action<bool, string> check, Action<Action, string> expectThrows)
    {
        string root = Path.Combine(parent, "r217-b2-prepared-group-promotion");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        GuardRoot(root);
        string? previous = Journal.TestFolderPath;
        try
        {
            Journal.TestFolderPath = Path.Combine(root, "part27-journal");
            string a = Path.Combine(root, "prepared-a.mp4");
            string b = Path.Combine(root, "prepared-b.mp4");
            string d = Path.Combine(root, "prepared-c.mp4");
            File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(96 * 1024));
            File.Copy(a, b);
            File.Copy(a, d);
            DateTime common = File.GetLastWriteTimeUtc(a);
            DateTime created = File.GetCreationTimeUtc(a);
            foreach (string path in new[] { a, b, d })
            {
                File.SetCreationTimeUtc(path, created);
                File.SetLastWriteTimeUtc(path, common);
            }
            var scan = Scanner.Run(root, true, null, CancellationToken.None);
            var group = scan.Groups.Single();
            var originalIds = group.Files.Select(f => f.Identity).ToArray();
            check(group.Files.Count == 3 && group.BlockReason(false) is null,
                "B2: prepared manifest fixture consists of three independent byte-equal originals");

            string unowned = Guid.NewGuid().ToString("N");
            // The same internal authorized entrypoint is compiled into the
            // normal WPF build; tests use real NTFS files + strict marker.
            expectThrows(() => DedupeEngine.RunPreparedGroup(root, group,
                unowned, null, CancellationToken.None),
                "B2: shared production-compiled Core rejects a missing prepared manifest BEFORE any mutation");
            expectThrows(() => DedupeEngine.RunPreparedGroup(root, group,
                "malformed-ID", null, CancellationToken.None),
                "B2: shared authorized Core rejects malformed group IDs before any mutation");
            check(!Directory.Exists(Safety.StorePath(root)) &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0 &&
                group.Files.Select((x, i) =>
                    NativeFile.GetIdentity(x.Path).SameFile(originalIds[i])).All(x => x),
                "B2: unrecorded group cannot create store links, journals or change original IDs");

            expectThrows(() => CompletedGroupLedger.MergeSynthetic(root, group,
                testOnlyAfterVerifiedCore: () =>
                    throw new IOException("generated-only stop after core, before durable commit")),
                "B2: injected crash window after real Core merge keeps PREPARED manifest for review");
            var review = CompletedGroupLedger.Inspect(root);
            string id = review.Entries.Single().OperationId;
            string master = Path.Combine(Safety.StorePath(root), group.Hash + ".mp4");
            string prepared = Path.Combine(Safety.StorePath(root),
                ".chappy-group-completed", id + ".prepared.json");
            check(review.NeedsReview == 1 &&
                File.Exists(prepared) &&
                group.Files.All(x => NativeFile.SameFile(x.Path, master)) &&
                NativeFile.GetIdentity(master).LinkCount == 4 &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0 &&
                CompletedMergeLedger.Inspect(root).Entries.Count == 0,
                "B2: completed physical Core merge remains manual-review until explicit verified durable promotion");

            string foreign = Path.Combine(root, "b2-owned-extra-link.tmp");
            NativeFile.NewHardLink(foreign, master);
            expectThrows(() => CompletedGroupLedger.ConfirmPreparedSynthetic(root, id),
                "B2: unexpected external hardlink blocks promotion of PREPARED manifest");
            check(CompletedGroupLedger.Inspect(root).NeedsReview == 1 &&
                File.Exists(prepared) && File.Exists(foreign) &&
                NativeFile.SameFile(foreign, master),
                "B2: unexpected link rejection neither erases foreign evidence nor promotes the record");
            if (!NativeFile.SameFile(foreign, master))
                throw new InvalidDataException("Generated link ownership changed");
            File.Delete(foreign);

            CompletedGroupLedger.ConfirmPreparedSynthetic(root, id);
            var done = CompletedGroupLedger.Inspect(root);
            check(done.VerifiedCompleted == 1 && done.NeedsReview == 0 &&
                done.Entries.Single().AliasCount == 2 && !File.Exists(prepared) &&
                CompletedMergeLedger.Inspect(root).Entries.Count == 0,
                "B2: explicit restart-time promotion requires ALL alias IDs, metadata and bytes, with no pending journal");
            expectThrows(() => CompletedGroupLedger.ConfirmPreparedSynthetic(root, id),
                "B2: committed group cannot be promoted twice");
            expectThrows(() => DedupeEngine.RunPreparedGroup(root, group,
                id, null, CancellationToken.None),
                "B2: normal-compiled authorized Core cannot reuse a committed durable manifest");

            string evidence = Path.Combine(root, "part27-group-evidence");
            Directory.CreateDirectory(evidence);
            int detached = CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence);
            var restored = CompletedGroupLedger.Inspect(root);
            check(detached == 2 && restored.VerifiedRestored == 1 &&
                group.Files.All(x => Hash(x.Path) == group.Hash) &&
                !NativeFile.SameFile(a, b) && !NativeFile.SameFile(b, d) &&
                NativeFile.GetIdentity(master).LinkCount == 2,
                "B2: explicitly promoted group remains usable by existing independent Undo and durable checkpoints");
        }
        finally
        {
            Journal.TestFolderPath = previous;
        }
    }

    // B2: use the SAME durable group ledger and independent Undo for two
    // names and for the older four-name crash fixture. The old pair-specific
    // CompletedMergeLedger remains readable but must receive no new records.
    private static void VerifyTwoNameUnifiedLedger(string parent,
        Action<bool, string> check, Action<Action, string> expectThrows)
    {
        string root = Path.Combine(parent, "r217-b2-two-name-shared-ledger");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        GuardRoot(root);
        Journal.TestFolderPath = Path.Combine(root, "r217-b2-journal");
        string a = Path.Combine(root, "b2-pair-a.mp4");
        string b = Path.Combine(root, "b2-pair-b.mp4");
        File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(288 * 1024));
        File.Copy(a, b);
        File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
        File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        check(scan.Groups.Count == 1 && scan.Groups[0].Files.Count == 2 &&
              scan.Groups[0].BlockReason(false) is null,
            "B2: two independently identified equal-byte names are eligible for the common manifest");
        // Reject a perfectly valid duplicate pair when its source is OUTSIDE
        // the root selected for the transaction. Hash/ACL equality alone
        // must never grant arbitrary pathname mutation authority.
        string outside = Path.Combine(parent, "r217-b2-outside-selected-root");
        Directory.CreateDirectory(outside);
        string alienA = Path.Combine(outside, "alien-a.mp4");
        string alienB = Path.Combine(outside, "alien-b.mp4");
        File.Copy(a, alienA);
        File.Copy(a, alienB);
        File.SetCreationTimeUtc(alienA, File.GetCreationTimeUtc(a));
        File.SetCreationTimeUtc(alienB, File.GetCreationTimeUtc(a));
        File.SetLastWriteTimeUtc(alienA, File.GetLastWriteTimeUtc(a));
        File.SetLastWriteTimeUtc(alienB, File.GetLastWriteTimeUtc(a));
        var foreignScan = Scanner.Run(outside, true, null, CancellationToken.None);
        check(foreignScan.Groups.Count == 1 &&
              !Safety.ContainedRegularSource(root, alienA) &&
              !Safety.ContainedRegularSource(root, alienB) &&
              !Safety.ContainedRegularSource(root, Path.Combine(
                  Safety.StorePath(root), "foreign.mp4")),
            "B2: outside-root and reserved-store names are not allowed by shared admission policy");
        string priorA = Hash(alienA), priorB = Hash(alienB);
        var foreignIdA = NativeFile.GetIdentity(alienA);
        var foreignIdB = NativeFile.GetIdentity(alienB);
        expectThrows(() => DedupeEngine.Run(root, foreignScan.Groups, false,
            null, CancellationToken.None),
            "B2: real Core refuses foreign-root exact duplicates before any store/journal creation");
        check(!Directory.Exists(Safety.StorePath(root)) &&
              NativeFile.GetIdentity(alienA).SameFile(foreignIdA) &&
              NativeFile.GetIdentity(alienB).SameFile(foreignIdB) &&
              !NativeFile.SameFile(alienA, alienB) &&
              Hash(alienA) == priorA && Hash(alienB) == priorB &&
              Journal.Pending().Count == 0 &&
              Journal.UnresolvedRecordFiles().Count == 0,
            "B2: preflight rejection leaves every out-of-scope source independent and original");

        var snapshots = scan.Groups.Single().Files.ToDictionary(x => x.Path,
            StringComparer.OrdinalIgnoreCase);
        string hash = scan.Groups.Single().Hash;
        string id = CompletedGroupLedger.MergeSynthetic(root, scan.Groups.Single());
        string master = Path.Combine(Safety.StorePath(root), hash + ".mp4");
        var complete = CompletedGroupLedger.Inspect(root);
        check(complete.VerifiedCompleted == 1 &&
              complete.Entries.Single().AliasCount == 1 &&
              complete.Entries.Single().DetachedCount == 0 &&
              CompletedMergeLedger.Inspect(root).Entries.Count == 0 &&
              NativeFile.GetIdentity(master).LinkCount == 3 &&
              snapshots.Keys.All(path => NativeFile.SameFile(path, master) &&
                  Hash(path) == hash) &&
              Journal.Pending().Count == 0 &&
              Journal.UnresolvedRecordFiles().Count == 0,
            "B2: 2-file merge writes ONE durable group manifest and no parallel pair ledger");

        string external = Path.Combine(root, "b2-test-owned-foreign-link.tmp");
        NativeFile.NewHardLink(external, master);
        string evidence = Path.Combine(root, "b2-undo-evidence");
        Directory.CreateDirectory(evidence);
        check(CompletedGroupLedger.Inspect(root).NeedsReview == 1,
            "B2: unregistered hardlink blocks 2-file manifest exactly as it blocks 4-file manifests");
        expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
            "B2: unregistered link prevents two-name Undo");
        if (!NativeFile.SameFile(external, master))
            throw new InvalidDataException("Generated external link identity changed");
        File.Delete(external);

        // Inject a recoverable exception AFTER the only alias checkpoint was
        // flushed. The next invocation must finish idempotently from disk,
        // even though no aliases remain to loop over.
        expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence,
            afterDurableAlias: n =>
            {
                if (n == 1) throw new IOException("generated-only interruption after checkpoint");
            }), "B2: stop after first and only durable alias checkpoint");
        var pending = CompletedGroupLedger.Inspect(root);
        check(pending.ExplicitResumeRequired == 1 &&
              pending.Entries.Single().DetachedCount == 1 &&
              NativeFile.GetIdentity(master).LinkCount == 2 &&
              CompletedMergeLedger.Inspect(root).Entries.Count == 0,
            "B2: one-alias group interruption reopens as explicit resume, never auto-completed");
        int resumed = CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence);
        var restored = CompletedGroupLedger.Inspect(root);
        bool unchanged = snapshots.Values.All(saved =>
        {
            var now = Safety.Capture(saved.Path);
            return now.Length == saved.Length &&
                now.CreationUtc == saved.CreationUtc &&
                now.ModifiedUtc == saved.ModifiedUtc &&
                now.Attributes == saved.Attributes &&
                now.SecurityDescriptor.AsSpan().SequenceEqual(saved.SecurityDescriptor) &&
                now.Identity.VolumeSerial == saved.Identity.VolumeSerial &&
                now.Identity.LinkCount == (saved.Path ==
                    scan.Groups.Single().Representative.Path ? 2u : 1u) &&
                Hash(now.Path) == hash;
        });
        check(resumed == 1 && restored.VerifiedRestored == 1 &&
              restored.NeedsReview == 0 &&
              restored.Entries.Single().DetachedCount == 1 &&
              !NativeFile.SameFile(a, b) &&
              NativeFile.GetIdentity(master).LinkCount == 2 &&
              unchanged && CompletedMergeLedger.Inspect(root).Entries.Count == 0,
            "B2: common durable 2-file manifest safely resumes after interrupted final Undo and restores original metadata/independent IDs");
        expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
            "B2: a fully restored two-name manifest cannot authorize a second Undo");
    }

    // B2: two independent groups pass through the SAME production-compiled
    // prepared -> Core -> verified-completed path without creating a new ledger.
    // Both retain an independently resumable synthetic Undo history.
    private static void VerifySuccessiveGroupTransactions(string parent,
        Action<bool, string> check, Action<Action, string> expectThrows)
    {
        string root = Path.Combine(parent, "r217-b2-two-successive-groups");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        GuardRoot(root);
        string? priorJournal = Journal.TestFolderPath;
        try
        {
            Journal.TestFolderPath = Path.Combine(root, "b2-successive-journal");
            string a1 = Path.Combine(root, "first-a.mp4");
            string a2 = Path.Combine(root, "first-b.mp4");
            string b1 = Path.Combine(root, "second-a.mp4");
            string b2 = Path.Combine(root, "second-b.mp4");
            File.WriteAllBytes(a1, RandomNumberGenerator.GetBytes(180 * 1024));
            File.WriteAllBytes(b1, RandomNumberGenerator.GetBytes(220 * 1024));
            foreach (var pair in new[] { (a1, a2), (b1, b2) })
            {
                File.Copy(pair.Item1, pair.Item2);
                File.SetCreationTimeUtc(pair.Item2,
                    File.GetCreationTimeUtc(pair.Item1));
                File.SetLastWriteTimeUtc(pair.Item2,
                    File.GetLastWriteTimeUtc(pair.Item1));
            }
            var groups = Scanner.Run(root, true, null, CancellationToken.None)
                .Groups.OrderBy(g => g.Representative.Length).ToList();
            check(groups.Count == 2 &&
                groups.All(g => g.Files.Count == 2 &&
                    g.BlockReason(false) is null),
                "B2: two separate byte-identical generated groups share one safe transaction plan");
            if (groups.Count != 2)
                throw new InvalidDataException("Missing two-group B2 synthetic fixture");
            string firstId = CompletedGroupLedger.MergeSynthetic(root, groups[0]);
            check(CompletedGroupLedger.Inspect(root).VerifiedCompleted == 1 &&
                Journal.Pending().Count == 0,
                "B2: first durable group committed before starting second");
            string secondId = CompletedGroupLedger.MergeSynthetic(root, groups[1]);
            var both = CompletedGroupLedger.Inspect(root);
            check(firstId != secondId && both.VerifiedCompleted == 2 &&
                both.NeedsReview == 0 &&
                groups.All(g => g.Files.All(s =>
                    NativeFile.SameFile(s.Path, g.Representative.Path))),
                "B2: both groups have distinct durable receipts and correct NTFS link identities");
            // A stale scan must never be accepted as another transaction.
            expectThrows(() => CompletedGroupLedger.MergeSynthetic(root, groups[0]),
                "B2: already consolidated group cannot be authorized by a stale scan");
            check(CompletedGroupLedger.Inspect(root).VerifiedCompleted == 2,
                "B2: rejected stale transaction did not disturb either committed group");

            string evidence = Path.Combine(root, "b2-independent-evidence");
            Directory.CreateDirectory(evidence);
            int undoneFirst = CompletedGroupLedger.ResumeUndoSynthetic(
                root, firstId, evidence);
            int undoneSecond = CompletedGroupLedger.ResumeUndoSynthetic(
                root, secondId, evidence);
            var restored = CompletedGroupLedger.Inspect(root);
            check(undoneFirst == 1 && undoneSecond == 1 &&
                restored.VerifiedRestored == 2 && restored.NeedsReview == 0 &&
                !NativeFile.SameFile(a1, a2) &&
                !NativeFile.SameFile(b1, b2) &&
                groups.All(g => g.Files.All(s =>
                    Hash(s.Path) == g.Hash)),
                "B2: successive groups independently Undo to separate NTFS IDs and exact bytes");
        }
        finally { Journal.TestFolderPath = priorJournal; }
    }

    // B2 G2-04: a new low-space observation at each last-moment safety
    // boundary must never silently delete the original independent data.
    // The prepared record remains a manual-review receipt on failure.
    private static void VerifyMidTransactionCapacityRefusal(string parent,
        Action<bool, string> check, Action<Action, string> expectThrows)
    {
        foreach (int lowAtGate in new[] { 3, 5 })
        {
            string root = Path.Combine(parent, "r217-b2-capacity-gate-" + lowAtGate);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            GuardRoot(root);
            string? oldJournal = Journal.TestFolderPath;
            try
            {
                Journal.TestFolderPath = Path.Combine(root, "part27-journal");
                string a = Path.Combine(root, "capacity-a.mp4");
                string b = Path.Combine(root, "capacity-b.mp4");
                File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(160 * 1024));
                File.Copy(a, b);
                File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
                File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
                var group = Scanner.Run(root, true, null,
                    CancellationToken.None).Groups.Single();
                var originalIds = group.Files.Select(x => x.Identity).ToArray();
                int checks = 0;
                try
                {
                    CapacityGuard.TestOnlyReadAvailable = _ =>
                    {
                        checks++;
                        return checks >= lowAtGate
                            ? (0L, 0L)
                            : (CapacityGuard.TargetReserve + 16 * 1024 * 1024,
                                CapacityGuard.JournalReserve + 16 * 1024 * 1024);
                    };
                    expectThrows(() => CompletedGroupLedger.MergeSynthetic(root, group),
                        "B2 G2-04: refreshed disk/journal low-space reading refuses an unsafe transaction");
                }
                finally { CapacityGuard.TestOnlyReadAvailable = null; }
                var summary = CompletedGroupLedger.Inspect(root);
                string master = Path.Combine(Safety.StorePath(root), group.Hash + ".mp4");
                // Gate 5 has already created a store hardlink to the
                // representative. Its link count must be exactly TWO until
                // the pending journal is explicitly reviewed; the duplicate
                // must regain its original independent ID and single link.
                bool originalBytesAndIds = group.Files.Select((x, i) =>
                    File.Exists(x.Path) &&
                    NativeFile.GetIdentity(x.Path).SameFile(originalIds[i]) &&
                    NativeFile.GetIdentity(x.Path).LinkCount ==
                        (lowAtGate == 5 && x.Path == group.Representative.Path ? 2u : 1u) &&
                    Hash(x.Path) == group.Hash).All(x => x);
                bool masterCorrect = lowAtGate == 3
                    ? !File.Exists(master) && Journal.Pending().Count == 0
                    : File.Exists(master) && Journal.Pending().Count == 1 &&
                      NativeFile.SameFile(master, group.Representative.Path) &&
                      !NativeFile.SameFile(master, group.Files[1].Path);
                if (checks != lowAtGate || !originalBytesAndIds || !masterCorrect ||
                    summary.NeedsReview != 1 || summary.VerifiedCompleted != 0)
                    Console.WriteLine("B2 G2-04 DIAGNOSTIC: gate=" + lowAtGate +
                        " actualChecks=" + checks + " originals=" + originalBytesAndIds +
                        " masterCorrect=" + masterCorrect + " review=" + summary.NeedsReview +
                        " completed=" + summary.VerifiedCompleted +
                        " pending=" + Journal.Pending().Count);
                check(checks == lowAtGate && originalBytesAndIds &&
                    summary.NeedsReview == 1 && summary.VerifiedCompleted == 0 &&
                    masterCorrect,
                    "B2 G2-04: refreshed low-space check at gate " + lowAtGate +
                    " preserves original independent file IDs/bytes and retains exact review evidence");
            }
            finally
            {
                CapacityGuard.TestOnlyReadAvailable = null;
                Journal.TestFolderPath = oldJournal;
            }
        }
    }

    // B2 G2-05/G2-06: reuse the REAL Core + child-process kill for every
    // durable journal/cutover boundary, and both sides of group completion.
    // A separate generated root and journal isolate every crash scenario.
    private sealed record PartialAllocationEvidence(string Checkpoint,
        long AllocatedBeforeBytes, long AllocatedAfterBytes,
        long PhysicalAllocatedDeltaBytes, long VerifiedReceiptLogicalBytes,
        int UniqueIdsBefore, int UniqueIdsAfter);

    private static IReadOnlyList<PartialAllocationEvidence> VerifyKilledMultiGroupBatch(
        string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        var evidence = new List<PartialAllocationEvidence>();
        foreach (string checkpoint in BatchCrashCheckpoints)
        {
            string root = Path.Combine(parent, "r217-b2-kill-" + checkpoint);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            GuardRoot(root);
            string? previousJournal = Journal.TestFolderPath;
            try
            {
                Journal.TestFolderPath = Path.Combine(root, "part27-journal");
                for (int g = 0; g < 3; g++)
                {
                    string first = Path.Combine(root, $"group-{g}-a.mp4");
                    File.WriteAllBytes(first, RandomNumberGenerator.GetBytes(
                        (g + 2) * 64 * 1024));
                    int count = g == 1 ? 3 : 2;
                    for (int i = 1; i < count; i++)
                    {
                        string alias = Path.Combine(root,
                            $"group-{g}-{(char)('a' + i)}.mp4");
                        File.Copy(first, alias);
                        File.SetCreationTimeUtc(alias, File.GetCreationTimeUtc(first));
                        File.SetLastWriteTimeUtc(alias, File.GetLastWriteTimeUtc(first));
                    }
                }
                var groups = Scanner.Run(root, true, null, CancellationToken.None)
                    .Groups.OrderBy(x => x.Representative.Length).ToList();
                check(groups.Count == 3 && groups[0].Files.Count == 2 &&
                    groups[1].Files.Count == 3 && groups[2].Files.Count == 2 &&
                    groups.All(x => x.BlockReason(false) is null),
                    "B2: independent 2/3/2 generated groups before kill at " + checkpoint);
                if (groups.Count != 3)
                    throw new InvalidDataException("B2 crash matrix fixture is incomplete");
                var original = groups.SelectMany(g => g.Files).ToDictionary(
                    x => x.Path, x => x.Identity, StringComparer.OrdinalIgnoreCase);
                string[] sourceNames = groups.SelectMany(g =>
                    g.Files.Select(f => f.Path)).ToArray();
                var baselineAllocation = UniqueAllocated(sourceNames);
                check(baselineAllocation.UniqueIds == 7 &&
                    baselineAllocation.Bytes >=
                        groups.Sum(g => g.Files.Sum(f => f.Length)),
                    "B2 G2-06: seven independent original NTFS IDs have allocated bytes before real Core mutation at " +
                        checkpoint);
                var firstGroup = groups[0];
                var second = groups[1];
                var third = groups[2];
                string firstMaster = Path.Combine(Safety.StorePath(root),
                    firstGroup.Hash + ".mp4");
                string secondMaster = Path.Combine(Safety.StorePath(root),
                    second.Hash + ".mp4");
                string thirdMaster = Path.Combine(Safety.StorePath(root),
                    third.Hash + ".mp4");
                string signal = Path.Combine(root, "b2-merge-killed.signal");
                int exit = KillWorker(root, "merge-batch:" + checkpoint, signal);

                bool afterAllAliases = checkpoint is
                    "group-core-verified" or "group-completed";
                bool completedSecond = checkpoint == "group-completed";
                bool aliasLinked = checkpoint is "new-link-created" or
                    "pre-backup-delete" or "backup-deleted" ||
                    afterAllAliases;
                bool backupExpected = checkpoint is "backup-renamed" or
                    "new-link-created" or "pre-backup-delete";
                bool pendingExpected = !afterAllAliases;
                uint secondLinks = afterAllAliases ? 4u :
                    aliasLinked ? 3u : 2u;

                var summary = CompletedGroupLedger.Inspect(root);
                var journals = Journal.Pending();
                // The observed storage after a killed worker includes the
                // relocated original in a pending backup, if present. A
                // committed link's original file ID no longer consumes space.
                string[] masters = [firstMaster, secondMaster, thirdMaster];
                var allocated = UniqueAllocated(sourceNames.Concat(masters)
                    .Concat(journals.Select(j => j.BackupPath)));
                long measuredFreed = baselineAllocation.Bytes - allocated.Bytes;
                bool afterOneAliasDeleted = checkpoint == "backup-deleted";
                int expectedRemainingIds = afterAllAliases ? 4 :
                    afterOneAliasDeleted ? 5 : 6;
                long expectedFreed = firstGroup.Files[1].Length +
                    (afterAllAliases ? second.Savings :
                        afterOneAliasDeleted ? second.Files[1].Length : 0);
                // Source data lengths are integral NTFS clusters in these
                // synthetic fixtures; exact unique-ID allocation must match
                // the physical, not just logical, released bytes.
                check(allocated.UniqueIds == expectedRemainingIds &&
                    measuredFreed == expectedFreed &&
                    allocated.Bytes > 0,
                    "B2 G2-06: real Core partial crash at " + checkpoint +
                        " has exact distinct-ID NTFS allocation delta; recovered=" +
                        measuredFreed + ", verified-receipts-only=" +
                        firstGroup.Files[1].Length +
                        (completedSecond ? second.Savings : 0));
                evidence.Add(new PartialAllocationEvidence(checkpoint,
                    baselineAllocation.Bytes, allocated.Bytes, measuredFreed,
                    firstGroup.Savings + (completedSecond ? second.Savings : 0),
                    baselineAllocation.UniqueIds, allocated.UniqueIds));
                bool firstCommitted = File.Exists(firstMaster) &&
                    NativeFile.GetIdentity(firstMaster).LinkCount == 3 &&
                    firstGroup.Files.All(file =>
                        File.Exists(file.Path) &&
                        NativeFile.SameFile(file.Path, firstMaster) &&
                        Hash(file.Path) == firstGroup.Hash);
                bool secondMasterValid = File.Exists(secondMaster) &&
                    NativeFile.GetIdentity(secondMaster).LinkCount == secondLinks &&
                    NativeFile.GetIdentity(second.Representative.Path)
                        .SameFile(original[second.Representative.Path]) &&
                    NativeFile.SameFile(second.Representative.Path, secondMaster) &&
                    Hash(second.Representative.Path) == second.Hash;
                FileSnapshot firstAlias = second.Files[1];
                FileSnapshot lastAlias = second.Files[2];
                bool firstAliasValid = checkpoint == "backup-renamed"
                    ? !File.Exists(firstAlias.Path)
                    : File.Exists(firstAlias.Path) &&
                      Hash(firstAlias.Path) == second.Hash &&
                      (aliasLinked
                          ? NativeFile.SameFile(firstAlias.Path, secondMaster)
                          : NativeFile.GetIdentity(firstAlias.Path)
                                .SameFile(original[firstAlias.Path]));
                bool lastAliasValid = File.Exists(lastAlias.Path) &&
                    Hash(lastAlias.Path) == second.Hash &&
                    (afterAllAliases
                        ? NativeFile.SameFile(lastAlias.Path, secondMaster)
                        : NativeFile.GetIdentity(lastAlias.Path)
                            .SameFile(original[lastAlias.Path]));
                bool journalValid = pendingExpected
                    ? journals.Count == 1 &&
                      journals[0].OriginalPath.Equals(firstAlias.Path,
                          StringComparison.OrdinalIgnoreCase) &&
                      journals[0].IndependentIdentity is { } identity &&
                      identity.SameFile(original[firstAlias.Path]) &&
                      (backupExpected
                          ? File.Exists(journals[0].BackupPath) &&
                            NativeFile.GetIdentity(journals[0].BackupPath)
                                .SameFile(original[firstAlias.Path]) &&
                            Hash(journals[0].BackupPath) == second.Hash
                          : !File.Exists(journals[0].BackupPath))
                    : journals.Count == 0;
                bool historyValid = summary.Entries.Count == 2 &&
                    summary.VerifiedCompleted == (completedSecond ? 2 : 1) &&
                    summary.NeedsReview == (completedSecond ? 0 : 1) &&
                    summary.Entries.Any(entry =>
                        entry.State == "VerifiedCompleted" &&
                        entry.AliasCount == 1) &&
                    (completedSecond
                        ? summary.Entries.Any(entry =>
                            entry.State == "VerifiedCompleted" &&
                            entry.AliasCount == 2)
                        : summary.Entries.Any(entry =>
                            entry.State == "ManualReview"));
                bool thirdUnchanged = !File.Exists(thirdMaster) &&
                    third.Files.All(file => File.Exists(file.Path) &&
                        NativeFile.GetIdentity(file.Path)
                            .SameFile(original[file.Path]) &&
                        NativeFile.GetIdentity(file.Path).LinkCount == 1 &&
                        Hash(file.Path) == third.Hash);
                bool killed = exit != 0 &&
                    File.Exists(signal) && File.ReadAllText(signal) ==
                        "second-group-" + checkpoint;
                check(killed && historyValid && firstCommitted &&
                      secondMasterValid && firstAliasValid && lastAliasValid &&
                      journalValid && thirdUnchanged &&
                      Journal.UnresolvedRecordFiles().Count == 0,
                    "B2 G2-05/06: REAL child kill at " + checkpoint +
                    " preserves receipt classification, original NTFS IDs and untouched third group");
                if (!(killed && historyValid && firstCommitted &&
                      secondMasterValid && firstAliasValid && lastAliasValid &&
                      journalValid && thirdUnchanged))
                    throw new InvalidDataException("B2 generated crash state invalid at " +
                        checkpoint + ": history=" + historyValid +
                        " master=" + secondMasterValid + " alias=" +
                        firstAliasValid + "/" + lastAliasValid + " journal=" +
                        journalValid + " third=" + thirdUnchanged);

                // Only completed groups may count towards verified logical
                // savings; an incomplete alias is not a success receipt.
                long verifiedSavings = firstGroup.Savings +
                    (completedSecond ? second.Savings : 0);
                long expectedVerifiedSavings = completedSecond ?
                    firstGroup.Files[1].Length + second.Files[1].Length +
                        second.Files[2].Length : firstGroup.Files[1].Length;
                check(verifiedSavings == expectedVerifiedSavings &&
                    NativeFile.GetIdentity(firstMaster).LinkCount == 3 &&
                    NativeFile.GetIdentity(secondMaster).LinkCount == secondLinks,
                    "B2 G2-06: verified group receipt count and logical savings exclude partial uncommitted aliases at " +
                        checkpoint);

                string before = string.Join("|", summary.Entries
                    .OrderBy(x => x.OperationId)
                    .Select(x => x.OperationId + ":" + x.State));
                var again = CompletedGroupLedger.Inspect(root);
                check(before == string.Join("|", again.Entries
                        .OrderBy(x => x.OperationId)
                        .Select(x => x.OperationId + ":" + x.State)) &&
                    journals.Count == Journal.Pending().Count &&
                    third.Files.All(file =>
                        NativeFile.GetIdentity(file.Path)
                            .SameFile(original[file.Path])),
                    "B2 G2-05: repeated restart-equivalent inspection never adopts partial evidence or modifies later originals at " +
                        checkpoint);

                if (!completedSecond)
                {
                    expectThrows(() => CompletedGroupLedger.MergeVerifiedGroups(
                        root, [third], CancellationToken.None),
                        "B2 G2-06: incomplete prior group blocks the next group at " +
                            checkpoint);
                    check(!File.Exists(thirdMaster) &&
                        CompletedGroupLedger.Inspect(root).NeedsReview == 1 &&
                        third.Files.All(file =>
                            NativeFile.GetIdentity(file.Path)
                                .SameFile(original[file.Path])),
                        "B2 G2-06: refused continuation preserves all subsequent independent IDs at " +
                            checkpoint);
                }
                else
                {
                    // The earlier transaction really completed. Resuming
                    // untouched third group requires a NEW explicit call.
                    var receipts = CompletedGroupLedger.MergeVerifiedGroups(
                        root, [third], CancellationToken.None);
                    check(receipts.Count == 1 &&
                        CompletedGroupLedger.Inspect(root).VerifiedCompleted == 3 &&
                        third.Files.All(file =>
                            NativeFile.SameFile(file.Path, thirdMaster) &&
                            Hash(file.Path) == third.Hash),
                        "B2 G2-06: after a fully committed crash, an explicit subsequent invocation can safely complete the untouched group");
                }
            }
            finally { Journal.TestFolderPath = previousJournal; }
        }
        return evidence;
    }

    // An ordinary, deterministic group-2 source swap must stop a batch too:
    // the successful prior group remains committed and group 3 stays intact.
    private static void VerifyBatchSourceSwapIsolation(string parent,
        Action<bool, string> check, Action<Action, string> expectThrows)
    {
        string root = Path.Combine(parent, "r217-b2-stale-middle-group");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        GuardRoot(root);
        string? previousJournal = Journal.TestFolderPath;
        try
        {
            Journal.TestFolderPath = Path.Combine(root, "part27-journal");
            for (int g = 0; g < 3; g++)
            {
                string first = Path.Combine(root, $"group-{g}-a.mp4");
                File.WriteAllBytes(first,
                    RandomNumberGenerator.GetBytes((g + 2) * 64 * 1024));
                string alias = Path.Combine(root, $"group-{g}-b.mp4");
                File.Copy(first, alias);
                File.SetCreationTimeUtc(alias, File.GetCreationTimeUtc(first));
                File.SetLastWriteTimeUtc(alias, File.GetLastWriteTimeUtc(first));
            }
            var groups = Scanner.Run(root, true, null, CancellationToken.None)
                .Groups.OrderBy(g => g.Representative.Length).ToList();
            if (groups.Count != 3) throw new InvalidDataException(
                "B2 independent failure fixture incomplete");
            var thirdIds = groups[2].Files.Select(x => x.Identity).ToArray();
            var altered = groups[1].Files[1];
            string moved = Path.Combine(root, "moved-middle-original.mp4");
            File.Move(altered.Path, moved);
            File.Copy(moved, altered.Path);
            File.SetCreationTimeUtc(altered.Path, File.GetCreationTimeUtc(moved));
            File.SetLastWriteTimeUtc(altered.Path, File.GetLastWriteTimeUtc(moved));
            check(Hash(altered.Path) == groups[1].Hash &&
                !NativeFile.GetIdentity(altered.Path).SameFile(altered.Identity),
                "B2 G2-06: same-byte independent source identity changes between scan and group transaction");
            expectThrows(() => CompletedGroupLedger.MergeVerifiedGroups(
                root, groups, CancellationToken.None),
                "B2 G2-06: middle group must reject a stale exact-hash foreign NTFS identity");
            var summary = CompletedGroupLedger.Inspect(root);
            string firstMaster = Path.Combine(Safety.StorePath(root),
                groups[0].Hash + ".mp4");
            string middleMaster = Path.Combine(Safety.StorePath(root),
                groups[1].Hash + ".mp4");
            string lastMaster = Path.Combine(Safety.StorePath(root),
                groups[2].Hash + ".mp4");
            check(summary.VerifiedCompleted == 1 && summary.NeedsReview == 0 &&
                NativeFile.SameFile(groups[0].Files[0].Path, firstMaster) &&
                NativeFile.SameFile(groups[0].Files[1].Path, firstMaster) &&
                !File.Exists(middleMaster) && !File.Exists(lastMaster) &&
                NativeFile.GetIdentity(moved).SameFile(altered.Identity) &&
                Hash(moved) == groups[1].Hash &&
                groups[2].Files.Select((file, i) =>
                    NativeFile.GetIdentity(file.Path)
                        .SameFile(thirdIds[i]) &&
                    NativeFile.GetIdentity(file.Path).LinkCount == 1 &&
                    Hash(file.Path) == groups[2].Hash).All(x => x) &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0,
                "B2 G2-06: failed middle group cannot change verified first group, foreign original, or unstarted third group");
        }
        finally { Journal.TestFolderPath = previousJournal; }
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? oldJournal = Journal.TestFolderPath;
        try
        {
            string root = Path.Combine(parent, "r217-part27-four-name-group");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            GuardRoot(root);
            Journal.TestFolderPath = Path.Combine(root, "part27-journal");
            string original = Path.Combine(root, "media-a.mp4");
            File.WriteAllBytes(original, RandomNumberGenerator.GetBytes(384 * 1024));
            for (int i = 1; i <= 3; i++)
            {
                string destination = Path.Combine(root, $"media-{(char)('a' + i)}.mp4");
                File.Copy(original, destination);
                File.SetCreationTimeUtc(destination, File.GetCreationTimeUtc(original));
                File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(original));
            }

            var scan = Scanner.Run(root, true, null, CancellationToken.None);
            check(scan.FileCount == 4 && scan.Groups.Count == 1 &&
                  scan.Groups[0].Files.Count == 4 &&
                  scan.Groups[0].BlockReason(false) is null,
                "R2.17 part27: real scanner discovers four independent, identical generated MP4 files");
            var before = scan.Groups.Single().Files.ToDictionary(s => s.Path,
                StringComparer.OrdinalIgnoreCase);
            string groupHash = scan.Groups.Single().Hash;
            var empty = CompletedGroupLedger.Inspect(root);
            check(empty.Entries.Count == 0 && !Directory.Exists(Safety.StorePath(root)),
                "R2.17 part27: read-only group inspection creates no files or store");

            string id = CompletedGroupLedger.MergeSynthetic(root, scan.Groups.Single());
            var completed = CompletedGroupLedger.Inspect(root);
            var originalRecords = CompletedGroupLedger.InspectOriginals(root);
            check(originalRecords.Count == 4 &&
                originalRecords.Count(e => e.Representative) == 1 &&
                originalRecords.Select(e => e.OperationId).Distinct().Single() == id &&
                originalRecords.Select(e => (e.OriginalVolumeSerial,
                    e.OriginalFileIndex)).Distinct().Count() == 4 &&
                originalRecords.All(e => e.State == "VerifiedCompleted" &&
                    e.OriginalLinks == 1 && e.Sha256 == groupHash &&
                    e.OwnerDaclFingerprint.Length == 64 &&
                    before.TryGetValue(e.Path, out var source) &&
                    source.Identity.FileIndex == e.OriginalFileIndex &&
                    source.Identity.VolumeSerial == e.OriginalVolumeSerial &&
                    source.CreationUtc == e.CreationUtc &&
                    source.ModifiedUtc == e.ModifiedUtc &&
                    source.Attributes == e.Attributes &&
                    source.Length == e.Length),
                "B2 G2-02: exact four original NTFS IDs, SHA, timestamps, ACL fingerprints and operation ID survive as read-only group history");
            var overview = RecoveryEvidenceOverview.Inspect(root);
            check(overview.Rows.Count(e =>
                    e.Source == "グループ元ファイルの個別履歴" &&
                    !e.NeedsAttention && e.State == "VerifiedCompleted") == 4 &&
                overview.Rows.All(e => e.Source != "グループ元ファイルの個別履歴" ||
                    e.Explanation.Contains("元NTFS ID=", StringComparison.Ordinal)),
                "B2 G2-02: cross-ledger view exposes every verified original ID and per-file metadata as read-only WPF evidence");
            string master = Path.Combine(Safety.StorePath(root), groupHash + ".mp4");
            bool merged = completed.VerifiedCompleted == 1 &&
                completed.Entries.Single().AliasCount == 3 &&
                completed.Entries.Single().DetachedCount == 0 &&
                NativeFile.GetIdentity(master).LinkCount == 5 &&
                scan.Groups.Single().Files.All(s =>
                    NativeFile.SameFile(s.Path, master) &&
                    Hash(s.Path) == groupHash) &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0;
            if (!merged)
                Console.WriteLine("PART27 GENERATED DIAGNOSTIC: entries=" +
                    string.Join(",", completed.Entries.Select(e =>
                        e.State + "/" + e.AliasCount + "/" + e.DetachedCount)) +
                    ", master links=" + NativeFile.GetIdentity(master).LinkCount +
                    ", journals=" + Journal.Pending().Count +
                    ", allAliasesShared=" + scan.Groups.Single().Files.All(s =>
                        NativeFile.SameFile(s.Path, master) && Hash(s.Path) == groupHash));
            check(merged,
                "R2.17 part27: real Core consolidates four generated files with durable group-wide original metadata");
            // G3-01: a completely fresh process, NOT the live scanner object,
            // must read every original ID/metadata from the durable receipt.
            VerifyRestartUndoReview(root, id, before, 0, check);
            var goodReview = CompletedGroupLedger.InspectUndoCandidates(root)
                .Single(x => x.OperationId == id);
            check(goodReview.ReviewReady && goodReview.OriginalFiles.Count == 4 &&
                goodReview.RemainingAliases == 3 &&
                !goodReview.ProductionUndoAuthorized,
                "G3-01: normal-built read-only Undo review identifies a full completed group without authorizing a write");

            // G3-04 preflight ONLY: refuse to claim the Undo is ready when
            // the target or the journal volume cannot cover restore bytes.
            var originalCapacityHook = CapacityGuard.TestOnlyReadAvailable;
            try
            {
                CapacityGuard.TestOnlyReadAvailable = _ =>
                    (goodReview.RequiredAdditionalBytes - 1,
                        CapacityGuard.JournalReserve + 64);
                var lowTarget = CompletedGroupLedger.InspectUndoCandidates(root)
                    .Single(x => x.OperationId == id);
                check(!lowTarget.ReviewReady && !lowTarget.CapacityEnough &&
                    lowTarget.RequiredAdditionalBytes == goodReview.RequiredAdditionalBytes,
                    "G3-04: preview refuses a one-byte-short NTFS target volume without touching media");
                CapacityGuard.TestOnlyReadAvailable = _ =>
                    (goodReview.RequiredAdditionalBytes + 64,
                        CapacityGuard.JournalReserve - 1);
                var lowJournal = CompletedGroupLedger.InspectUndoCandidates(root)
                    .Single(x => x.OperationId == id);
                check(!lowJournal.ReviewReady && !lowJournal.CapacityEnough,
                    "G3-04: preview refuses insufficient journal drive even with target capacity available");
            }
            finally { CapacityGuard.TestOnlyReadAvailable = originalCapacityHook; }

            string foreign = Path.Combine(root, "part27-known-extra-hardlink.tmp");
            NativeFile.NewHardLink(foreign, master);
            var suspicious = CompletedGroupLedger.Inspect(root);
            check(suspicious.NeedsReview == 1 &&
                  suspicious.VerifiedCompleted == 0 &&
                  NativeFile.GetIdentity(master).LinkCount == 6,
                "R2.17 part27: unrecorded extra NTFS hardlink makes whole group untrusted");
            check(CompletedGroupLedger.InspectOriginals(root).Count == 0,
                "B2 G2-02: unknown external hardlink hides every formerly verified original from trusted WPF history");
            check(CompletedGroupLedger.InspectUndoCandidates(root).Count == 0,
                "G3-01/04: unknown external hardlink never yields a trusted Undo candidate");
            check(RecoveryEvidenceOverview.Inspect(root).Rows.All(e =>
                    e.Source != "グループ元ファイルの個別履歴"),
                "B2 G2-02: untrusted shared-link history cannot leak unverified source records to UI");
            string evidence = Path.Combine(root, "part27-undo-evidence");
            Directory.CreateDirectory(evidence);
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
                "R2.17 part27: external alias forbids group Undo before mutation");
            check(before.Keys.All(path => NativeFile.SameFile(path, master) &&
                  Hash(path) == groupHash),
                "R2.17 part27: unsafe Undo refusal preserves all shared originals");
            if (!NativeFile.SameFile(foreign, master) || Hash(foreign) != groupHash)
                throw new InvalidDataException("Test-owned extra link changed before explicit cleanup");
            File.Delete(foreign);
            check(CompletedGroupLedger.Inspect(root).VerifiedCompleted == 1,
                "R2.17 part27: known generated extra-link cleanup restores exact trusted group evidence");

            string signal = Path.Combine(root, "part27-killed.checkpoint");
            int exit = KillWorker(root, id, signal);
            var paused = CompletedGroupLedger.Inspect(root);
            bool realKill = exit != 0 && File.Exists(signal) &&
                File.ReadAllText(signal) == "first-alias-checkpoint-durable" &&
                paused.ExplicitResumeRequired == 1 &&
                paused.Entries.Single().DetachedCount == 1 &&
                NativeFile.GetIdentity(master).LinkCount == 4 &&
                Journal.Pending().Count == 0 &&
                Journal.UnresolvedRecordFiles().Count == 0;
            check(realKill,
                "R2.17 part27: actual child kill after durable first Undo checkpoint leaves recoverable partial group");
            VerifyRestartUndoReview(root, id, before, 1, check);
            int recovered = CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence);
            var restored = CompletedGroupLedger.Inspect(root);
            var persistedAfterUndo = CompletedGroupLedger.InspectOriginals(root);
            check(persistedAfterUndo.Count == 4 &&
                persistedAfterUndo.All(e => e.State == "VerifiedRestored" &&
                    before.TryGetValue(e.Path, out var source) &&
                    source.Identity.SameFile(new FileIdentity(
                        e.OriginalVolumeSerial, e.OriginalFileIndex,
                        e.OriginalLinks))),
                "B2 G2-02: read-only original identity records survive complete independent Undo");
            bool originals = restored.VerifiedRestored == 1 &&
                restored.VerifiedCompleted == 0 && restored.NeedsReview == 0 &&
                restored.Entries.Single().DetachedCount == 3 &&
                NativeFile.GetIdentity(master).LinkCount == 2 &&
                Hash(master) == groupHash;
            foreach (var originalSnapshot in before.Values)
            {
                var now = Safety.Capture(originalSnapshot.Path);
                originals &= now.Identity.LinkCount == (originalSnapshot.Path ==
                    scan.Groups.Single().Representative.Path ? 2u : 1u) &&
                    now.Length == originalSnapshot.Length &&
                    now.CreationUtc == originalSnapshot.CreationUtc &&
                    now.ModifiedUtc == originalSnapshot.ModifiedUtc &&
                    now.Attributes == originalSnapshot.Attributes &&
                    now.SecurityDescriptor.AsSpan().SequenceEqual(
                        originalSnapshot.SecurityDescriptor) &&
                    Hash(now.Path) == groupHash;
            }
            check(recovered == 3 && originals,
                "R2.17 part27: explicit restart completes remaining two Undo aliases; all original bytes/metadata and separate NTFS identities verified");
            check(CompletedGroupLedger.InspectUndoCandidates(root).Count == 0,
                "G3-01: fully restored group does not offer an already-complete Undo again");
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
                "R2.17 part27: final restored group cannot be replayed as another Undo");

            VerifyTwoNameUnifiedLedger(parent, check, expectThrows);
            VerifyPreparedGroupPromotion(parent, check, expectThrows);
            VerifySuccessiveGroupTransactions(parent, check, expectThrows);
            VerifyMidTransactionCapacityRefusal(parent, check, expectThrows);
            var allocationMatrix = VerifyKilledMultiGroupBatch(parent, check, expectThrows);
            VerifyBatchSourceSwapIsolation(parent, check, expectThrows);
            // The helper uses a separately guarded generated journal folder.
            Journal.TestFolderPath = Path.Combine(root, "part27-journal");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-group-ledger-resume.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part27-group-ledger-v1",
                    generatedOnly = true,
                    sourceFileCount = 4,
                    realCoreGroupMerged = merged,
                    savedGroupLedgerVerifiedAfterRestart = completed.VerifiedCompleted == 1,
                    unexpectedExtraLinkRefusedBeforeUndo = suspicious.NeedsReview == 1,
                    actualUndoWorkerProcessKilled = realKill,
                    durableDetachedAliasesAtInterruption = paused.Entries.Single().DetachedCount,
                    explicitResumeFinalDetachedAliases = recovered,
                    allBytesTimesOwnerDaclAndNtfsIdsVerified = originals,
                    groupOriginalRecordsReadOnlyVerified = originalRecords.Count == 4 &&
                        persistedAfterUndo.Count == 4,
                    untrustedGroupOriginalRecordsDenied = true,
                    completedHistoryRestored = restored.VerifiedRestored == 1,
                    g3RestartedProcessOriginalGroupFullIdProof = true,
                    g3RestartedProcessPartialUndoRemainingAliasProof = true,
                    g3LowTargetAndJournalSpaceReadOnlyRefusal = true,
                    g3UnknownLinkAndRestoredGroupNoUndoCandidate = true,
                    actualCoreKillCheckpointCount = BatchCrashCheckpoints.Length,
                    actualCoreKillCheckpoints = BatchCrashCheckpoints,
                    actualCorePartialAllocationMatrix = allocationMatrix,
                    actualCoreDistinctNtfsIdAllocationMeasuredAtEveryKill = true,
                    partialFailedGroupAllocationNeverCountedAsVerifiedReceipt = true,
                    repeatedCrashInspectionAndGroupIsolationTested = true,
                    middleGroupSameShaForeignIdRefused = true,
                    productionMergeEnabled = false,
                    productionUndoEnabled = CompletedGroupLedger.ProductionMutationEnabled,
                    actualUserFilesTouched = false,
                    userCapacityFreedBytes = 0,
                    insideAliasCrashRecoveryProven = false,
                    powerCutDurabilityProven = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART27 synthetic four-name group manifest and killed-worker Undo resume PASS");
        }
        finally { Journal.TestFolderPath = oldJournal; }
    }
}
