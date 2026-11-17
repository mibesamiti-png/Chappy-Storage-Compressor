using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ChappyStorageCompressor.Core;

// A+B: core run-through on explicitly owned, generated NTFS data.
// Product mutations remain compiled out. The fake recycle directory is
// emphatically NOT Windows Recycle Bin; Windows shell acceptance is still C.
internal static class DuplicateCleanupBatchTests
{
    private static string Hash(string path) =>
        Safety.ComputeSha256(path, CancellationToken.None);

    private static string CreateRoot(string owned, string name, int count,
        bool priorCollision = false)
    {
        string folder = Path.Combine(owned, name);
        Directory.CreateDirectory(folder);
        byte[] payload = RandomNumberGenerator.GetBytes(240 * 1024);
        for (int i = 0; i < count; i++)
        {
            string sub = Path.Combine(folder, i == 0 ? "Keep" : "Source" + i);
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "content." + (i == 0 ? "txt" : "bin")),
                payload);
        }
        if (priorCollision)
        {
            string existing = Path.Combine(folder, "同名ファイル");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "prior.txt"), "user-existing-collection");
            File.WriteAllText(Path.Combine(folder, "同名ファイル一覧.txt"),
                "user-existing-report");
        }
        return folder;
    }

    private static (DuplicateCleanupPreview.Result Plan, DuplicateGroup Group,
        FileSnapshot Keeper, string Hash) Scan(string root)
    {
        var result = Scanner.Run(root, false, null, CancellationToken.None);
        var group = result.Groups.Single();
        var keep = group.Files.Single(x => x.Path.Contains("Keep" +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        var plan = DuplicateCleanupPreview.Inspect(root, group, keep.Path);
        return (plan, group, keep, Hash(keep.Path));
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string testRoot = Path.Combine(parent,
            "ChappyStorageSmoke_DuplicateCleanup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        File.WriteAllText(Path.Combine(testRoot, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");

        check(!DuplicateCleanupWorkflow.ProductionMutationEnabled,
            "A+B: normal product cannot stage/recycle user files");

        string root = CreateRoot(testRoot, "multi-candidate", 3, priorCollision: true);
        var (plan, group, keeper, beforeHash) = Scan(root);
        string priorFile = Path.Combine(root, "同名ファイル", "prior.txt");
        string priorMemo = Path.Combine(root, "同名ファイル一覧.txt");
        check(plan.CollectionRelativePath ==
                  Path.Combine("同名ファイル", "同名ファイル") &&
              plan.ReportRelativePath == "同名ファイル一覧 (1).txt",
            "A: existing collision collection and memo trigger nesting and report numbering");

        string id = DuplicateCleanupWorkflow.BeginSynthetic(testRoot, plan, group);
        check(group.Files.All(f => File.Exists(f.Path)) &&
              !File.Exists(Path.Combine(root, plan.ReportRelativePath)) &&
              File.Exists(Path.Combine(root, ".chappy-cleanup-evidence",
                  id + ".intent.json")),
            "A: durable intent recorded before changing any synthetic source");

        bool crashSeen = false;
        try
        {
            DuplicateCleanupWorkflow.StageSynthetic(testRoot, root, id, 0,
                checkpoint: _ => throw new IOException("injected after rename"));
        }
        catch (IOException) { crashSeen = true; }
        var first = plan.DeleteCandidates[0];
        string staged0 = Path.Combine(root, first.ProposedCollectionRelativePath);
        var interrupted = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, root, id);
        check(crashSeen && !File.Exists(first.OriginalPath) &&
              File.Exists(staged0) &&
              interrupted[0].State == "InterruptedAfterStageRecoverable" &&
              File.Exists(keeper.Path) && Hash(keeper.Path) == beforeHash,
            "A: after-rename crash keeps original bytes/ID and is classified before receipt");
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, root, id, 0);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, root, id, 1);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, root, id, 1);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, root, id);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, root, id);
        string report = Path.Combine(root, plan.ReportRelativePath);
        check(File.ReadAllText(priorFile) == "user-existing-collection" &&
              File.ReadAllText(priorMemo) == "user-existing-report" &&
              File.ReadAllText(report).Contains(first.OriginalPath, StringComparison.Ordinal) &&
              plan.DeleteCandidates.All(x => File.Exists(Path.Combine(root,
                  x.ProposedCollectionRelativePath))) &&
              keeper.Identity.SameFile(NativeFile.GetIdentity(keeper.Path)) &&
              Safety.SameSnapshot(keeper) && Hash(keeper.Path) == beforeHash,
            "A: nested collection, numbered readable memo and keeper survive staged moves");

        expectThrows(() => DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(
            testRoot, root, id, 0), "B: unconfirmed staged duplicate never enters fake recycle");

        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, root, id, 0, true);
        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, root, id, 0, false);
        var canceled = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, root, id);
        check(!canceled[0].Confirmed && canceled[0].State == "StagedUnconfirmed",
            "B: persistent cancellation of prior confirmation blocks simulated recycle");
        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, root, id, 0, true);
        var reviewed = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, root, id);
        check(reviewed[0].Confirmed && !reviewed[1].Confirmed &&
              reviewed[0].State == "ConfirmedStaged" &&
              reviewed[1].State == "StagedUnconfirmed",
            "B: per-file confirmed state survives fresh journal inspection");
        expectThrows(() => DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(
            testRoot, root, id, 1), "B: nonselected second candidate cannot be recycled");

        // First checkpoint: intent exists but bytes have not moved. Never
        // silently redo a partly started irreversible operation.
        bool abort = false;
        try
        {
            DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(testRoot, root, id, 0,
                checkpoint: stage =>
                {
                    if (stage == "after-recycle-intent-before-rename")
                        throw new IOException("intent-only simulated crash");
                });
        }
        catch (IOException) { abort = true; }
        var unknown = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, root, id);
        check(abort && unknown[0].State == "RecycleIntentRequiresManualReview" &&
              File.Exists(staged0) && Hash(staged0) == beforeHash &&
              File.Exists(keeper.Path),
            "B: interrupted recycle intent forces manual review, never blind replay");
        expectThrows(() => DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(
            testRoot, root, id, 0), "B: repeated recycle attempt after unknown checkpoint is refused");

        string normal = CreateRoot(testRoot, "successful-fake-recycle", 2);
        var (secondPlan, secondGroup, secondKeep, secondHash) = Scan(normal);
        string secondId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, secondPlan, secondGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, normal, secondId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, normal, secondId);
        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, normal, secondId, 0, true);
        DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(testRoot, normal, secondId, 0);
        string fake = Path.Combine(testRoot, ".chappy-test-recycle",
            secondId + "-0000.bin");
        var done = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, normal, secondId);
        check(File.Exists(fake) && Hash(fake) == secondHash &&
              !File.Exists(secondPlan.DeleteCandidates[0].OriginalPath) &&
              !File.Exists(Path.Combine(normal,
                  secondPlan.DeleteCandidates[0].ProposedCollectionRelativePath)) &&
              done[0].State == "SimulatedRecycleReceiptRequiresReview" &&
              Safety.SameSnapshot(secondKeep) &&
              NativeFile.GetIdentity(fake).LinkCount == 1,
            "B: synthetic-only fake recycle moves one confirmed independent file; keeper unchanged");
        expectThrows(() => DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(
            testRoot, normal, secondId, 0),
            "B: completed simulated recycle refuses duplicate execution");

        string denied = CreateRoot(testRoot, "stale-source", 2);
        var (deniedPlan, deniedGroup, deniedKeep, _) = Scan(denied);
        File.AppendAllText(deniedPlan.DeleteCandidates[0].OriginalPath, "changed");
        expectThrows(() => DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, deniedPlan, deniedGroup),
            "A: changed source after scan rejects journal start and does not stage data");
        check(File.Exists(deniedKeep.Path) &&
              Directory.GetFileSystemEntries(denied)
                .All(x => Path.GetFileName(x) != ".chappy-cleanup-evidence"),
            "A: stale-plan rejection leaves no orphan cleanup evidence");

        // Never allow the generated test API on ordinary folders without a
        // sentinel and a unique temporary root.
        expectThrows(() => DuplicateCleanupWorkflow.BeginSynthetic(
            parent, secondPlan, secondGroup),
            "A+B: cleanup writer refuses unmarked caller root");

        Console.WriteLine("A+B DUPLICATE CLEANUP synthetic stage/review/recycle gate PASS; real Windows Bin not tested");
    }
}
