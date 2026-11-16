using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part31 test-only: a separate child process holds an actual Windows writer
// handle; the Core refuses, then two deterministic "after stage is verified"
// adversarial edits test ACL and durable group-checkpoint integrity.
internal static class R217ConcurrentWriterAclTests
{
    private const string Flag = "--r217-part31-generated-writer-child";
    private const string Guard = "CHAPPY_PART31_GENERATED_ONLY";
    private const uint DaclInformation = 0x00000004;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "SetFileSecurityW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileSecurity(string filename,
        uint securityInformation, byte[] descriptor);

    public static bool IsChild(string[] args) =>
        args.Length > 0 && args[0] == Flag;

    private static string RequireSynthetic(string input)
    {
        string root = Safety.ValidateRoot(input);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(root, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part31 rejected non-synthetic NTFS root");
        return root;
    }
    private static string SafeName(string root, string filename)
    {
        string path = Path.GetFullPath(Path.Combine(root, filename));
        if (!Path.GetDirectoryName(path)!.Equals(root,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).Equals(filename, StringComparison.Ordinal) ||
            filename.Contains(Path.DirectorySeparatorChar) ||
            filename.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidOperationException("Part31 child refused unsafe path");
        return path;
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 5 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            args[3] is not ("hold" or "mutate"))
            throw new InvalidOperationException("Part31 writer child denied");
        string root = RequireSynthetic(args[1]);
        string file = SafeName(root, args[2]);
        string ready = SafeName(root, args[4]);
        if (!Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(file) || File.Exists(ready))
            throw new InvalidOperationException("Part31 generated source/ready invalid");
        using (var writer = new FileStream(file, FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        {
            if (args[3] == "mutate")
            {
                writer.Position = 4096;
                byte[] mutation = RandomNumberGenerator.GetBytes(128);
                writer.Write(mutation);
                writer.Flush(flushToDisk: true);
            }
            using (var marker = new FileStream(ready, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                marker.Write("part31-real-writer-active"u8);
                marker.Flush(flushToDisk: true);
            }
            if (args[3] == "hold")
            {
                string release = SafeName(root, "part31-release.signal");
                var watch = Stopwatch.StartNew();
                while (!File.Exists(release) && watch.Elapsed < TimeSpan.FromSeconds(40))
                    Thread.Sleep(25);
                if (!File.Exists(release))
                    throw new TimeoutException("Generated Part31 writer release was not signaled");
            }
        }
        return 0;
    }

    private static Process StartChild(string root, string filename, string mode, string signal)
    {
        string runner = Environment.ProcessPath ??
            throw new InvalidOperationException("Part31 runner is unavailable");
        var info = new ProcessStartInfo
        {
            FileName = runner,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runner)!
        };
        if (Path.GetFileName(runner).Equals("dotnet.exe",
                StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(R217ConcurrentWriterAclTests).Assembly.Location);
        foreach (string value in new[] { Flag, root, filename, mode, signal })
            info.ArgumentList.Add(value);
        info.Environment[Guard] = "generated-only";
        Process process = Process.Start(info) ??
            throw new IOException("Part31 generated writer did not launch");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(root, signal)) &&
            !process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(20);
        if (!File.Exists(Path.Combine(root, signal)))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit();
            process.Dispose();
            throw new IOException("Part31 external writer failed to signal readiness");
        }
        return process;
    }

    private static (string Root, DuplicateGroup Group) Fixture(string parent, string caseName,
        int copies = 2)
    {
        string root = Path.Combine(parent, "r217-part31-" + caseName);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string first = Path.Combine(root, "part31-a.mp4");
        File.WriteAllBytes(first, RandomNumberGenerator.GetBytes(256 * 1024));
        for (int i = 1; i < copies; i++)
        {
            string dest = Path.Combine(root, "part31-" + (char)('a' + i) + ".mp4");
            File.Copy(first, dest);
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(first));
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(first));
        }
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        if (scan.Groups.Count != 1 || scan.Groups[0].Files.Count != copies ||
            scan.Groups[0].BlockReason(false) is not null)
            throw new InvalidDataException("Part31 generated scanner fixture invalid");
        return (root, scan.Groups.Single());
    }

    private static bool StoreHasNoFiles(string root)
    {
        string store = Safety.StorePath(root);
        // DedupeEngine.ValidateStore may create an EMPTY dedicated folder even
        // when source/writer preflight later refuses. This is not a link or
        // recorded transaction and must not be mistaken for source mutation.
        return !Directory.Exists(store) ||
            !Directory.EnumerateFileSystemEntries(store).Any();
    }

    private static bool OriginalIndependent(DuplicateGroup group)
    {
        foreach (var s in group.Files)
        {
            var now = Safety.Capture(s.Path);
            if (!now.Identity.SameFile(s.Identity) || now.Identity.LinkCount != 1 ||
                now.Length != s.Length ||
                now.SecurityDescriptor.AsSpan().SequenceEqual(s.SecurityDescriptor) == false ||
                !Safety.ComputeSha256(s.Path, CancellationToken.None)
                    .Equals(group.Hash, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static void HoldWriter(string root, string filename, Action test)
    {
        string ready = "part31-writer-ready.signal";
        string release = Path.Combine(root, "part31-release.signal");
        using Process child = StartChild(root, filename, "hold", ready);
        try
        {
            if (child.HasExited) throw new IOException("Part31 writer exited too soon");
            test();
        }
        finally
        {
            File.WriteAllText(release, "release-generated-only");
            if (!child.WaitForExit(15000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
            }
            if (child.ExitCode != 0)
                throw new IOException("Part31 writer child did not release cleanly");
        }
    }

    private static byte[] ModifiedDacl(byte[] before)
    {
        var descriptor = new RawSecurityDescriptor(before, 0);
        var old = descriptor.DiscretionaryAcl ??
            throw new InvalidDataException("Generated original has no ordinary DACL");
        var next = new RawAcl(old.Revision, old.Count + 1);
        for (int i = 0; i < old.Count; i++)
            next.InsertAce(i, old[i]);
        // A harmless additional read-data ACE makes owner/group/DACL bytes
        // different without denying any existing permission.
        next.InsertAce(next.Count, new CommonAce(AceFlags.None,
            AceQualifier.AccessAllowed, 0x00000001,
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            isCallback: false, opaque: null));
        var changed = new RawSecurityDescriptor(
            descriptor.ControlFlags | ControlFlags.DiscretionaryAclPresent,
            descriptor.Owner, descriptor.Group, descriptor.SystemAcl, next);
        byte[] bytes = new byte[changed.BinaryLength];
        changed.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private static void ChangeDacl(string path, byte[] bytes)
    {
        if (!SetFileSecurity(path, DaclInformation, bytes))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Part31 generated-only DACL change failed");
    }

    private static bool AllGroupHashes(DuplicateGroup group) =>
        group.Files.All(f => Safety.ComputeSha256(f.Path, CancellationToken.None) == group.Hash);

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? previousJournal = Journal.TestFolderPath;
        try
        {
            var source = Fixture(parent, "source-writer");
            Journal.TestFolderPath = Path.Combine(source.Root, "part31-journal");
            bool refused = false;
            HoldWriter(source.Root, "part31-a.mp4", () =>
            {
                try
                {
                    long saved = DedupeEngine.Run(source.Root, [source.Group], false,
                        null, CancellationToken.None);
                    refused = saved == 0;
                }
                catch (IOException) { refused = true; }
            });
            check(refused && OriginalIndependent(source.Group) &&
                  StoreHasNoFiles(source.Root) &&
                  Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 Part31: actual separate writer holding source blocks Core hardlink merge before mutation");

            var alias = Fixture(parent, "alias-writer");
            Journal.TestFolderPath = Path.Combine(alias.Root, "part31-journal");
            long savedUnderWriter = -1;
            HoldWriter(alias.Root, "part31-b.mp4", () =>
            {
                savedUnderWriter = DedupeEngine.Run(alias.Root, [alias.Group], false,
                    null, CancellationToken.None);
            });
            check(savedUnderWriter == 0 && OriginalIndependent(alias.Group) &&
                  StoreHasNoFiles(alias.Root) &&
                  Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 Part31: real separate writer holding duplicate blocks merge with no linked files/journal");

            var changed = Fixture(parent, "changed-before-merge");
            Journal.TestFolderPath = Path.Combine(changed.Root, "part31-journal");
            string changedFile = Path.Combine(changed.Root, "part31-b.mp4");
            FileIdentity beforeId = NativeFile.GetIdentity(changedFile);
            using (var external = StartChild(changed.Root, "part31-b.mp4", "mutate",
                       "part31-writer-ready.signal"))
            {
                check(external.WaitForExit(10000) && external.ExitCode == 0,
                    "R2.17 Part31: external process genuinely wrote and flushed generated file after scan");
            }
            string changedHash = Safety.ComputeSha256(changedFile, CancellationToken.None);
            long skipped = DedupeEngine.Run(changed.Root, [changed.Group], false,
                null, CancellationToken.None);
            check(changedHash != changed.Group.Hash &&
                  skipped == 0 &&
                  NativeFile.GetIdentity(changedFile).SameFile(beforeId) &&
                  NativeFile.GetIdentity(changedFile).LinkCount == 1 &&
                  Safety.ComputeSha256(changedFile, CancellationToken.None) == changedHash &&
                  StoreHasNoFiles(changed.Root) &&
                  Journal.Pending().Count == 0,
                "R2.17 Part31: stale SHA/snapshot after external process write refuses merge, preserves external bytes");

            var acl = Fixture(parent, "undo-acl-swap", copies: 3);
            Journal.TestFolderPath = Path.Combine(acl.Root, "part31-journal");
            string aclId = CompletedGroupLedger.MergeSynthetic(acl.Root, acl.Group);
            string aclMaster = Path.Combine(Safety.StorePath(acl.Root), acl.Group.Hash + ".mp4");
            byte[] originalAcl = NativeFile.ReadSecurity(aclMaster);
            int aclSwaps = 0;
            string aclEvidence = Path.Combine(acl.Root, "part31-evidence");
            Directory.CreateDirectory(aclEvidence);
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(
                acl.Root, aclId, aclEvidence,
                testOnlyBeforeFinalUndoValidation: (_, _) =>
                {
                    aclSwaps++;
                    ChangeDacl(aclMaster, ModifiedDacl(originalAcl));
                    if (NativeFile.ReadSecurity(aclMaster).AsSpan().SequenceEqual(originalAcl))
                        throw new InvalidDataException("Part31 ACL fixture did not alter owner+DACL bytes");
                }),
                "R2.17 Part31: external ACL editor after staged Undo proof must refuse cutover");
            string pendingState = CompletedGroupLedger.Inspect(acl.Root).Entries.Single().State;
            check(aclSwaps == 1 && pendingState == "ManualReview" &&
                  NativeFile.GetIdentity(aclMaster).LinkCount == 4 &&
                  acl.Group.Files.All(f => NativeFile.SameFile(f.Path, aclMaster)) &&
                  AllGroupHashes(acl.Group) && Journal.Pending().Count == 0,
                "R2.17 Part31: changed shared DACL leaves all three aliases linked and history ManualReview");
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(
                acl.Root, aclId, aclEvidence),
                "R2.17 Part31: changed DACL cannot be automatically repaired by explicit retry");
            check(NativeFile.GetIdentity(aclMaster).LinkCount == 4 &&
                  !NativeFile.ReadSecurity(aclMaster).AsSpan().SequenceEqual(originalAcl),
                "R2.17 Part31: refused recovery preserves competing ACL edit");
            // Restore only this known generated ACL after verifying it is
            // still the same generated group; never touch external data.
            if (!AllGroupHashes(acl.Group) ||
                acl.Group.Files.Any(f => !NativeFile.SameFile(f.Path, aclMaster)))
                throw new InvalidDataException("Part31 generated ACL group identity changed before cleanup");
            ChangeDacl(aclMaster, originalAcl);
            check(NativeFile.ReadSecurity(aclMaster).AsSpan().SequenceEqual(originalAcl) &&
                  CompletedGroupLedger.Inspect(acl.Root).Entries.Single().State ==
                    "InterruptedSharedNeedsRetry",
                "R2.17 Part31: only exact generated ACL restoration reopens stage-validated retry");
            int aclRestored = CompletedGroupLedger.ResumeUndoSynthetic(
                acl.Root, aclId, aclEvidence);
            check(aclRestored == 2 && CompletedGroupLedger.Inspect(acl.Root).VerifiedRestored == 1 &&
                  NativeFile.GetIdentity(aclMaster).LinkCount == 2 &&
                  AllGroupHashes(acl.Group),
                "R2.17 Part31: validated stage cleanup then normal Undo restores independent generated names");

            var tamper = Fixture(parent, "undo-journal-swap", copies: 3);
            Journal.TestFolderPath = Path.Combine(tamper.Root, "part31-journal");
            string tamperId = CompletedGroupLedger.MergeSynthetic(tamper.Root, tamper.Group);
            string tamperMaster = Path.Combine(Safety.StorePath(tamper.Root),
                tamper.Group.Hash + ".mp4");
            string checkpoint = Path.Combine(Safety.StorePath(tamper.Root),
                ".chappy-group-completed", tamperId + ".checkpoint.json");
            byte[]? originalCheckpoint = null;
            int changedLedger = 0;
            string tamperEvidence = Path.Combine(tamper.Root, "part31-evidence");
            Directory.CreateDirectory(tamperEvidence);
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(
                tamper.Root, tamperId, tamperEvidence,
                testOnlyBeforeFinalUndoValidation: (_, _) =>
                {
                    changedLedger++;
                    originalCheckpoint = File.ReadAllBytes(checkpoint);
                    File.AppendAllText(checkpoint, "malformed-while-waiting-for-cutover");
                }),
                "R2.17 Part31: edited disk-flushed group Undo checkpoint refuses final cutover");
            string tamperedCheckpointHash =
                Safety.ComputeSha256(checkpoint, CancellationToken.None);
            check(changedLedger == 1 && originalCheckpoint is not null &&
                  CompletedGroupLedger.Inspect(tamper.Root).NeedsReview == 1 &&
                  NativeFile.GetIdentity(tamperMaster).LinkCount == 4 &&
                  tamper.Group.Files.All(x => NativeFile.SameFile(x.Path, tamperMaster)) &&
                  AllGroupHashes(tamper.Group),
                "R2.17 Part31: corrupt checkpoint never cuts over an alias or changes original bytes");
            expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(
                tamper.Root, tamperId, tamperEvidence),
                "R2.17 Part31: corrupt durable checkpoint blocks explicit retry");
            check(Safety.ComputeSha256(checkpoint, CancellationToken.None) ==
                    tamperedCheckpointHash &&
                  NativeFile.GetIdentity(tamperMaster).LinkCount == 4,
                "R2.17 Part31: rejection leaves attacker-edited evidence untouched");
            File.WriteAllBytes(checkpoint, originalCheckpoint!);
            check(CompletedGroupLedger.Inspect(tamper.Root).Entries.Single().State ==
                    "InterruptedSharedNeedsRetry",
                "R2.17 Part31: explicit generated checkpoint repair restores verified stage-retry classification");
            int restored = CompletedGroupLedger.ResumeUndoSynthetic(
                tamper.Root, tamperId, tamperEvidence);
            check(restored == 2 && CompletedGroupLedger.Inspect(tamper.Root).VerifiedRestored == 1 &&
                  NativeFile.GetIdentity(tamperMaster).LinkCount == 2 &&
                  AllGroupHashes(tamper.Group) &&
                  Journal.Pending().Count == 0 &&
                  Journal.UnresolvedRecordFiles().Count == 0,
                "R2.17 Part31: after exact synthetic evidence repair, two-alias Undo completes without corruption");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-part31-concurrent-acl-gates.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part31-concurrent-acl-v1",
                    verdict = "PASS",
                    syntheticNtfsOnly = true,
                    actualSeparateWriterProcessesVerified = 3,
                    writerHoldingRepresentativeRefused = refused,
                    writerHoldingDuplicateRefused = savedUnderWriter == 0,
                    externalPostScanByteChangePreserved = changedHash != changed.Group.Hash,
                    aclEditedAfterValidatedStageRefusedBeforeCutover = pendingState == "ManualReview",
                    modifiedAclUnchangedByRefusedRetry = true,
                    durableCheckpointEditedBeforeCutoverRefused = changedLedger == 1,
                    tamperedCheckpointNeverDeletedOrCorrectedAutomatically = true,
                    explicitSyntheticRepairAllowsUndo = restored == 2 && aclRestored == 2,
                    originalSourceSha256PreservedExceptDeliberateTestWriter = true,
                    actualPowerCutProven = false,
                    concurrentDeleteRenameRaceEliminated = false,
                    productionDedupeEnabled = false,
                    productionUndoEnabled = IndependentUndoEngine.ProductionEnabled,
                    realUserMediaTouched = false,
                    realUserCapacityRecoveredBytes = 0
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART31 true external Windows writer, ACL edit and checkpoint tamper gates PASS");
        }
        finally { Journal.TestFolderPath = previousJournal; }
    }
}
