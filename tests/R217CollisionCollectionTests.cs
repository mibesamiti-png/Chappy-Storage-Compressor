using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// Generated-only collision selection and output verification. Future archive
// extraction and completed-ledger Undo are deliberately NOT wired to this yet.
internal static class R217CollisionCollectionTests
{
    private static string Hash(string p) =>
        Safety.ComputeSha256(p, CancellationToken.None);

    private static string Source(string parent, string name)
    {
        string folder = Path.Combine(parent, "part34-incoming");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(96 * 1024));
        return path;
    }
    private static string Work(string parent, string name)
    {
        string path = Path.Combine(parent, "r217-part34-" + name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string srcA = Source(parent, "part34-a.mp4");
        string srcB = Source(parent, "part34-b.mp4");
        string srcC = Source(parent, "part34-c.mp4");
        string srcD = Source(parent, "part34-d.mp4");
        string originalA = Hash(srcA);
        string originalB = Hash(srcB);
        string originalC = Hash(srcC);
        string originalD = Hash(srcD);
        var report = new List<object>();

        string root = Work(parent, "duplicate-subdirectories");
        string oldDir = Path.Combine(root, "Camera");
        Directory.CreateDirectory(oldDir);
        string old = Path.Combine(oldDir, "clip.mp4");
        File.WriteAllBytes(old, RandomNumberGenerator.GetBytes(113 * 1024));
        string oldHash = Hash(old);
        FileIdentity oldId = NativeFile.GetIdentity(old);
        var incoming = new (string Source, string Relative)[]
        {
            (srcA, Path.Combine("Camera", "clip.mp4")),
            (srcB, Path.Combine("Camera", "clip.mp4")),
            (srcC, Path.Combine("Trip", "clip.mp4")),
            (srcD, Path.Combine("Trip", "extra.mp4"))
        };
        var plan = CollisionCollectionPlan.Inspect(root,
            incoming.Select(x => x.Relative));
        string aRelative = Path.Combine("同名ファイル", "Camera", "clip (1).mp4");
        string bRelative = Path.Combine("同名ファイル", "Camera", "clip (2).mp4");
        check(plan.Conflicts == 2 && plan.Entries.Count == 4 &&
              plan.CollectionRelativePath == "同名ファイル" &&
              plan.ReportRelativePath == "同名ファイル一覧.txt" &&
              plan.Entries[0].DestinationRelativePath == aRelative &&
              plan.Entries[1].DestinationRelativePath == bRelative &&
              !plan.Entries[2].Collected &&
              !plan.Entries[3].Collected &&
              !plan.CanModifyUserFiles &&
              !CollisionCollectionPlan.ProductionExtractionOrUndoEnabled &&
              !Directory.Exists(Path.Combine(root, "同名ファイル")),
              "R2.17 Part34: plan-only central folder, duplicate numbering and source subdirectory preservation (no write)");
        CollisionCollectionPlan.ExerciseSynthetic(parent, plan, incoming);
        string textReport = File.ReadAllText(
            Path.Combine(root, plan.ReportRelativePath), Encoding.UTF8);
        bool preserved = Hash(old) == oldHash &&
            NativeFile.GetIdentity(old).SameFile(oldId) &&
            Hash(Path.Combine(root, aRelative)) == originalA &&
            Hash(Path.Combine(root, bRelative)) == originalB &&
            Hash(Path.Combine(root, "Trip", "clip.mp4")) == originalC &&
            Hash(Path.Combine(root, "Trip", "extra.mp4")) == originalD &&
            textReport.Contains("Camera" + Path.DirectorySeparatorChar +
                "clip.mp4", StringComparison.Ordinal) &&
            textReport.Contains(aRelative, StringComparison.Ordinal) &&
            textReport.Contains(bRelative, StringComparison.Ordinal) &&
            textReport.Contains("同名件数: 2", StringComparison.Ordinal) &&
            Hash(srcA) == originalA && Hash(srcB) == originalB &&
            Hash(srcC) == originalC && Hash(srcD) == originalD;
        check(preserved,
            "R2.17 Part34: real generated extraction saves conflict files (1)/(2), preserves existing NTFS identity and UTF-8 root report");
        report.Add(new { name = "two-same-name-entries-and-original", pass = preserved });

        string nestedRoot = Work(parent, "nested-collection");
        string priorFolder = Path.Combine(nestedRoot, "同名ファイル");
        string nestedFolder = Path.Combine(priorFolder, "同名ファイル");
        Directory.CreateDirectory(nestedFolder);
        string oldA = Path.Combine(priorFolder, "previous.mp4");
        string oldB = Path.Combine(nestedFolder, "previous.mp4");
        File.WriteAllBytes(oldA, RandomNumberGenerator.GetBytes(13 * 1024));
        File.WriteAllBytes(oldB, RandomNumberGenerator.GetBytes(19 * 1024));
        string oldAHash = Hash(oldA), oldBHash = Hash(oldB);
        string oldMemo = Path.Combine(nestedRoot, "同名ファイル一覧.txt");
        File.WriteAllText(oldMemo, "既存の利用者メモ。上書き禁止。");
        string relative = Path.Combine("Camera", "scene.mp4");
        Directory.CreateDirectory(Path.Combine(nestedRoot, "Camera"));
        File.Copy(srcA, Path.Combine(nestedRoot, relative));
        string nestedOriginal = Hash(Path.Combine(nestedRoot, relative));
        var nestedPlan = CollisionCollectionPlan.Inspect(nestedRoot, [relative]);
        string nestedCollection = Path.Combine("同名ファイル", "同名ファイル",
            "同名ファイル");
        check(nestedPlan.CollectionRelativePath == nestedCollection &&
              nestedPlan.Entries.Single().DestinationRelativePath ==
                  Path.Combine(nestedCollection, "Camera", "scene (1).mp4") &&
              nestedPlan.ReportRelativePath == "同名ファイル一覧 (1).txt" &&
              File.ReadAllText(oldMemo) == "既存の利用者メモ。上書き禁止。" &&
              !Directory.Exists(Path.Combine(nestedRoot, nestedCollection)),
              "R2.17 Part34: requested 同名ファイル/同名ファイル nested folder and numbered memo never overwrite existing collections");
        CollisionCollectionPlan.ExerciseSynthetic(parent, nestedPlan,
            [(srcB, relative)]);
        bool nestedOkay = Hash(oldA) == oldAHash &&
            Hash(oldB) == oldBHash &&
            Hash(Path.Combine(nestedRoot, relative)) == nestedOriginal &&
            Hash(Path.Combine(nestedRoot,
                nestedPlan.Entries.Single().DestinationRelativePath)) ==
                originalB &&
            File.ReadAllText(oldMemo) == "既存の利用者メモ。上書き禁止。" &&
            File.ReadAllText(Path.Combine(nestedRoot,
                nestedPlan.ReportRelativePath)).Contains("scene (1).mp4",
                    StringComparison.Ordinal);
        check(nestedOkay,
            "R2.17 Part34: all existing nested collections and prior memo preserved while newly numbered root memo reports exact diverted path");
        report.Add(new { name = "recursive-nested-collection-and-report", pass = nestedOkay });

        string folderCollision = Work(parent, "folder-versus-file");
        string previousFolderName = Path.Combine(folderCollision, "資料");
        File.WriteAllBytes(previousFolderName, RandomNumberGenerator.GetBytes(101));
        string previousHash = Hash(previousFolderName);
        string blockedRelative = Path.Combine("資料", "説明書.mp4");
        var folderPlan = CollisionCollectionPlan.Inspect(folderCollision, [blockedRelative]);
        check(folderPlan.Conflicts == 1 &&
              folderPlan.Entries.Single().Reason.Contains("親フォルダ名",
                  StringComparison.Ordinal) &&
              folderPlan.Entries.Single().DestinationRelativePath ==
                Path.Combine("同名ファイル", "資料", "説明書 (1).mp4"),
              "R2.17 Part34: incoming directory path colliding with existing FILE is routed to central collection without overwriting folder-name occupant");
        CollisionCollectionPlan.ExerciseSynthetic(parent, folderPlan,
            [(srcC, blockedRelative)]);
        check(Hash(previousFolderName) == previousHash &&
              Hash(Path.Combine(folderCollision,
                  folderPlan.Entries.Single().DestinationRelativePath)) ==
                  originalC,
            "R2.17 Part34: real generated parent-file/child-directory collision preserves original bytes and relative folder hierarchy");
        report.Add(new { name = "directory-file-conflict", pass = true });

        string noCollision = Work(parent, "no-conflict");
        string simpleRelative = Path.Combine("album", "unique.mp4");
        var cleanPlan = CollisionCollectionPlan.Inspect(noCollision, [simpleRelative]);
        check(cleanPlan.Conflicts == 0 && cleanPlan.ReportRelativePath == "" &&
              cleanPlan.CollectionRelativePath == "",
            "R2.17 Part34: no collision means no extra collection folder and no memo");
        CollisionCollectionPlan.ExerciseSynthetic(parent, cleanPlan,
            [(srcD, simpleRelative)]);
        check(Hash(Path.Combine(noCollision, simpleRelative)) == originalD &&
              !Directory.Exists(Path.Combine(noCollision, "同名ファイル")) &&
              !File.Exists(Path.Combine(noCollision, "同名ファイル一覧.txt")),
            "R2.17 Part34: clean synthetic extraction creates only the requested file");
        report.Add(new { name = "no-collision-does-not-create-report", pass = true });

        string stale = Work(parent, "new-occupant-after-preview");
        string newName = Path.Combine("camera", "new.mp4");
        var stalePlan = CollisionCollectionPlan.Inspect(stale, [newName]);
        Directory.CreateDirectory(Path.Combine(stale, "camera"));
        File.WriteAllBytes(Path.Combine(stale, newName),
            RandomNumberGenerator.GetBytes(211));
        string attackerHash = Hash(Path.Combine(stale, newName));
        FileIdentity attackerId = NativeFile.GetIdentity(
            Path.Combine(stale, newName));
        expectThrows(() => CollisionCollectionPlan.ExerciseSynthetic(parent,
            stalePlan, [(srcA, newName)]),
            "R2.17 Part34: a destination appearing after preview forces replan without replacing foreign file");
        bool lateOkay = NativeFile.GetIdentity(
                Path.Combine(stale, newName)).SameFile(attackerId) &&
            Hash(Path.Combine(stale, newName)) == attackerHash &&
            !Directory.Exists(Path.Combine(stale, "同名ファイル")) &&
            !File.Exists(Path.Combine(stale, "同名ファイル一覧.txt"));
        check(lateOkay,
            "R2.17 Part34: stale collision plan never overwrites a newly arrived foreign NTFS file or invents a report");
        report.Add(new { name = "foreign-arrival-reject", pass = lateOkay });

        string securityRoot = Work(parent, "invalid-paths");
        foreach (var malicious in new[]
        {
            "../outside.mp4", @"..\outside.mp4",
            @"C:\Windows\test.mp4", "foo/../bar.mp4",
            "a:ads.mp4"
        })
            expectThrows(() => CollisionCollectionPlan.Inspect(
                securityRoot, [malicious]),
                "R2.17 Part34: invalid/archive-traversal path not permitted: " +
                malicious);
        check(!Directory.EnumerateFileSystemEntries(securityRoot).Any(),
            "R2.17 Part34: malicious relative paths are rejected without any output");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-part34-collision-collection.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-part34-collision-collection-v1",
                verdict = "PASS",
                generatedOnly = true,
                scenarios = report,
                collisionFolder = CollisionCollectionPlan.FolderName,
                nestedCollisionFolder = nestedCollection,
                topLevelReport = CollisionCollectionPlan.ReportName,
                incrementalFileNames = true,
                originalRelativeHierarchyPreserved = true,
                inputAndExistingHashesPreserved = true,
                existingCollisionFoldersAndMemosNeverOverwritten = nestedOkay,
                directoryVsFileConflictSupported = true,
                noConflictWritesNoMemo = true,
                foreignArrivalAfterPreviewRefused = lateOkay,
                traversalAndAdsPathCandidatesRefused = true,
                productionExtractionOrUndoEnabled =
                    CollisionCollectionPlan.ProductionExtractionOrUndoEnabled,
                productionDedupeEnabled = false,
                realUserMediaTouched = false,
                realUserCapacityRecoveredBytes = 0,
                productionAtomicRecoveryProven = false,
                physicalPowerCutProven = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R2.17 PART34 collision-group folder, nested repeats, numbered files and root TXT note PASS");
    }
}
