using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Part28: real process death INSIDE IndependentUndoEngine.Restore, with two
// distinct persisted group manifests sharing the same synthetic NTFS root.
// No real photos/video and no non-sentinel-guarded writes are allowed.
internal static class R217GroupIntraAliasCrashTests
{
    private const string Flag = "--r217-part28-generated-group-intra-alias-kill";
    private const string Guard = "CHAPPY_PART28_GROUP_CRASH_GENERATED_ONLY";
    private static readonly string[] Phases =
    [
        "intent-written", "stage-validated",
        "post-cutover-before-metadata", "metadata-restored-before-ledger"
    ];
    private static string Hash(string p) => Safety.ComputeSha256(p, CancellationToken.None);
    public static bool IsChild(string[] args) => args.Length > 0 && args[0] == Flag;

    private static void AssertRoot(string root)
    {
        string full = Safety.ValidateRoot(root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !full.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            File.ReadAllText(Path.Combine(full, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException("Part28 child refused non-generated NTFS root");
    }

    public static int RunChild(string[] args)
    {
        if (args.Length != 5 || args[0] != Flag ||
            Environment.GetEnvironmentVariable(Guard) != "generated-only" ||
            !Guid.TryParseExact(args[2], "N", out _) ||
            !Phases.Contains(args[3], StringComparer.Ordinal))
            throw new InvalidDataException("Untrusted Part28 child arguments");
        string root = Path.GetFullPath(args[1]);
        string signal = Path.GetFullPath(args[4]);
        AssertRoot(root);
        if (!signal.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Part28 process signal outside generated root");
        Journal.TestFolderPath = Path.Combine(root, "part28-journal");
        CompletedGroupLedger.ResumeUndoSynthetic(root, args[2],
            Path.Combine(root, "part28-evidence"), afterDurablePhase: phase =>
            {
                if (phase != args[3]) return;
                using (var stream = new FileStream(signal, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None))
                {
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(phase);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                using var self = Process.GetCurrentProcess();
                self.Kill(); // only our guarded generated-only worker
                Thread.Sleep(Timeout.Infinite);
            });
        throw new InvalidDataException("Part28 worker did not hit requested real Core phase");
    }

    private static int KillWorker(string root, string id, string phase, string signal)
    {
        string runtime = Environment.ProcessPath ??
            throw new InvalidOperationException("No Core test runner executable");
        var info = new ProcessStartInfo
        {
            FileName = runtime, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(runtime)!
        };
        if (Path.GetFileName(runtime).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(R217GroupIntraAliasCrashTests).Assembly.Location);
        foreach (string arg in new[] { Flag, root, id, phase, signal })
            info.ArgumentList.Add(arg);
        info.Environment[Guard] = "generated-only";
        using var worker = Process.Start(info) ??
            throw new IOException("Part28 generated worker refused launch");
        try
        {
            if (!worker.WaitForExit(60000))
                throw new TimeoutException("Part28 actual worker kill did not finish");
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                worker.WaitForExit();
            }
        }
        return worker.ExitCode;
    }

    private static (string Root, string First, string Other, string FirstMaster,
        string OtherMaster, Dictionary<string, FileSnapshot> Originals)
        CreateFixture(string parent, string phase)
    {
        string root = Path.Combine(parent, "r217-part28-group-" +
            phase.Replace('-', '_'));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        Journal.TestFolderPath = Path.Combine(root, "part28-journal");
        string a = Path.Combine(root, "group-a0.mp4");
        string b = Path.Combine(root, "group-b0.mp4");
        File.WriteAllBytes(a, RandomNumberGenerator.GetBytes(384 * 1024));
        File.WriteAllBytes(b, RandomNumberGenerator.GetBytes(211 * 1024));
        foreach (var (original, prefix, aliases) in new[]
        {
            (a, "group-a", 3), (b, "group-b", 2)
        })
        {
            for (int i = 1; i <= aliases; i++)
            {
                string dest = Path.Combine(root, prefix + i + ".mp4");
                File.Copy(original, dest);
                File.SetCreationTimeUtc(dest, File.GetCreationTimeUtc(original));
                File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(original));
            }
        }
        var scan = Scanner.Run(root, true, null, CancellationToken.None);
        if (scan.Groups.Count != 2 ||
            !scan.Groups.Any(g => g.Files.Count == 4) ||
            !scan.Groups.Any(g => g.Files.Count == 3) ||
            scan.Groups.Any(g => g.BlockReason(false) is not null))
            throw new InvalidDataException("Part28 generated two-group scanner fixture invalid");
        var originals = scan.Groups.SelectMany(g => g.Files)
            .ToDictionary(s => s.Path, StringComparer.OrdinalIgnoreCase);
        var first = scan.Groups.Single(g => g.Files.Count == 4);
        var other = scan.Groups.Single(g => g.Files.Count == 3);
        string firstId = CompletedGroupLedger.MergeSynthetic(root, first);
        string otherId = CompletedGroupLedger.MergeSynthetic(root, other);
        var ledgers = CompletedGroupLedger.Inspect(root);
        if (ledgers.VerifiedCompleted != 2 || ledgers.NeedsReview != 0 ||
            Journal.Pending().Count != 0)
            throw new InvalidDataException("Part28 two disjoint group ledgers did not complete");
        return (root, firstId, otherId,
            Path.Combine(Safety.StorePath(root), first.Hash + ".mp4"),
            Path.Combine(Safety.StorePath(root), other.Hash + ".mp4"), originals);
    }

    private static bool AllOriginalsPreserved(
        Dictionary<string, FileSnapshot> originals)
    {
        foreach (var original in originals.Values)
        {
            var now = Safety.Capture(original.Path);
            if (now.Identity.VolumeSerial != original.Identity.VolumeSerial ||
                now.Identity.LinkCount is not (1 or 2) ||
                now.Length != original.Length ||
                now.CreationUtc != original.CreationUtc ||
                now.ModifiedUtc != original.ModifiedUtc ||
                now.Attributes != original.Attributes ||
                !now.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor) ||
                Hash(now.Path) != original.Sha256 &&
                original.Sha256 is not null)
                return false;
        }
        return true;
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string? old = Journal.TestFolderPath;
        var checkpoints = new List<object>();
        try
        {
            foreach (string phase in Phases)
            {
                var f = CreateFixture(parent, phase);
                Journal.TestFolderPath = Path.Combine(f.Root, "part28-journal");
                string root = f.Root;
                string signal = Path.Combine(root, "part28-killed.checkpoint");
                Directory.CreateDirectory(Path.Combine(root, "part28-evidence"));
                int code = KillWorker(root, f.First, phase, signal);
                bool killed = code != 0 && File.Exists(signal) &&
                    File.ReadAllText(signal) == phase;
                check(killed, "R2.17 part28: actual guarded process kill at " + phase);
                var interrupted = CompletedGroupLedger.Inspect(root);
                string state = interrupted.Entries.Single(e =>
                    e.OperationId == f.First).State;
                string expected = phase is "intent-written" or "stage-validated" ?
                    "InterruptedSharedNeedsRetry" : "InterruptedCutoverNeedsRepair";
                bool isolated = state == expected &&
                    interrupted.Entries.Single(e => e.OperationId == f.Other).State ==
                        "VerifiedCompleted" &&
                    NativeFile.GetIdentity(f.OtherMaster).LinkCount == 4 &&
                    Journal.Pending().Count == 0 &&
                    Journal.UnresolvedRecordFiles().Count == 0;
                check(isolated,
                    "R2.17 part28: read-only original NTFS proof classifies one interrupted group without invalidating a distinct group");

                bool attackedEvidenceRefused = true;
                if (phase == "stage-validated")
                {
                    string stage = Directory.GetFiles(root,
                        ".chappy-part23-undo-*.stage").Single();
                    byte[] pristine = File.ReadAllBytes(stage);
                    File.AppendAllText(stage, "changed-stage-refuse");
                    string afterTamper = Hash(stage);
                    attackedEvidenceRefused =
                        CompletedGroupLedger.Inspect(root).NeedsReview == 1;
                    expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root,
                        f.First, Path.Combine(root, "part28-evidence")),
                        "R2.17 part28: changed staged file must fail closed without cleanup");
                    attackedEvidenceRefused &= File.Exists(stage) &&
                        Hash(stage) == afterTamper;
                    // Only repair our known generated test stage explicitly.
                    File.WriteAllBytes(stage, pristine);
                    // Writing stage may change timestamps; restore only from
                    // the original synthetic alias's exact metadata.
                    var alias = f.Originals.Values.Single(x =>
                        Path.GetFileName(x.Path).Equals("group-a1.mp4",
                            StringComparison.OrdinalIgnoreCase));
                    File.SetCreationTimeUtc(stage, alias.CreationUtc);
                    File.SetLastWriteTimeUtc(stage, alias.ModifiedUtc);
                    attackedEvidenceRefused &=
                        CompletedGroupLedger.Inspect(root).Entries.Single(e =>
                            e.OperationId == f.First).State == expected;

                    // G3-06: valid stage alone is not enough: the saved
                    // per-alias sidecar must match the same original/master
                    // identity and byte evidence, or no replay is permitted.
                    string receipt = Directory.GetFiles(
                        Path.Combine(root, "part28-evidence"),
                        "*.part23-undo.json").Single();
                    byte[] authenticReceipt = File.ReadAllBytes(receipt);
                    File.AppendAllText(receipt, "G3-UNTRUSTED-EVIDENCE");
                    string tamperedReceiptHash = Hash(receipt);
                    expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root,
                        f.First, Path.Combine(root, "part28-evidence")),
                        "G3-06: altered durable per-alias receipt refuses a valid staged Undo");
                    attackedEvidenceRefused &=
                        Hash(receipt) == tamperedReceiptHash &&
                        NativeFile.SameFile(f.FirstMaster,
                            f.Originals.Values.Single(x =>
                                Path.GetFileName(x.Path) == "group-a1.mp4").Path);
                    // Restore only our known test-owned JSON bytes.
                    File.WriteAllBytes(receipt, authenticReceipt);
                }
                if (phase == "post-cutover-before-metadata")
                {
                    string extra = Path.Combine(root, "part28-test-owned-extra.tmp");
                    NativeFile.NewHardLink(extra, f.FirstMaster);
                    bool linked = NativeFile.SameFile(extra, f.FirstMaster);
                    attackedEvidenceRefused =
                        linked && CompletedGroupLedger.Inspect(root).NeedsReview == 1;
                    expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root,
                        f.First, Path.Combine(root, "part28-evidence")),
                        "R2.17 part28: unexplained extra alias blocks cutover metadata repair");
                    if (!linked || Hash(extra) != Hash(f.FirstMaster))
                        throw new InvalidDataException("Part28 cannot identify test-owned extra link");
                    File.Delete(extra);
                    attackedEvidenceRefused &=
                        CompletedGroupLedger.Inspect(root).Entries.Single(e =>
                            e.OperationId == f.First).State == expected;
                }
                check(attackedEvidenceRefused,
                    "R2.17 part28: source/stage/link tampering refuses unattended mutation");

                int firstRestored = CompletedGroupLedger.ResumeUndoSynthetic(root,
                    f.First, Path.Combine(root, "part28-evidence"));
                var afterFirst = CompletedGroupLedger.Inspect(root);
                bool otherUnaffected = firstRestored == 3 &&
                    afterFirst.VerifiedRestored == 1 &&
                    afterFirst.VerifiedCompleted == 1 &&
                    afterFirst.NeedsReview == 0 &&
                    NativeFile.GetIdentity(f.FirstMaster).LinkCount == 2 &&
                    NativeFile.GetIdentity(f.OtherMaster).LinkCount == 4;
                check(otherUnaffected,
                    "R2.17 part28: explicit group recovery rejoins normal Undo without changing the other completed group");
                int otherRestored = CompletedGroupLedger.ResumeUndoSynthetic(root,
                    f.Other, Path.Combine(root, "part28-evidence"));
                var complete = CompletedGroupLedger.Inspect(root);
                bool full = otherRestored == 2 &&
                    complete.VerifiedRestored == 2 &&
                    complete.NeedsReview == 0 &&
                    complete.InterruptedAliases == 0 &&
                    NativeFile.GetIdentity(f.FirstMaster).LinkCount == 2 &&
                    NativeFile.GetIdentity(f.OtherMaster).LinkCount == 2 &&
                    Journal.Pending().Count == 0 &&
                    Journal.UnresolvedRecordFiles().Count == 0;
                foreach (var original in f.Originals.Values)
                {
                    var now = Safety.Capture(original.Path);
                    full &= now.Identity.LinkCount ==
                        (Path.GetFileName(original.Path) is
                         "group-a0.mp4" or "group-b0.mp4" ? 2u : 1u) &&
                        now.Length == original.Length &&
                        now.CreationUtc == original.CreationUtc &&
                        now.ModifiedUtc == original.ModifiedUtc &&
                        now.Attributes == original.Attributes &&
                        now.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor) &&
                        Hash(original.Path) == Hash(
                            Path.Combine(root, Path.GetFileName(original.Path)
                                .StartsWith("group-a", StringComparison.Ordinal) ?
                                "group-a0.mp4" : "group-b0.mp4"));
                }
                // Explicitly hash each fixture against the original group
                // before-merge snapshot reference path (not a replaced file).
                var repA = f.Originals.Values.Single(x =>
                    Path.GetFileName(x.Path) == "group-a0.mp4");
                var repB = f.Originals.Values.Single(x =>
                    Path.GetFileName(x.Path) == "group-b0.mp4");
                string shaA = Hash(repA.Path), shaB = Hash(repB.Path);
                full &= f.Originals.Values.All(x =>
                    Hash(x.Path) == (Path.GetFileName(x.Path).StartsWith(
                        "group-a", StringComparison.Ordinal) ? shaA : shaB));
                check(full,
                    "R2.17 part28: both groups preserve every original byte, owner+DACL, timestamp, attribute, and independent identity");
                expectThrows(() => CompletedGroupLedger.ResumeUndoSynthetic(root,
                    f.First, Path.Combine(root, "part28-evidence")),
                    "R2.17 part28: finished group must refuse double Undo");
                checkpoints.Add(new
                {
                    phase, actualWorkerKilled = killed, interruptedState = state,
                    independentOtherGroup = isolated,
                    evidenceTamperingRefused = attackedEvidenceRefused,
                    explicitRestartAndBothGroupsRestored = full
                });
            }

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-group-intra-alias-crash.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-part28-intra-alias-v1",
                    generatedOnly = true,
                    actualIndependentWorkerKills = checkpoints.Count,
                    exactCheckpoints = checkpoints,
                    supportedGroupCountPerScenario = 2,
                    tamperedStageAndExternalAliasRefused = true,
                    fullOriginalBytesAndOwnerDaclRestored = true,
                    crossLedgerReplayUsesNormalCompiledCore = true,
                    modifiedSavedUndoSidecarRefused = true,
                    productionMergeEnabled = false,
                    productionUndoEnabled = CompletedGroupLedger.ProductionMutationEnabled,
                    realUserMediaTouched = false,
                    realUserCapacityRecovered = 0,
                    truePowerCutProven = false,
                    arbitraryUserSecurityProven = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 PART28 intra-alias real process kills and two-group recovery PASS");
        }
        finally { Journal.TestFolderPath = old; }
    }
}
