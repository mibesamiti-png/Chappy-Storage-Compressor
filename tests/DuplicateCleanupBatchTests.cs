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


        // C1: restoration is an ID-pinned, no-overwrite reverse transaction.
        // A durable intent always precedes rename; restart must reconcile
        // original AND staged names before doing anything further.
        string back = CreateRoot(testRoot, "restore-after-intent", 3);
        var (backPlan, backGroup, backKeep, backHash) = Scan(back);
        string backId = DuplicateCleanupWorkflow.BeginSynthetic(testRoot, backPlan, backGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, back, backId, 0);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, back, backId, 1);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, back, backId);
        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, back, backId, 0, true);
        string backSource = backPlan.DeleteCandidates[0].OriginalPath;
        string backStaged = Path.Combine(back, backPlan.DeleteCandidates[0].ProposedCollectionRelativePath);
        bool afterIntent = false;
        try
        {
            DuplicateCleanupWorkflow.RestoreSynthetic(testRoot, back, backId, 0,
                checkpoint: stage =>
                {
                    if (stage == "after-restore-intent-before-rename")
                        throw new IOException("synthetic restore after-intent crash");
                });
        }
        catch (IOException) { afterIntent = true; }
        var intentOnly = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, back, backId);
        check(afterIntent && intentOnly[0].State == "RestoreIntentRecorded" &&
              !File.Exists(backSource) && File.Exists(backStaged),
            "C: aborted restore intent is durable but staging remains untouched");
        DuplicateCleanupWorkflow.RestoreSynthetic(testRoot, back, backId, 0);
        DuplicateCleanupWorkflow.RestoreSynthetic(testRoot, back, backId, 0);
        var backStates = DuplicateCleanupWorkflow.InspectSynthetic(testRoot, back, backId);
        check(backStates[0].State == "RestoredOriginal" &&
              backStates[1].State == "StagedUnconfirmed" &&
              File.Exists(backSource) && !File.Exists(backStaged) &&
              Hash(backSource) == backHash && Safety.SameSnapshot(backKeep),
            "C: verified restore replays the intent once, retains keeper and unrelated staged candidate");
        expectThrows(() => DuplicateCleanupWorkflow.StageSynthetic(
            testRoot, back, backId, 0),
            "C: restored original cannot silently be staged again");
        expectThrows(() => DuplicateCleanupWorkflow.ConfirmSynthetic(
            testRoot, back, backId, 0, true),
            "C: restored original cannot be confirmed for recycle");
        expectThrows(() => DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(
            testRoot, back, backId, 0),
            "C: restored original cannot be recycled again");
        expectThrows(() => DuplicateCleanupWorkflow.WriteReportSynthetic(
            testRoot, back, backId),
            "C: original report cannot be rewritten after an item was restored");

        string afterRenameRoot = CreateRoot(testRoot, "restore-after-rename", 2);
        var (renamePlan, renameGroup, renameKeeper, renameHash) = Scan(afterRenameRoot);
        string renameId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, renamePlan, renameGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, afterRenameRoot, renameId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, afterRenameRoot, renameId);
        bool afterRename = false;
        try
        {
            DuplicateCleanupWorkflow.RestoreSynthetic(testRoot, afterRenameRoot, renameId, 0,
                checkpoint: stage =>
                {
                    if (stage == "after-restore-rename-before-receipt")
                        throw new IOException("synthetic restore post-rename crash");
                });
        }
        catch (IOException) { afterRename = true; }
        var renameStates = DuplicateCleanupWorkflow.InspectSynthetic(
            testRoot, afterRenameRoot, renameId);
        check(afterRename && renameStates[0].State == "InterruptedAfterRestoreRecoverable" &&
              File.Exists(renamePlan.DeleteCandidates[0].OriginalPath) &&
              !File.Exists(Path.Combine(afterRenameRoot,
                  renamePlan.DeleteCandidates[0].ProposedCollectionRelativePath)),
            "C: crash after reverse rename classifies original-by-ID as recoverable");
        DuplicateCleanupWorkflow.RestoreSynthetic(testRoot, afterRenameRoot, renameId, 0);
        check(DuplicateCleanupWorkflow.InspectSynthetic(
                  testRoot, afterRenameRoot, renameId)[0].State == "RestoredOriginal" &&
              Safety.SameSnapshot(renameKeeper) &&
              Hash(renamePlan.DeleteCandidates[0].OriginalPath) == renameHash,
            "C: receipt repaired only for verified exact original file");

        string occupiedRoot = CreateRoot(testRoot, "restore-name-occupied", 2);
        var (occupiedPlan, occupiedGroup, occupiedKeeper, _) = Scan(occupiedRoot);
        string occupiedId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, occupiedPlan, occupiedGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, occupiedRoot, occupiedId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, occupiedRoot, occupiedId);
        string occupied = occupiedPlan.DeleteCandidates[0].OriginalPath;
        string stashed = Path.Combine(occupiedRoot,
            occupiedPlan.DeleteCandidates[0].ProposedCollectionRelativePath);
        File.WriteAllText(occupied, "FOREIGN USER FILE MUST NOT BE REPLACED");
        string foreignHash = Hash(occupied);
        expectThrows(() => DuplicateCleanupWorkflow.RestoreSynthetic(
            testRoot, occupiedRoot, occupiedId, 0),
            "C: occupied original path blocks restoration without overwrite");
        check(File.Exists(stashed) && Hash(occupied) == foreignHash &&
              Safety.SameSnapshot(occupiedKeeper) &&
              DuplicateCleanupWorkflow.InspectSynthetic(
                  testRoot, occupiedRoot, occupiedId)[0].State == "ManualReview",
            "C: occupied original remains and source is preserved for manual review");
        File.Delete(occupied); // generated foreign fixture only
        DuplicateCleanupWorkflow.RestoreSynthetic(testRoot, occupiedRoot, occupiedId, 0);
        check(File.Exists(occupied) && !File.Exists(stashed),
            "C: after removing synthetic blocker, verified original can be restored");

        string tamperRoot = CreateRoot(testRoot, "tampered-evidence", 2);
        var (tamperPlan, tamperGroup, tamperKeeper, _) = Scan(tamperRoot);
        string tamperId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, tamperPlan, tamperGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, tamperRoot, tamperId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, tamperRoot, tamperId);
        string tamperStage = Path.Combine(tamperRoot,
            tamperPlan.DeleteCandidates[0].ProposedCollectionRelativePath);
        File.AppendAllText(Path.Combine(tamperRoot, tamperPlan.ReportRelativePath),
            "unexpected unrecorded edit");
        expectThrows(() => DuplicateCleanupWorkflow.ConfirmSynthetic(
            testRoot, tamperRoot, tamperId, 0, true),
            "C: altered human-readable report refuses confirmation");
        expectThrows(() => DuplicateCleanupWorkflow.RestoreSynthetic(
            testRoot, tamperRoot, tamperId, 0),
            "C: altered report cannot authorize any original restore");
        check(File.Exists(tamperStage) && Safety.SameSnapshot(tamperKeeper),
            "C: report tampering leaves staged original bytes and keeper alone");

        string forgedRoot = CreateRoot(testRoot, "forged-stage-receipt", 2);
        var (forgedPlan, forgedGroup, forgedKeeper, _) = Scan(forgedRoot);
        string forgedId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, forgedPlan, forgedGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, forgedRoot, forgedId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, forgedRoot, forgedId);
        string stageReceipt = Path.Combine(forgedRoot, ".chappy-cleanup-evidence",
            forgedId + ".item-0000.staged.json");
        File.WriteAllText(stageReceipt, "{}");
        expectThrows(() => DuplicateCleanupWorkflow.ConfirmSynthetic(
            testRoot, forgedRoot, forgedId, 0, true),
            "C: tampered immutable stage receipt cannot authorize recycle");
        expectThrows(() => DuplicateCleanupWorkflow.RestoreSynthetic(
            testRoot, forgedRoot, forgedId, 0),
            "C: forged stage receipt prevents unverified restore");
        check(Safety.SameSnapshot(forgedKeeper) &&
              File.Exists(Path.Combine(forgedRoot,
                  forgedPlan.DeleteCandidates[0].ProposedCollectionRelativePath)),
            "C: refused forged evidence keeps staged data intact");

        expectThrows(() => DuplicateCleanupWorkflow.RestoreSynthetic(
            testRoot, normal, secondId, 0),
            "C: fake-recycled original cannot be silently restored by staging API");


        // C2: product-readable history is a non-authoritative, bounded view.
        // It must never promote an unverified position to file-ID proof.
        check(!DuplicateCleanupHistory.CanModifyUserFiles &&
              DuplicateCleanupHistory.Inspect(root).Count == 2 &&
              DuplicateCleanupHistory.Inspect(root).All(x =>
                  x.State.Contains("ごみ箱", StringComparison.Ordinal) ||
                  x.State.Contains("保留先", StringComparison.Ordinal) ||
                  x.State.Contains("要手動確認", StringComparison.Ordinal)),
            "C: product history reads completed synthetic journals without mutation permission");
        check(DuplicateCleanupHistory.Inspect(back).Count == 2 &&
              DuplicateCleanupHistory.Inspect(back).Any(x =>
                  x.State.Contains("元パス復帰の記録あり", StringComparison.Ordinal)),
            "C: read-only history recognizes recorded original-path return without claiming ID proof");

        string shellRoot = CreateRoot(testRoot, "shell-recycle-error", 2);
        var (shellPlan, shellGroup, shellKeeper, shellHash) = Scan(shellRoot);
        string shellId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, shellPlan, shellGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, shellRoot, shellId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, shellRoot, shellId);
        string shellStage = Path.Combine(shellRoot,
            shellPlan.DeleteCandidates[0].ProposedCollectionRelativePath);
        expectThrows(() => DuplicateCleanupWorkflow.RecycleViaShellSynthetic(
            testRoot, shellRoot, shellId, 0,
            testTransport: _ => throw new IOException("fake shell unavailable")),
            "C: unconfirmed synthetic candidate cannot reach shell transport");
        check(!File.Exists(Path.Combine(shellRoot, ".chappy-cleanup-evidence",
                  shellId + ".item-0000.recycle-intent.json")) &&
              File.Exists(shellStage),
            "C: unconfirmed shell attempt writes no recycle intent and preserves candidate");

        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, shellRoot, shellId, 0, true);
        int transportCalls = 0;
        bool failure = false;
        try
        {
            DuplicateCleanupWorkflow.RecycleViaShellSynthetic(testRoot, shellRoot,
                shellId, 0, testTransport: _ =>
                {
                    transportCalls++;
                    throw new IOException("injected shell recycle failure");
                });
        }
        catch (IOException) { failure = true; }
        check(failure && transportCalls == 1 &&
              File.Exists(shellStage) && Hash(shellStage) == shellHash &&
              Safety.SameSnapshot(shellKeeper) &&
              DuplicateCleanupWorkflow.InspectSynthetic(testRoot, shellRoot, shellId)[0].State ==
                  "ShellRecycleOutcomeRequiresManualReview" &&
              DuplicateCleanupHistory.Inspect(shellRoot).Single().State.Contains(
                  "ごみ箱処理開始", StringComparison.Ordinal),
            "C: shell failure persists intent/outcome, never deletes or claims recycle success");
        expectThrows(() => DuplicateCleanupWorkflow.RecycleViaShellSynthetic(
            testRoot, shellRoot, shellId, 0, _ => transportCalls++),
            "C: ambiguous shell attempt can never be replayed automatically");
        expectThrows(() => DuplicateCleanupWorkflow.RestoreSynthetic(
            testRoot, shellRoot, shellId, 0),
            "C: shell-attempted staged data cannot auto-restore without independent review");
        check(transportCalls == 1 && File.Exists(shellStage),
            "C: blocked retry cannot invoke shell or alter the staged file");

        string noopRoot = CreateRoot(testRoot, "shell-returns-without-moving", 2);
        var (noopPlan, noopGroup, noopKeeper, _) = Scan(noopRoot);
        string noopId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, noopPlan, noopGroup);
        DuplicateCleanupWorkflow.StageSynthetic(testRoot, noopRoot, noopId, 0);
        DuplicateCleanupWorkflow.WriteReportSynthetic(testRoot, noopRoot, noopId);
        DuplicateCleanupWorkflow.ConfirmSynthetic(testRoot, noopRoot, noopId, 0, true);
        DuplicateCleanupWorkflow.RecycleViaShellSynthetic(testRoot, noopRoot, noopId, 0,
            testTransport: _ => { }); // no-op transport, NEVER Windows shell
        string noopStage = Path.Combine(noopRoot,
            noopPlan.DeleteCandidates[0].ProposedCollectionRelativePath);
        check(File.Exists(noopStage) && Safety.SameSnapshot(noopKeeper) &&
              DuplicateCleanupWorkflow.InspectSynthetic(testRoot, noopRoot, noopId)[0].State ==
                  "ShellRecycleOutcomeRequiresManualReview" &&
              !File.Exists(Path.Combine(noopRoot, ".chappy-cleanup-evidence",
                  noopId + ".item-0000.recycled.json")),
            "C: shell transport returning is NOT treated as recycle bin proof");

        // C3: Independent Windows $I/$R proof. Test ONLY a generated fake
        // recycle directory; no actual user Recycle Bin is opened by CI.
        string proofSource = Path.Combine(testRoot, "forensic-source.txt");
        File.WriteAllBytes(proofSource, RandomNumberGenerator.GetBytes(64 * 1024));
        var proofSnapshot = Safety.Capture(proofSource);
        string proofHash = Hash(proofSource);
        var proofEvidence = new DuplicateCleanupWorkflow.FileEvidence(
            proofSource, proofSnapshot.Length, proofSnapshot.CreationUtc,
            proofSnapshot.ModifiedUtc, proofSnapshot.Attributes,
            proofSnapshot.Identity.VolumeSerial, proofSnapshot.Identity.FileIndex,
            Convert.ToHexString(SHA256.HashData(proofSnapshot.SecurityDescriptor)),
            proofHash);
        string fakeBin = Path.Combine(testRoot, "synthetic-pair-only");
        Directory.CreateDirectory(fakeBin);
        string iPath = Path.Combine(fakeBin, "$IABCDEF.txt");
        string rPath = Path.Combine(fakeBin, "$RABCDEF.txt");
        DateTime windowBegin = DateTime.UtcNow.AddMinutes(-1);
        File.Move(proofSource, rPath); // generated file only
        DateTime deleted = DateTime.UtcNow;
        static void WriteIndex(string destination, string originalPath, long length,
            DateTime deletedUtc, bool legacy)
        {
            using var mem = new MemoryStream();
            using (var writer = new BinaryWriter(mem, Encoding.Unicode, leaveOpen: true))
            {
                writer.Write(legacy ? 1L : 2L);
                writer.Write(length);
                writer.Write(deletedUtc.ToFileTimeUtc());
                if (legacy)
                {
                    if (originalPath.Length > 259)
                        throw new InvalidOperationException("Generated v1 path too long");
                    byte[] chars = Encoding.Unicode.GetBytes(originalPath + "\0");
                    writer.Write(chars);
                    writer.Write(new byte[520 - chars.Length]);
                }
                else
                {
                    writer.Write(originalPath.Length + 1);
                    writer.Write(Encoding.Unicode.GetBytes(originalPath + "\0"));
                }
            }
            File.WriteAllBytes(destination, mem.ToArray());
        }
        WriteIndex(iPath, proofSource, proofEvidence.Length, deleted, legacy: false);
        var v2 = RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
            proofEvidence, windowBegin, DateTime.UtcNow);
        check(v2.Verified && v2.IndexPath == iPath && v2.RecycledPath == rPath &&
              NativeFile.GetIdentity(rPath).SameFile(proofSnapshot.Identity) &&
              Hash(rPath) == proofHash,
            "C: independent v2 recycle index and data require same original path, NTFS ID, SHA and bytes");
        var wrongId = RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
            proofEvidence with { FileIndex = proofEvidence.FileIndex + 1 },
            windowBegin, DateTime.UtcNow);
        var wrongHash = RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
            proofEvidence with { Sha256 = new string('0', 64) },
            windowBegin, DateTime.UtcNow);
        check(!wrongId.Verified && !wrongHash.Verified,
            "C: recycled data with the right name cannot pass mismatched ID or SHA");
        var wrongTime = RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
            proofEvidence, DateTime.UtcNow.AddHours(3), DateTime.UtcNow.AddHours(4));
        check(!wrongTime.Verified,
            "C: stale recycle timestamp cannot be mistaken for current operation");
        File.WriteAllBytes(iPath, new byte[] { 2, 0, 0 });
        check(!RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
                  proofEvidence, windowBegin, DateTime.UtcNow).Verified,
            "C: truncated index is never proof");
        WriteIndex(iPath, proofSource, proofEvidence.Length, deleted, legacy: true);
        check(RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
                  proofEvidence, windowBegin, DateTime.UtcNow).Verified,
            "C: fixed-length v1 index also requires same independent proof");
        string otherI = Path.Combine(fakeBin, "$I012345.txt");
        File.Copy(iPath, otherI);
        check(!RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
                  proofEvidence, windowBegin, DateTime.UtcNow).Verified,
            "C: multiple original-path indexes are ambiguous, never promoted");
        File.Delete(otherI); // generated index only
        string parked = Path.Combine(fakeBin, "temporarily-parked.bin");
        File.Move(rPath, parked);
        check(!RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
                  proofEvidence, windowBegin, DateTime.UtcNow).Verified,
            "C: missing matching $R payload refuses proof even if $I looks correct");
        File.Move(parked, rPath);
        File.WriteAllBytes(proofSource, Encoding.UTF8.GetBytes("foreign original occupant"));
        check(!RecycleBinEvidence.VerifyInDirectory(fakeBin, proofSource,
                  proofEvidence, windowBegin, DateTime.UtcNow).Verified,
            "C: recycled source name occupied again cannot be treated as verified");

        string malformedRoot = CreateRoot(testRoot, "history-malformed-intent", 2);
        var (malformedPlan, malformedGroup, malformedKeeper, _) = Scan(malformedRoot);
        string malformedId = DuplicateCleanupWorkflow.BeginSynthetic(
            testRoot, malformedPlan, malformedGroup);
        string malformedIntent = Path.Combine(malformedRoot, ".chappy-cleanup-evidence",
            malformedId + ".intent.json");
        File.WriteAllText(malformedIntent, "{}");
        var malformedRows = DuplicateCleanupHistory.Inspect(malformedRoot);
        check(malformedRows.Count == 1 && malformedRows[0].State == "要手動確認" &&
              malformedRows[0].OriginalPath == "" &&
              Safety.SameSnapshot(malformedKeeper),
            "C: corrupted journal is shown as an error without trusting its paths");

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
