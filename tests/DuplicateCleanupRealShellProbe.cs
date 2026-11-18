using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using ChappyStorageCompressor.Core;

/// <summary>
/// One-shot TEST BUILD ONLY probe. It prepares its own isolated NTFS duplicate
/// pair beneath Windows TEMP with the same immutable smoke sentinel, runs the
/// existing begin/stage/report/confirm path, and requests the real Windows
/// Shell Recycle Bin from a dedicated STA. A runner watchdog must kill a
/// hanging dialog. NO real user path is accepted as a CLI argument.
///
/// An inconclusive or unsupported hosted Recycle Bin must NEVER be called
/// PASS. The test report distinguishes independently verified vs unknown.
/// </summary>
internal static class DuplicateCleanupRealShellProbe
{
    private sealed record ProbeResult(
        int Schema, string Verdict, bool RealShellAttempted,
        bool RecyclePairIndependentlyVerified,
        bool KeeperUnchanged, bool OriginalCandidatePathRemainsAbsent,
        bool ProductMutationEnabled, bool RealUserFilesTouched,
        string Reason);

    internal static bool IsProbe(string[] args) =>
        args.Length == 2 && args[0] == "--duplicate-shell-probe";

    internal static int Run(string[] args)
    {
        // An exact opt-in in the Actions runner is required even to create
        // our own fixture for this special test. A shipped product cannot
        // include or call this class.
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("CHAPPY_TEST_CI_SYNTHETIC_RECYCLE") !=
                "EXPLICIT_EPHEMERAL_RUNNER" ||
            Environment.GetEnvironmentVariable("CHAPPY_TEST_REAL_RECYCLE_BIN") !=
                "I_ACCEPT_SYNTHETIC_RECYCLE")
            return 72;

        string report = Path.GetFullPath(args[1]);
        string work = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Environment.CurrentDirectory));
        if (!report.StartsWith(work + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return 73;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        if (File.Exists(report)) return 74;

        bool attempted = false;
        bool sameKeeper = false;
        bool originalAbsent = false;
        bool verified = false;
        string verdict = "NOT_RUN";
        string reason = "No Shell operation started.";
        int exit = 0;
        try
        {
            string parent = Path.Combine(Path.GetTempPath(),
                "ChappyStorageSmoke_ShellE2E_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parent);
            File.WriteAllText(Path.Combine(parent, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            string root = Path.Combine(parent, "GeneratedOnly");
            string keeperDir = Path.Combine(root, "Keep");
            string candidateDir = Path.Combine(root, "Source");
            Directory.CreateDirectory(keeperDir);
            Directory.CreateDirectory(candidateDir);

            // Only two new random names created by this test, max 64KiB.
            string keeperPath = Path.Combine(keeperDir, "GeneratedKeeper.txt");
            string candidatePath = Path.Combine(candidateDir, "GeneratedCandidate.bin");
            byte[] payload = RandomNumberGenerator.GetBytes(64 * 1024);
            File.WriteAllBytes(keeperPath, payload);
            File.WriteAllBytes(candidatePath, payload);
            File.SetCreationTimeUtc(candidatePath, File.GetCreationTimeUtc(keeperPath));
            File.SetLastWriteTimeUtc(candidatePath, File.GetLastWriteTimeUtc(keeperPath));
            string keeperHash = Safety.ComputeSha256(keeperPath, CancellationToken.None);
            FileSnapshot keeperBefore = Safety.Capture(keeperPath);

            var scan = Scanner.Run(root, false, null, CancellationToken.None);
            var group = scan.Groups.Single();
            var plan = DuplicateCleanupPreview.Inspect(root, group, keeperPath);
            if (plan.DeleteCandidates.Count != 1 ||
                !string.Equals(plan.DeleteCandidates[0].OriginalPath,
                    candidatePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Generated fixture scan is not exactly a duplicate pair");

            string id = DuplicateCleanupWorkflow.BeginSynthetic(parent, plan, group);
            DuplicateCleanupWorkflow.StageSynthetic(parent, root, id, 0);
            DuplicateCleanupWorkflow.WriteReportSynthetic(parent, root, id);
            DuplicateCleanupWorkflow.ConfirmSynthetic(parent, root, id, 0, true);

            string intentPath = Path.Combine(root, ".chappy-cleanup-evidence",
                id + ".intent.json");
            var immutable = JsonSerializer.Deserialize<DuplicateCleanupWorkflow.Intent>(
                File.ReadAllBytes(intentPath)) ??
                throw new InvalidDataException("Generated test intent missing");
            var candidate = immutable.Candidates.Single();
            if (candidate.Original.Length != payload.Length ||
                !File.Exists(candidate.DestinationPath) ||
                File.Exists(candidate.SourcePath))
                throw new InvalidDataException("Generated-only staging failed");

            // The worker's real Shell call may block on an OS dialog.
            // PowerShell Actions step terminates this ENTIRE child process
            // after 25 seconds; no automatic retry is allowed.
            Exception? shellError = null;
            DateTime startUtc = DateTime.UtcNow;
            var thread = new Thread(() =>
            {
                try
                {
                    DuplicateCleanupWorkflow.RecycleViaShellSynthetic(parent,
                        root, id, 0);
                }
                catch (Exception ex) { shellError = ex; }
            })
            {
                Name = "Disposable NTFS Recycle Bin STA probe",
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            attempted = true;
            Console.WriteLine("Real Shell probe starting on OWN generated fixture only.");
            Console.Out.Flush();
            thread.Start();
            thread.Join();

            sameKeeper = Safety.SameSnapshot(keeperBefore) &&
                string.Equals(Safety.ComputeSha256(keeperPath, CancellationToken.None),
                    keeperHash, StringComparison.OrdinalIgnoreCase);
            originalAbsent = !File.Exists(candidatePath) &&
                !Directory.Exists(candidatePath);

            string receipt = Path.Combine(root, ".chappy-cleanup-evidence",
                id + ".item-0000.recycled.json");
            string shellIntent = Path.Combine(root, ".chappy-cleanup-evidence",
                id + ".item-0000.recycle-intent.json");
            string shellOutcome = Path.Combine(root, ".chappy-cleanup-evidence",
                id + ".item-0000.shell-outcome.json");

            RecycleBinEvidence.Finding proof =
                RecycleBinEvidence.VerifyForCurrentUser(candidate.DestinationPath,
                    candidate.Original, startUtc, DateTime.UtcNow);
            verified = proof.Verified && File.Exists(shellIntent) &&
                File.Exists(shellOutcome) && File.Exists(receipt);
            if (!sameKeeper || !originalAbsent)
            {
                verdict = "SAFETY_FAILURE";
                reason = "Keeper or pre-staging source was unexpectedly changed.";
                exit = 86;
            }
            else if (verified)
            {
                verdict = "VERIFIED_SYNTHETIC_RECYCLE_PAIR";
                reason = "Windows Shell and independent $I/$R + NTFS ID + SHA evidence agreed.";
            }
            else
            {
                verdict = "INCONCLUSIVE_MANUAL_REVIEW";
                reason = shellError is null
                    ? "Real Shell returned, but independent $I/$R proof is incomplete: " +
                      proof.Reason
                    : "Real Shell raised " + shellError.GetType().Name +
                      "; no verified Recycle Bin receipt. " + proof.Reason;
            }
            // A failed/ambiguous Shell operation is NOT retried or rolled
            // back automatically, and its generated staged file is untouched.
        }
        catch (Exception ex)
        {
            verdict = "SAFETY_FAILURE";
            reason = "Unanticipated synthetic E2E error: " +
                ex.GetType().Name + ": " + ex.Message;
            exit = 85;
        }
        var result = new ProbeResult(1, verdict, attempted, verified,
            sameKeeper, originalAbsent,
            DuplicateCleanupWorkflow.ProductionMutationEnabled,
            false, reason);
        try
        {
            using var stream = new FileStream(report, FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(result,
                new JsonSerializerOptions { WriteIndented = true });
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not persist shell probe evidence: " + ex);
            return 87;
        }
        Console.WriteLine(JsonSerializer.Serialize(result));
        return exit;
    }
}
