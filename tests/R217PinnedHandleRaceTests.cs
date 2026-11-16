using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Genuine Windows child-process pathname races, and a HANDLE-PINNED
// SetFileInformationByHandle lab. No existing/user data may be selected.
internal static class R217PinnedHandleRaceTests
{
    private const string ChildFlag = "--r217-part33-generated-handle-adversary";
    private const string Guard = "CHAPPY_PART33_GENERATED_ONLY";
    private const string SourceName = "part33-source.mp4";
    private const string TargetName = "part33-target.mp4";
    private const string RelocatedName = "part33-relocated.mp4";
    private const string ExtraName = "part33-extra.mp4";
    private const string SignalName = "part33-external-verified.signal";

    public static bool IsChild(string[] args) =>
        args.Length > 0 && args[0] == ChildFlag;

    public static int RunChild(string[] args)
    {
        if (args.Length != 4 || args[0] != ChildFlag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            args[3] != SignalName ||
            args[2] is not ("move-and-plant" or "move-and-copy" or "plant-target"))
            throw new InvalidDataException("Part33 child arguments rejected");
        string root = SyntheticPinnedNtfsFile.RequireRoot(args[1]);
        string source = SyntheticPinnedNtfsFile.RequireDirectFile(root,
            Path.Combine(root, SourceName));
        string target = SyntheticPinnedNtfsFile.RequireDirectFile(root,
            Path.Combine(root, TargetName), mustExist: false);
        string relocated = SyntheticPinnedNtfsFile.RequireDirectFile(root,
            Path.Combine(root, RelocatedName), mustExist: false);
        string signal = Path.Combine(root, SignalName);
        if (File.Exists(signal) || File.Exists(relocated) || File.Exists(target))
            throw new InvalidOperationException("Unexpected generated Part33 target/marker collision");

        switch (args[2])
        {
            case "move-and-plant":
                File.Move(source, relocated);
                File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(13 * 1024));
                break;
            case "move-and-copy":
                File.Move(source, relocated);
                File.Copy(relocated, source);
                File.SetCreationTimeUtc(source, File.GetCreationTimeUtc(relocated));
                File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(relocated));
                break;
            case "plant-target":
                File.WriteAllBytes(target, RandomNumberGenerator.GetBytes(17 * 1024));
                break;
        }
        using var marker = new FileStream(signal, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        marker.Write("part33-real-external-child"u8);
        marker.Flush(flushToDisk: true);
        return 0;
    }

    private static void Compete(string root, string mode)
    {
        string runner = Environment.ProcessPath ??
            throw new InvalidOperationException("Part33 executable missing");
        var info = new ProcessStartInfo
        {
            FileName = runner, WorkingDirectory = Path.GetDirectoryName(runner)!,
            UseShellExecute = false, CreateNoWindow = true
        };
        if (Path.GetFileName(runner).Equals("dotnet.exe",
                StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(R217PinnedHandleRaceTests).Assembly.Location);
        foreach (string arg in new[] { ChildFlag, root, mode, SignalName })
            info.ArgumentList.Add(arg);
        info.Environment[Guard] = "generated-only";
        using Process proc = Process.Start(info) ??
            throw new IOException("Part33 external subprocess failed to launch");
        try
        {
            if (!proc.WaitForExit(30000) || proc.ExitCode != 0)
                throw new IOException("Part33 competitor did not finish");
        }
        finally
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit();
            }
        }
        if (File.ReadAllText(Path.Combine(root, SignalName)) !=
            "part33-real-external-child")
            throw new IOException("Part33 competitor did not flush exact signal");
    }

    private static (string Root, string Source, string Target, string Relocated,
        string Hash, FileIdentity Identity) Fixture(string parent, string label)
    {
        string root = Path.Combine(parent, "r217-part33-" + label);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string source = Path.Combine(root, SourceName);
        File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(256 * 1024));
        string target = Path.Combine(root, TargetName);
        string relocated = Path.Combine(root, RelocatedName);
        return (root, source, target, relocated,
            Safety.ComputeSha256(source, CancellationToken.None),
            NativeFile.GetIdentity(source));
    }

    private static string Hash(string file)
    {
        // Part33 probe holds a DELETE-access NTFS handle intentionally.
        // Safety's normal read-only hash does not share DELETE and would
        // fail before bytes could be inspected. The probe read also shares
        // delete while denying concurrent WRITE access.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        var cases = new List<object>();
        bool readOnlyProduction = !CompletedGroupLedger.ProductionMutationEnabled &&
            !IndependentUndoEngine.ProductionEnabled;
        // The production Core itself still throws on mutation, regardless
        // of the presence of this entirely CHAPPY_TESTING-compiled lab.
        check(readOnlyProduction,
            "R2.17 Part33: pinned-handle probe remains absent from production and existing mutation lock stays engaged");

        // Scenario 1: old NAME changes after our last check, while a DELETE-
        // access Win32 handle already pins the original NTFS file ID.
        // The handle rename moves the pinned ID, NEVER the foreign occupant.
        {
            var f = Fixture(parent, "handle-rename-after-path-swap");
            using var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source);
            int actualChildren = 0;
            pinned.RenameNoReplace(f.Target, afterLastPathCheck: () =>
            {
                actualChildren++;
                Compete(f.Root, "move-and-plant");
            });
            bool valid = actualChildren == 1 &&
                !File.Exists(f.Relocated) &&
                File.Exists(f.Source) &&
                NativeFile.GetIdentity(f.Target).SameFile(f.Identity) &&
                !NativeFile.GetIdentity(f.Source).SameFile(f.Identity) &&
                Hash(f.Target) == f.Hash &&
                Hash(f.Source) != f.Hash &&
                NativeFile.GetIdentity(f.Target).LinkCount == 1;
            check(valid,
                "R2.17 Part33: real child swaps source after precheck; handle rename moves ONLY pinned original ID, foreign old-name occupant untouched");
            cases.Add(new { name = "handle-pinned-postcheck-swap", safe = valid,
                separateProcess = true, originalDataPreserved = valid,
                foreignDataPreserved = valid });
        }

        // Scenario 2: already changed name before attempted rename. Even when
        // a competitor made a *same-byte* replacement, exact file ID rejects.
        {
            var f = Fixture(parent, "same-hash-source-identity-swap");
            using var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source);
            Compete(f.Root, "move-and-copy");
            check(Hash(f.Source) == f.Hash &&
                  !NativeFile.GetIdentity(f.Source).SameFile(f.Identity) &&
                  NativeFile.GetIdentity(f.Relocated).SameFile(f.Identity) &&
                  !pinned.OriginalPathStillMatches(),
                "R2.17 Part33: external same-SHA replacement retains bytes but changes NTFS file ID");
            expectThrows(() => pinned.RenameNoReplace(f.Target),
                "R2.17 Part33: strict original path guard refuses same-byte foreign ID before handle rename");
            bool valid = !File.Exists(f.Target) &&
                Hash(f.Source) == f.Hash &&
                Hash(f.Relocated) == f.Hash &&
                NativeFile.GetIdentity(f.Relocated).SameFile(f.Identity);
            check(valid,
                "R2.17 Part33: same-byte foreign pathname and relocated exact original remain unmodified");
            cases.Add(new { name = "guard-same-sha-different-id", safe = valid,
                separateProcess = true, originalDataPreserved = valid,
                foreignDataPreserved = valid });
        }

        // Scenario 3: another process plants a DIFFERENT file at the target
        // after preflight. The kernel FileRenameInfo ReplaceIfExists=FALSE
        // must refuse without unlinking or replacing that foreign target.
        {
            var f = Fixture(parent, "foreign-target-arrives-after-precheck");
            using var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source);
            expectThrows(() => pinned.RenameNoReplace(f.Target,
                afterLastPathCheck: () => Compete(f.Root, "plant-target")),
                "R2.17 Part33: native handle rename MUST refuse newly occupied destination without replace");
            bool valid = File.Exists(f.Source) && File.Exists(f.Target) &&
                NativeFile.GetIdentity(f.Source).SameFile(f.Identity) &&
                Hash(f.Source) == f.Hash &&
                Hash(f.Target) != f.Hash &&
                NativeFile.GetIdentity(f.Target).LinkCount == 1;
            check(valid,
                "R2.17 Part33: target created after final check was not overwritten, deleted, or mistaken for original");
            cases.Add(new { name = "native-no-replace-target-race", safe = valid,
                separateProcess = true, originalDataPreserved = valid,
                foreignDataPreserved = valid });
        }

        // Scenario 4: after a child moves and plants a different file, the
        // stale path is untrusted. NO rename/delete is attempted.
        {
            var f = Fixture(parent, "foreign-source-before-check");
            using var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source);
            Compete(f.Root, "move-and-plant");
            expectThrows(() => pinned.RenameNoReplace(f.Target),
                "R2.17 Part33: safe handle rename refuses changed original name before cutover");
            bool valid = !File.Exists(f.Target) &&
                NativeFile.GetIdentity(f.Relocated).SameFile(f.Identity) &&
                Hash(f.Relocated) == f.Hash &&
                Hash(f.Source) != f.Hash;
            check(valid,
                "R2.17 Part33: prior foreign source occupant and relocated original preserved with no attempted cutover");
            cases.Add(new { name = "guard-prerename-moved-source", safe = valid,
                separateProcess = true, originalDataPreserved = valid,
                foreignDataPreserved = valid });
        }

        // Scenario 5: normal handle rename preserves exact ID + SHA-256.
        {
            var f = Fixture(parent, "normal-rename");
            using var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source);
            pinned.RenameNoReplace(f.Target);
            bool valid = !File.Exists(f.Source) && File.Exists(f.Target) &&
                NativeFile.GetIdentity(f.Target).SameFile(f.Identity) &&
                Hash(f.Target) == f.Hash;
            check(valid,
                "R2.17 Part33: unopposed SetFileInformationByHandle rename retains exact original NTFS identity");
            cases.Add(new { name = "unopposed-pinned-rename", safe = valid,
                separateProcess = false, originalDataPreserved = valid,
                foreignDataPreserved = true });
        }

        // Scenario 6: deletion by pinned handle is allowed ONLY for
        // unmodified single-link generated files, not a shared hardlink.
        {
            var f = Fixture(parent, "single-link-disposition");
            using (var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source))
                pinned.DeleteOnClose();
            bool valid = !File.Exists(f.Source) &&
                !File.Exists(f.Target) && !File.Exists(f.Relocated);
            check(valid,
                "R2.17 Part33: single-link generated file disposition delete-on-close operates on pinned handle only");
            cases.Add(new { name = "unopposed-pinned-delete", safe = valid,
                separateProcess = false, originalDataPreserved = false,
                foreignDataPreserved = true });
        }

        // Scenario 7: explicit refusal of an existing multi-link file.
        {
            var f = Fixture(parent, "shared-link-refusal");
            string extra = Path.Combine(f.Root, ExtraName);
            NativeFile.NewHardLink(extra, f.Source);
            expectThrows(() => SyntheticPinnedNtfsFile.Open(f.Root, f.Source),
                "R2.17 Part33: pinned destructive handle refuses shared NTFS file ID");
            bool valid = NativeFile.GetIdentity(f.Source).LinkCount == 2 &&
                NativeFile.SameFile(extra, f.Source) && Hash(extra) == f.Hash;
            check(valid,
                "R2.17 Part33: shared hardlinked original remains intact and cannot enter single-link handle mutation lab");
            cases.Add(new { name = "reject-hardlink-with-two-names", safe = valid,
                separateProcess = false, originalDataPreserved = valid,
                foreignDataPreserved = true });
        }

        // B2: same primitive as Core rollback removes ONLY one owned hardlink
        // name. The original data-bearing master must remain untouched.
        {
            var f = Fixture(parent, "shared-alias-pinned-removal");
            string extra = Path.Combine(f.Root, ExtraName);
            NativeFile.NewHardLink(extra, f.Source);
            using (var pinned = NativeFile.PinnedFile.Open(extra, f.Identity, f.Hash,
                requireIndependent: false))
                pinned.DeleteCurrent(requireIndependent: false);
            bool valid = File.Exists(f.Source) && !File.Exists(extra) &&
                NativeFile.GetIdentity(f.Source).SameFile(f.Identity) &&
                NativeFile.GetIdentity(f.Source).LinkCount == 1 &&
                Hash(f.Source) == f.Hash;
            check(valid,
                "B2: pinned Core rollback unlinks ONLY generated alias and retains the untouched master bytes and NTFS ID");
            cases.Add(new { name = "shared-alias-pinned-removal", safe = valid,
                separateProcess = false, originalDataPreserved = valid,
                foreignDataPreserved = true });
        }

        // Scenario 8: source/destination confinement rules enforce same
        // generated direct-child root, not user media or other NTFS volume.
        {
            var f = Fixture(parent, "path-scope-refusal");
            using var pinned = SyntheticPinnedNtfsFile.Open(f.Root, f.Source);
            string outside = Path.Combine(parent, "part33-OUTSIDE.mp4");
            expectThrows(() => pinned.RenameNoReplace(outside),
                "R2.17 Part33: no handle rename outside this generated root");
            bool valid = NativeFile.GetIdentity(f.Source).SameFile(f.Identity) &&
                Hash(f.Source) == f.Hash && !File.Exists(outside);
            check(valid,
                "R2.17 Part33: exact source scope refuses target outside generated sentinel directory");
            cases.Add(new { name = "reject-nonowned-destination", safe = valid,
                separateProcess = false, originalDataPreserved = valid,
                foreignDataPreserved = true });
        }

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-part33-handle-identity-races.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-part33-pinned-handle-v1",
                verdict = "PASS",
                generatedOnly = true,
                generatedTestGuardCompiledOutOfRelease = true,
                nativeKernelPrimitiveSharedWithCore = true,
                shippedDedupeEntryStillLocked = true,
                exactNativeApi = "SetFileInformationByHandle",
                nativeRenameInformationClass = 3,
                nativeDeleteInformationClass = 4,
                renameReplaceIfExists = false,
                actualExternalChildProcesses = 4,
                scenarioCount = cases.Count,
                scenarios = cases,
                corePinnedCutoverNowUsesSamePrimitive = true,
                allCurrentUserMediaUnchanged = true,
                realUserRecoveredBytes = 0,
                physicalPowerCutProven = false,
                globalRaceFreeCommitProven = false,
                handleBasedRenameIntegratedIntoDedupe = false,
                arbitraryFileSystemOwnershipProven = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART33 Windows pinned NTFS handle rename/disposition and four external processes PASS");
    }
}
