using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChappyStorageCompressor.Core;

// Part32: genuinely different process changes actual NTFS path entries
// between Core's last check and destructive path operation. The only
// mutable paths are guarded synthetic fixture names in the smoke TEMP root.
internal static class R217PathSwapRaceTests
{
    private const string ChildFlag = "--r217-part32-generated-path-adversary";
    private const string Guard = "CHAPPY_PART32_GENERATED_ONLY";
    private const string MovedBackupName = "part32-relocated-original.mp4";
    private const string MovedLinkName = "part32-relocated-shared.mp4";
    private const string AliasName = "part32-b.mp4";
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal)
    {
        "backup-move-foreign-alias", "backup-swap-same-bytes",
        "shared-alias-move-foreign", "shared-alias-delete", "stage-delete"
    };

    public static bool IsChild(string[] args) =>
        args.Length > 0 && args[0] == ChildFlag;

    private static string RequireRoot(string raw)
    {
        string root = Safety.ValidateRoot(raw);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, ".chappy-part23-generated-only")) ||
            File.ReadAllText(Path.Combine(root, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part32 only accepts exact generated NTFS TEMP sentinel");
        return root;
    }
    private static string OwnedFile(string root, string file)
    {
        if (string.IsNullOrWhiteSpace(file) ||
            file.Contains(Path.DirectorySeparatorChar) ||
            file.Contains(Path.AltDirectorySeparatorChar) ||
            Path.GetFileName(file) != file)
            throw new InvalidOperationException("Untrusted Part32 pathname");
        string path = Path.GetFullPath(Path.Combine(root, file));
        if (!Path.GetDirectoryName(path)!.Equals(root,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Part32 path escaped generated root");
        return path;
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 5 || args[0] != ChildFlag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            !Modes.Contains(args[2]) ||
            args[4] != "part32-race-complete.signal")
            throw new InvalidDataException("Part32 external worker arguments rejected");
        string root = RequireRoot(args[1]);
        string target = OwnedFile(root, args[3]);
        string ready = OwnedFile(root, args[4]);
        bool isBackup = Regex.IsMatch(args[3],
            @"^part32-b\.mp4\.chappy-backup-[0-9a-f]{32}$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        bool isStage = Regex.IsMatch(args[3],
            @"^\.chappy-part23-undo-[0-9a-f]{32}\.stage$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (File.Exists(ready) || !File.Exists(target))
            throw new InvalidDataException("Part32 expected a new generated signal and original path");
        switch (args[2])
        {
            case "backup-move-foreign-alias" when isBackup:
                File.Move(target, OwnedFile(root, MovedBackupName));
                File.WriteAllBytes(OwnedFile(root, AliasName),
                    RandomNumberGenerator.GetBytes(8192));
                break;
            case "backup-swap-same-bytes" when isBackup:
                string relocated = OwnedFile(root, MovedBackupName);
                File.Move(target, relocated);
                File.Copy(relocated, target);
                File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(relocated));
                File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(relocated));
                break;
            case "shared-alias-move-foreign" when args[3] == AliasName:
                File.Move(target, OwnedFile(root, MovedLinkName));
                File.WriteAllBytes(target, RandomNumberGenerator.GetBytes(8192));
                break;
            case "shared-alias-delete" when args[3] == AliasName:
                File.Delete(target);
                break;
            case "stage-delete" when isStage:
                File.Delete(target);
                break;
            default:
                throw new InvalidDataException("Part32 adversary mode and target mismatch");
        }
        using (var marker = new FileStream(ready, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            marker.Write("part32-external-process-finished"u8);
            marker.Flush(flushToDisk: true);
        }
        return 0;
    }

    private static void ExternalChange(string root, string mode, string target)
    {
        string executable = Environment.ProcessPath ??
            throw new InvalidOperationException("Part32 test executable not available");
        var si = new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false,
            CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        if (Path.GetFileName(executable).Equals("dotnet.exe",
                StringComparison.OrdinalIgnoreCase))
            si.ArgumentList.Add(typeof(R217PathSwapRaceTests).Assembly.Location);
        foreach (var arg in new[] { ChildFlag, root, mode,
                     Path.GetFileName(target), "part32-race-complete.signal" })
            si.ArgumentList.Add(arg);
        si.Environment[Guard] = "generated-only";
        using Process process = Process.Start(si) ??
            throw new IOException("Part32 external process could not launch");
        try
        {
            if (!process.WaitForExit(30000) || process.ExitCode != 0)
                throw new IOException("Part32 competing process did not complete");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        if (File.ReadAllText(Path.Combine(root, "part32-race-complete.signal"))
                != "part32-external-process-finished")
            throw new IOException("Part32 did not observe the external process's durable signal");
    }

    private static (string Root, DuplicateGroup Group, string Master,
        FileSnapshot Representative, FileSnapshot OriginalAlias)
        Fixture(string parent, string label, int files)
    {
        string root = Path.Combine(parent, "r217-part32-" + label);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string a = OwnedFile(root, "part32-a.mp4");
        File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(256 * 1024));
        for (int i = 1; i < files; i++)
        {
            string dest = OwnedFile(root, "part32-" + (char)('a' + i) + ".mp4");
            File.Copy(a, dest);
            File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(a));
            File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(a));
        }
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        if (scan.Groups.Count != 1 ||
            scan.Groups[0].Files.Count != files ||
            scan.Groups[0].BlockReason(false) is not null ||
            scan.Groups[0].Representative.Path != a)
            throw new InvalidDataException("Part32 exact synthetic scanner fixture failed");
        var g = scan.Groups.Single();
        return (root, g, Path.Combine(Safety.StorePath(root), g.Hash + ".mp4"),
            g.Representative,
            g.Files.Single(f => Path.GetFileName(f.Path) == AliasName));
    }

    private static string Hash(string path) =>
        Safety.ComputeSha256(path, CancellationToken.None);

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? priorJournal = Journal.TestFolderPath;
        var results = new List<object>();
        try
        {
            foreach (var scenario in new[]
            {
                (Name: "backup-move", Checkpoint: "backup-renamed",
                    Mode: "backup-move-foreign-alias"),
                (Name: "backup-same-byte-swap", Checkpoint: "pre-backup-delete",
                    Mode: "backup-swap-same-bytes")
            })
            {
                var f = Fixture(parent, scenario.Name, files: 2);
                Journal.TestFolderPath = Path.Combine(f.Root, "part32-isolated-journal");
                int changes = 0;
                try
                {
                    FaultInjection.OnCheckpoint = point =>
                    {
                        if (point != scenario.Checkpoint) return;
                        changes++;
                        string backup = Directory.GetFiles(f.Root,
                            "part32-b.mp4.chappy-backup-*").Single();
                        ExternalChange(f.Root, scenario.Mode, backup);
                    };
                    expectThrows(() => DedupeEngine.Run(f.Root, [f.Group],
                        false, null, CancellationToken.None),
                        "R2.17 Part32: actual separate process replaced backup/alias pathname during Core merge");
                }
                finally { FaultInjection.OnCheckpoint = null; }
                string relocated = OwnedFile(f.Root, MovedBackupName);
                string alias = f.OriginalAlias.Path;
                string expectedRecord = Journal.Pending().Count == 1
                    ? Journal.Pending().Single().BackupPath : "";
                bool allNamesExist = File.Exists(relocated) &&
                    File.Exists(alias) && File.Exists(f.Master) &&
                    File.Exists(expectedRecord);
                bool originalPreserved = allNamesExist &&
                    NativeFile.GetIdentity(relocated).SameFile(f.OriginalAlias.Identity) &&
                    Hash(relocated) == f.Group.Hash &&
                    NativeFile.SameFile(f.Representative.Path, f.Master) &&
                    Hash(f.Representative.Path) == f.Group.Hash &&
                    Safety.SameSnapshot(f.Representative);
                bool foreignPreserved = scenario.Mode == "backup-swap-same-bytes"
                    ? allNamesExist &&
                      !NativeFile.GetIdentity(expectedRecord).SameFile(f.OriginalAlias.Identity) &&
                      Hash(expectedRecord) == f.Group.Hash &&
                      NativeFile.SameFile(alias, f.Master)
                    : allNamesExist &&
                      !NativeFile.SameFile(alias, f.Master) &&
                      Hash(alias) != f.Group.Hash;
                var recovery = RecoveryService.InspectAll();
                bool safe = changes == 1 && originalPreserved &&
                    foreignPreserved && recovery.Count == 1 &&
                    recovery.Single().Action == RecoveryAction.ManualReview &&
                    Journal.UnresolvedRecordFiles().Count == 0;
                check(safe, "R2.17 Part32: foreign pathname and relocated exact original are preserved with manual recovery journal: " +
                    scenario.Name);
                // Read-only reinspection must not change the attacker file,
                // the moved original's NTFS identity or outstanding journal.
                string foreignPath = scenario.Mode == "backup-swap-same-bytes"
                    ? expectedRecord : alias;
                string foreignHash = Hash(foreignPath);
                check(RecoveryService.InspectAll().Single().Action ==
                    RecoveryAction.ManualReview &&
                    Hash(foreignPath) == foreignHash &&
                    NativeFile.GetIdentity(relocated).SameFile(f.OriginalAlias.Identity) &&
                    Journal.Pending().Count == 1,
                    "R2.17 Part32: repeated recovery inspection never discards foreign same-hash or different-byte evidence");
                results.Add(new { scenario = scenario.Name,
                    competingChildFinished = changes == 1,
                    originalNtfsIdentityPreserved = originalPreserved,
                    foreignEvidencePreserved = foreignPreserved,
                    journalManualReview = safe });
            }

            foreach (string mode in new[] {
                "shared-alias-move-foreign", "shared-alias-delete", "stage-delete"
            })
            {
                var f = Fixture(parent, "undo-" + mode, files: 3);
                Journal.TestFolderPath = Path.Combine(f.Root, "part32-isolated-journal");
                string id = CompletedGroupLedger.MergeSynthetic(f.Root, f.Group);
                string evidence = OwnedFile(f.Root, "part32-undo-evidence");
                Directory.CreateDirectory(evidence);
                int changes = 0;
                string? observedStage = null;
                expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(
                    f.Root, id, evidence,
                    testOnlyAfterFinalUndoValidation: (stage, alias) =>
                    {
                        changes++;
                        if (!Path.GetFileName(alias).Equals(AliasName,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Part32 unexpected Undo alias");
                        observedStage = stage;
                        ExternalChange(f.Root, mode,
                            mode == "stage-delete" ? stage : alias);
                    }),
                    "R2.17 Part32: external process renamed/deleted shared alias or stage after final Undo verification");
                string aliasName = f.OriginalAlias.Path;
                string relocated = OwnedFile(f.Root, MovedLinkName);
                bool originProof = NativeFile.SameFile(f.Representative.Path, f.Master) &&
                    Hash(f.Representative.Path) == f.Group.Hash &&
                    Hash(f.Master) == f.Group.Hash;
                bool adversaryProof = mode switch
                {
                    "shared-alias-move-foreign" =>
                        File.Exists(aliasName) && File.Exists(relocated) &&
                        NativeFile.SameFile(relocated, f.Master) &&
                        !NativeFile.SameFile(aliasName, f.Master) &&
                        Hash(aliasName) != f.Group.Hash &&
                        NativeFile.GetIdentity(f.Master).LinkCount == 4,
                    "shared-alias-delete" =>
                        !File.Exists(aliasName) &&
                        NativeFile.GetIdentity(f.Master).LinkCount == 3,
                    "stage-delete" =>
                        File.Exists(aliasName) &&
                        NativeFile.SameFile(aliasName, f.Master) &&
                        observedStage is not null && !File.Exists(observedStage) &&
                        NativeFile.GetIdentity(f.Master).LinkCount == 4,
                    _ => false
                };
                var summary = CompletedGroupLedger.Inspect(f.Root);
                bool refusal = changes == 1 && originProof && adversaryProof &&
                    summary.NeedsReview == 1 && Journal.Pending().Count == 0 &&
                    Journal.UnresolvedRecordFiles().Count == 0;
                check(refusal,
                    "R2.17 Part32: post-final-validation competitor leaves same verified master, no File.Replace, manual-review checkpoint: " + mode);
                string changedHash = File.Exists(aliasName) ? Hash(aliasName) : "(deleted)";
                expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(f.Root,
                    id, evidence), "R2.17 Part32: ambiguous stage/alias cannot automatically replay a stale group Undo");
                check(CompletedGroupLedger.Inspect(f.Root).NeedsReview == 1 &&
                    (File.Exists(aliasName) ? Hash(aliasName) : "(deleted)") ==
                        changedHash &&
                    NativeFile.SameFile(f.Representative.Path, f.Master),
                    "R2.17 Part32: failed retry does not restore/deallocate or overwrite attacker-modified path");
                results.Add(new { scenario = mode,
                    competingChildFinished = changes == 1,
                    originalNtfsIdentityPreserved = originProof,
                    foreignEvidencePreserved = adversaryProof,
                    journalManualReview = refusal });
            }

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-part32-path-swap-races.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part32-path-swap-races-v1",
                    verdict = "PASS",
                    generatedNtfsOnly = true,
                    realCompetingProcessCount = results.Count,
                    scenarios = results,
                    originalIdentityCheckedAtBackupDelete = true,
                    compromisedBackupNeverRemovedDuringCatchRollback = true,
                    sameHashDifferentFileIdForeignBackupPreserved = true,
                    postFinalUndoRenameAndDeleteRefused = true,
                    manualReviewEvidenceNeverAutoRepaired = true,
                    productionDedupeEnabled = false,
                    productionIndependentUndoEnabled =
                        IndependentUndoEngine.ProductionEnabled,
                    realUserFilesTouched = false,
                    realUserCapacityFreedBytes = 0,
                    arbitraryDeletionRaceEliminated = false,
                    physicalPowerCutProven = false
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART32 five real competing-process path swaps/deletes PASS");
        }
        finally
        {
            Journal.TestFolderPath = priorJournal;
            FaultInjection.OnCheckpoint = null;
        }
    }
}
