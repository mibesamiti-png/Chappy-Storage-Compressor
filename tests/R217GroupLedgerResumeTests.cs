using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part27: real Core four-name synthetic merge, group-level persisted history,
// actual killed independent-Undo worker AFTER its first durable alias checkpoint.
internal static class R217GroupLedgerResumeTests
{
    private const string Flag = "--r217-part27-generated-group-undo-kill";
    private const string Guard = "CHAPPY_PART27_GROUP_UNDO_GENERATED_ONLY";
    private static string Hash(string path) => Safety.ComputeSha256(path, CancellationToken.None);

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
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            !Guid.TryParseExact(args[2], "N", out _))
            throw new InvalidOperationException("Part27 worker not authorized");
        string root = Path.GetFullPath(args[1]);
        GuardRoot(root);
        string signal = Path.GetFullPath(args[3]);
        if (!signal.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Part27 checkpoint outside generated root");
        Journal.TestFolderPath = Path.Combine(root, "part27-journal");
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

            string foreign = Path.Combine(root, "part27-known-extra-hardlink.tmp");
            NativeFile.NewHardLink(foreign, master);
            var suspicious = CompletedGroupLedger.Inspect(root);
            check(suspicious.NeedsReview == 1 &&
                  suspicious.VerifiedCompleted == 0 &&
                  NativeFile.GetIdentity(master).LinkCount == 6,
                "R2.17 part27: unrecorded extra NTFS hardlink makes whole group untrusted");
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
            int recovered = CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence);
            var restored = CompletedGroupLedger.Inspect(root);
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
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root, id, evidence),
                "R2.17 part27: final restored group cannot be replayed as another Undo");

            VerifyTwoNameUnifiedLedger(parent, check, expectThrows);
            VerifyPreparedGroupPromotion(parent, check, expectThrows);
            VerifySuccessiveGroupTransactions(parent, check, expectThrows);
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
                    completedHistoryRestored = restored.VerifiedRestored == 1,
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
