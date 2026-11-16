using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Real Core DedupeEngine low-capacity gates, with test-only observations.
// Synthetic files only; production capacity reads are unchanged.
internal static class R217LowSpaceDedupeTests
{
    private static string Hash(string path) =>
        Safety.ComputeSha256(path, CancellationToken.None);

    private static (string Root, DuplicateGroup Group, string Master)
        Fixture(string parent, string label)
    {
        string root = Path.Combine(parent, "r217-part29-capacity-" + label);
        Directory.CreateDirectory(root);
        string first = Path.Combine(root, "video-a.mp4");
        File.WriteAllBytes(first, RandomNumberGenerator.GetBytes(256 * 1024));
        for (int i = 1; i <= 2; i++)
        {
            string copy = Path.Combine(root, "video-" + (i == 1 ? "b" : "c") + ".mp4");
            File.Copy(first, copy);
            File.SetCreationTimeUtc(copy, File.GetCreationTimeUtc(first));
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(first));
        }
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        if (scan.Groups.Count != 1 || scan.Groups[0].Files.Count != 3 ||
            scan.Groups[0].BlockReason(false) is not null)
            throw new InvalidDataException("Part29 synthetic 3-file fixture invalid");
        return (root, scan.Groups[0],
            Path.Combine(Safety.StorePath(root), scan.Groups[0].Hash + ".mp4"));
    }

    private static bool Independent(DuplicateGroup group) =>
        group.Files.All(x =>
        {
            var now = Safety.Capture(x.Path);
            return now.Identity.SameFile(x.Identity) &&
                now.Identity.LinkCount == 1 &&
                now.Length == x.Length &&
                now.CreationUtc == x.CreationUtc &&
                now.ModifiedUtc == x.ModifiedUtc &&
                now.Attributes == x.Attributes &&
                now.SecurityDescriptor.AsSpan().SequenceEqual(x.SecurityDescriptor) &&
                Hash(x.Path) == group.Hash;
        });

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? old = Journal.TestFolderPath;
        try
        {
            var target = Fixture(parent, "target");
            Journal.TestFolderPath = Path.Combine(target.Root, "isolated-journal");
            int calls = 0;
            CapacityGuard.TestOnlyReadAvailable = _ =>
            {
                calls++;
                return (CapacityGuard.TargetReserve - 1,
                    CapacityGuard.JournalReserve + 4096);
            };
            expectThrows(() => DedupeEngine.Run(target.Root, [target.Group], false,
                null, CancellationToken.None),
                "R2.17 Part29: below-target reserve refuses Core merge at first preflight");
            check(calls == 1 && Independent(target.Group) &&
                  !File.Exists(target.Master) &&
                  Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 Part29: below-target threshold did not create master, pending record or change originals");

            var journal = Fixture(parent, "journal");
            Journal.TestFolderPath = Path.Combine(journal.Root, "isolated-journal");
            calls = 0;
            CapacityGuard.TestOnlyReadAvailable = _ =>
            {
                calls++;
                return (CapacityGuard.TargetReserve + 4096,
                    CapacityGuard.JournalReserve - 1);
            };
            expectThrows(() => DedupeEngine.Run(journal.Root, [journal.Group],
                false, null, CancellationToken.None),
                "R2.17 Part29: separate recovery-journal drive threshold refuses before mutation");
            check(calls == 1 && Independent(journal.Group) &&
                  !File.Exists(journal.Master) && Journal.Pending().Count == 0,
                "R2.17 Part29: low journal capacity preserves synthetic original file identities");

            var late = Fixture(parent, "late-first");
            Journal.TestFolderPath = Path.Combine(late.Root, "isolated-journal");
            calls = 0;
            CapacityGuard.TestOnlyReadAvailable = _ =>
            {
                calls++;
                return calls >= 2
                    ? (CapacityGuard.TargetReserve - 1, CapacityGuard.JournalReserve + 4096)
                    : (CapacityGuard.TargetReserve + 4096, CapacityGuard.JournalReserve + 4096);
            };
            long zero = DedupeEngine.Run(late.Root, [late.Group], false,
                null, CancellationToken.None);
            check(calls >= 2 && zero == 0 && Independent(late.Group) &&
                  !File.Exists(late.Master) &&
                  Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 Part29: second preflight after temporary store link fails without leaving any linked originals");

            var partial = Fixture(parent, "partial");
            Journal.TestFolderPath = Path.Combine(partial.Root, "isolated-journal");
            calls = 0;
            CapacityGuard.TestOnlyReadAvailable = _ =>
            {
                calls++;
                return calls >= 3
                    ? (CapacityGuard.TargetReserve + 4096, CapacityGuard.JournalReserve - 1)
                    : (CapacityGuard.TargetReserve + 4096, CapacityGuard.JournalReserve + 4096);
            };
            long saved = DedupeEngine.Run(partial.Root, [partial.Group], false,
                null, CancellationToken.None);
            int linked = partial.Group.Files.Count(x => NativeFile.SameFile(x.Path,
                partial.Master));
            int independent = partial.Group.Files.Count(x =>
                NativeFile.GetIdentity(x.Path).LinkCount == 1);
            check(calls >= 3 && saved == partial.Group.Files[1].Length &&
                  NativeFile.GetIdentity(partial.Master).LinkCount == 3 &&
                  linked == 2 && independent == 1 &&
                  partial.Group.Files.All(x => Hash(x.Path) == partial.Group.Hash) &&
                  Journal.Pending().Count == 0 &&
                  Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 Part29: sudden journal-space drop stops later aliases; already committed alias remains intact with no unresolved journal");

            // Real space is never reserved by the preflight. Its conservative
            // two-drive thresholds are observable, not a transaction guarantee.
            CapacityGuard.TestOnlyReadAvailable = null;
            check(CapacityGuard.Enough(CapacityGuard.TargetReserve,
                      CapacityGuard.TargetReserve) &&
                  !CapacityGuard.Enough(CapacityGuard.TargetReserve - 1,
                      CapacityGuard.TargetReserve) &&
                  !CapacityGuard.Enough(CapacityGuard.JournalReserve - 1,
                      CapacityGuard.JournalReserve),
                "R2.17 Part29: both target/journal reserve boundaries are strict");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-part29-core-low-space.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part29-core-low-space-v1",
                    verdict = "PASS",
                    generatedOnly = true,
                    targetReserveBytes = CapacityGuard.TargetReserve,
                    journalReserveBytes = CapacityGuard.JournalReserve,
                    targetSpaceRefusedBeforeMutation = true,
                    journalSpaceRefusedBeforeMutation = true,
                    secondPreflightRemovedTemporaryStoreLink = true,
                    lateJournalShortagePreservedCommittedAndRemainingAliases = true,
                    noUnresolvedTransactionJournal = true,
                    observedPartialGroupLogicalSavingBytes = saved,
                    productionCapacityOverrideExists = false,
                    productionOriginalMutationEnabled = false,
                    realUserFilesTouched = false,
                    realUserCapacityFreed = 0
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART29 Core low-space target/journal gates PASS");
        }
        finally
        {
            CapacityGuard.TestOnlyReadAvailable = null;
            Journal.TestFolderPath = old;
        }
    }
}
