using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// A+B integration of the existing duplicate preview, collision-folder policy,
/// pinned NTFS rename and durable evidence. Mutations are NEVER callable from
/// the shipped product; #if CHAPPY_TESTING additionally requires an owned
/// disposable Windows TEMP root. The "recycle" in tests is an independent
/// generated directory, NOT the real Windows Recycle Bin.
/// </summary>
public static class DuplicateCleanupWorkflow
{
    public const bool ProductionMutationEnabled = false;
    private const string JournalFolder = ".chappy-cleanup-evidence";
    private const string Sentinel = ".chappy-part23-generated-only";
    private const string SentinelValue = "Part23 synthetic data only";

    public sealed record FileEvidence(string Path, long Length,
        DateTime CreationUtc, DateTime ModifiedUtc, FileAttributes Attributes,
        uint VolumeSerial, ulong FileIndex, string SecurityHash, string Sha256);

    public sealed record Candidate(string SourcePath, string DestinationPath,
        string SourceRelative, string DestinationRelative, FileEvidence Original);

    public sealed record Intent(int Schema, string Id, string Root,
        FileEvidence Keeper, string GroupHash, string ReportRelative,
        string CollectionRelative, List<Candidate> Candidates);

    public sealed record ReviewEvent(int Schema, string Id, int Index, int Sequence,
        bool Confirmed, DateTime RecordedUtc);

    public sealed record Status(int Index, string State, string OriginalPath,
        string StagedPath, bool Confirmed, string Explanation);

    private static string RelativeTo(string root, string full)
    {
        string path = Path.GetFullPath(full);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cleanup path escaped selected NTFS root");
        return Path.GetRelativePath(root, path);
    }

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathFullyQualified(relative))
            throw new InvalidDataException("Invalid cleanup relative path");
        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(x => x is "." or ".." or "" || x.EndsWith('.') ||
            x.EndsWith(' ') || x.Contains(':') ||
            x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Unsafe cleanup relative path");
        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        _ = RelativeTo(root, full);
        return full;
    }

    private static FileEvidence Evidence(FileSnapshot snap, string hash) =>
        new(snap.Path, snap.Length, snap.CreationUtc, snap.ModifiedUtc,
            snap.Attributes, snap.Identity.VolumeSerial, snap.Identity.FileIndex,
            Convert.ToHexString(SHA256.HashData(snap.SecurityDescriptor)), hash);

    private static bool Matches(FileEvidence expected, string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var snap = Safety.Capture(path);
            return snap.Identity.VolumeSerial == expected.VolumeSerial &&
                snap.Identity.FileIndex == expected.FileIndex &&
                snap.Identity.LinkCount == 1 && snap.Length == expected.Length &&
                snap.CreationUtc == expected.CreationUtc &&
                snap.ModifiedUtc == expected.ModifiedUtc &&
                snap.Attributes == expected.Attributes &&
                Convert.ToHexString(SHA256.HashData(snap.SecurityDescriptor)) ==
                    expected.SecurityHash &&
                string.Equals(Safety.ComputeSha256(path, CancellationToken.None,
                    allowDeleteSharing: true),
                    expected.Sha256, StringComparison.OrdinalIgnoreCase) &&
                Safety.SameSnapshot(snap);
        }
        catch { return false; }
    }

    private static void RequireKeeper(Intent intent)
    {
        if (!Matches(intent.Keeper, intent.Keeper.Path))
            throw new InvalidDataException("Keeper changed. Cleanup is stopped.");
    }

#if CHAPPY_TESTING
    private static string OwnedRoot(string testRoot, string selectedRoot)
    {
        string parent = Safety.ValidateRoot(testRoot);
        string root = Safety.ValidateRoot(selectedRoot);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!parent.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !parent.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(parent, Sentinel)) ||
            File.ReadAllText(Path.Combine(parent, Sentinel)) != SentinelValue ||
            !root.StartsWith(parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Duplicate cleanup mutations are synthetic NTFS-only");
        return root;
    }

    private static string EvidenceDir(string root) => Path.Combine(root, JournalFolder);
    private static string IntentPath(string root, string id) =>
        Path.Combine(EvidenceDir(root), id + ".intent.json");
    private static string StagePath(string root, string id, int index) =>
        Path.Combine(EvidenceDir(root), id + ".item-" + index.ToString("D4") + ".staged.json");
    private static string TrashIntentPath(string root, string id, int index) =>
        Path.Combine(EvidenceDir(root), id + ".item-" + index.ToString("D4") + ".recycle-intent.json");
    private static string TrashReceiptPath(string root, string id, int index) =>
        Path.Combine(EvidenceDir(root), id + ".item-" + index.ToString("D4") + ".recycled.json");
    private static string ReportReceiptPath(string root, string id) =>
        Path.Combine(EvidenceDir(root), id + ".report.json");
    private static string RestoreIntentPath(string root, string id, int index) =>
        Path.Combine(EvidenceDir(root), id + ".item-" + index.ToString("D4") + ".restore-intent.json");
    private static string RestoreReceiptPath(string root, string id, int index) =>
        Path.Combine(EvidenceDir(root), id + ".item-" + index.ToString("D4") + ".restored.json");
    private static string ReviewPath(string root, string id, int index, int seq) =>
        Path.Combine(EvidenceDir(root), id + ".item-" + index.ToString("D4") +
            ".review-" + seq.ToString("D4") + ".json");

    private static void DurableCreate(string name, byte[] bytes)
    {
        // Never replace an existing evidence file. Unknown .tmp stays visible.
        string tmp = name + ".tmp";
        using (var file = new FileStream(tmp, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(tmp, name, overwrite: false);
    }

    private static void DurableJson<T>(string name, T value) =>
        DurableCreate(name, JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions { WriteIndented = true }));

    private static void ExistingEvidenceIsEmpty(string root)
    {
        string folder = EvidenceDir(root);
        if (File.Exists(folder) ||
            Directory.Exists(folder) &&
            ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 ||
             Directory.EnumerateFileSystemEntries(folder).Any()))
            throw new InvalidDataException("Existing cleanup evidence requires manual review");
    }

    private static Intent Read(string testRoot, string root, string id)
    {
        _ = OwnedRoot(testRoot, root);
        if (!Guid.TryParseExact(id, "N", out _) ||
            !Directory.Exists(EvidenceDir(root)) ||
            (File.GetAttributes(EvidenceDir(root)) & FileAttributes.ReparsePoint) != 0 ||
            Directory.EnumerateFiles(EvidenceDir(root), "*.tmp").Any())
            throw new InvalidDataException("Untrusted or interrupted cleanup evidence");
        var intent = JsonSerializer.Deserialize<Intent>(
            File.ReadAllBytes(IntentPath(root, id))) ??
            throw new InvalidDataException("Missing cleanup intent");
        if (intent.Schema != 1 || intent.Id != id ||
            !intent.Root.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            intent.Candidates is null || intent.Candidates.Count is < 1 or > 32 ||
            intent.GroupHash.Length != 64 || !intent.GroupHash.All(Uri.IsHexDigit) ||
            intent.Keeper.Path != Resolve(root, RelativeTo(root, intent.Keeper.Path)) ||
            intent.ReportRelative.Length == 0 || intent.CollectionRelative.Length == 0 ||
            Resolve(root, intent.ReportRelative) == Resolve(root, intent.CollectionRelative) ||
            intent.Candidates.Any(x => x is null ||
                x.SourcePath != Resolve(root, x.SourceRelative) ||
                x.DestinationPath != Resolve(root, x.DestinationRelative) ||
                x.Original.Path != x.SourcePath ||
                x.Original.Sha256 != intent.GroupHash ||
                x.Original.Length <= 0 || x.Original.FileIndex == 0 ||
                x.Original.SecurityHash.Length != 64 ||
                !x.Original.SecurityHash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid cleanup intent metadata");
        var all = intent.Candidates.SelectMany(x =>
            new[] { x.SourcePath, x.DestinationPath }).Append(intent.Keeper.Path);
        if (all.Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count() ||
            !intent.Candidates.All(x => x.DestinationRelative.StartsWith(
                intent.CollectionRelative + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Overlapping cleanup paths");
        return intent;
    }

    private static void ParentsSafe(string root, string file, bool create)
    {
        string relative = RelativeTo(root, Path.GetDirectoryName(file)!);
        string folder = root;
        if (relative == ".") return;
        foreach (string component in relative.Split(Path.DirectorySeparatorChar))
        {
            folder = Path.Combine(folder, component);
            if (File.Exists(folder)) throw new InvalidDataException("File occupies cleanup directory");
            if (!Directory.Exists(folder))
            {
                if (!create) return; // During read-only preview future subfolders do not exist.
                Directory.CreateDirectory(folder);
            }
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Cleanup cannot traverse a junction");
        }
    }

    internal static string BeginSynthetic(string testRoot,
        DuplicateCleanupPreview.Result plan, DuplicateGroup group)
    {
        using var lease = MutationLease.Acquire();
        string root = OwnedRoot(testRoot, plan.Root);
        ExistingEvidenceIsEmpty(root);
        var fresh = DuplicateCleanupPreview.Inspect(root, group, plan.KeeperPath);
        if (fresh.CollectionRelativePath != plan.CollectionRelativePath ||
            fresh.ReportRelativePath != plan.ReportRelativePath ||
            !fresh.DeleteCandidates.SequenceEqual(plan.DeleteCandidates))
            throw new InvalidDataException("Stale cleanup plan. Re-scan before use.");

        var keeperSnap = Safety.Capture(plan.KeeperPath);
        if (keeperSnap.Identity.LinkCount != 1 ||
            !string.Equals(Safety.ComputeSha256(keeperSnap.Path, CancellationToken.None),
                group.Hash, StringComparison.OrdinalIgnoreCase) ||
            !Safety.SameSnapshot(keeperSnap))
            throw new InvalidDataException("Keeper was modified before cleanup");
        var candidates = new List<Candidate>();
        foreach (var entry in fresh.DeleteCandidates)
        {
            var snap = Safety.Capture(entry.OriginalPath);
            if (snap.Identity.LinkCount != 1 ||
                snap.Identity.SameFile(keeperSnap.Identity) ||
                !string.Equals(Safety.ComputeSha256(entry.OriginalPath, CancellationToken.None),
                    group.Hash, StringComparison.OrdinalIgnoreCase) ||
                !Safety.ByteEqual(entry.OriginalPath, keeperSnap.Path,
                    CancellationToken.None) ||
                !Safety.SameSnapshot(snap))
                throw new InvalidDataException("Candidate changed before cleanup");
            string dest = Resolve(root, entry.ProposedCollectionRelativePath);
            ParentsSafe(root, dest, create: false);
            if (File.Exists(dest) || Directory.Exists(dest))
                throw new InvalidDataException("Cleanup destination occupied");
            candidates.Add(new Candidate(entry.OriginalPath, dest,
                entry.OriginalRelativePath, entry.ProposedCollectionRelativePath,
                Evidence(snap, group.Hash)));
        }

        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(EvidenceDir(root));
        var intent = new Intent(1, id, root, Evidence(keeperSnap, group.Hash),
            group.Hash, plan.ReportRelativePath, plan.CollectionRelativePath, candidates);
        DurableJson(IntentPath(root, id), intent);
        return id;
    }

    private static bool StagedAndValid(Intent x, Candidate c)
    {
        return !File.Exists(c.SourcePath) && Matches(c.Original, c.DestinationPath);
    }

    private static bool ValidStageReceipt(string root, string id, int index, Candidate c)
    {
        string receipt = StagePath(root, id, index);
        if (!File.Exists(receipt)) return false;
        try
        {
            var recorded = JsonSerializer.Deserialize<FileEvidence>(File.ReadAllBytes(receipt));
            if (recorded != c.Original)
                throw new InvalidDataException("Stage receipt does not match immutable intent");
            return true;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Stage receipt is invalid JSON", ex);
        }
    }

    private static bool ValidReport(Intent x)
    {
        string report = Resolve(x.Root, x.ReportRelative);
        string receipt = ReportReceiptPath(x.Root, x.Id);
        if (!File.Exists(report) || !File.Exists(receipt))
            return false;
        byte[] expected = ReportBytes(x);
        string? recorded;
        try
        {
            recorded = JsonSerializer.Deserialize<string>(File.ReadAllBytes(receipt));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Cleanup report receipt is malformed", ex);
        }
        if (!string.Equals(recorded, Convert.ToHexString(SHA256.HashData(expected)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cleanup report receipt hash is inconsistent");
        if (!File.ReadAllBytes(report).AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("Cleanup report was modified after verification");
        return true;
    }

    internal static void StageSynthetic(string testRoot, string scanRoot,
        string id, int index, Action<string>? checkpoint = null)
    {
        using var lease = MutationLease.Acquire();
        string root = OwnedRoot(testRoot, scanRoot);
        var x = Read(testRoot, root, id);
        if (index < 0 || index >= x.Candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        RequireKeeper(x);
        var c = x.Candidates[index];
        if (File.Exists(RestoreIntentPath(root, id, index)) ||
            File.Exists(RestoreReceiptPath(root, id, index)) ||
            File.Exists(TrashIntentPath(root, id, index)) ||
            File.Exists(TrashReceiptPath(root, id, index)))
            throw new InvalidDataException("Already restored/recycled or ambiguous: staging refused");
        string receipt = StagePath(root, id, index);
        if (ValidStageReceipt(root, id, index, c))
        {
            if (!StagedAndValid(x, c))
                throw new InvalidDataException("Staged receipt no longer matches the file");
            return;
        }
        if (!File.Exists(c.SourcePath) && Matches(c.Original, c.DestinationPath))
        {
            // The process may have died exactly after rename and before receipt.
            // Recover by verified ID+metadata+SHA, never by filename alone.
            DurableJson(receipt, c.Original);
            return;
        }
        if (!Matches(c.Original, c.SourcePath) ||
            File.Exists(c.DestinationPath) || Directory.Exists(c.DestinationPath))
            throw new InvalidDataException("Source or staging destination changed");

        ParentsSafe(root, c.DestinationPath, create: true);
        using var directories = NativeFile.PinnedDirectories.Open(root,
            new[] { x.Keeper.Path, c.SourcePath, c.DestinationPath });
        directories.AssertUnchanged();
        if (!Matches(x.Keeper, x.Keeper.Path) ||
            !Matches(c.Original, c.SourcePath) ||
            !Safety.ByteEqual(x.Keeper.Path, c.SourcePath, CancellationToken.None))
            throw new InvalidDataException("Stale source or keeper before NTFS rename");
        using var pinned = NativeFile.PinnedFile.Open(c.SourcePath,
            new FileIdentity(c.Original.VolumeSerial, c.Original.FileIndex, 1),
            c.Original.Sha256);
        pinned.RenameNoReplace(c.DestinationPath);
        checkpoint?.Invoke("after-stage-rename-before-receipt");
        if (!StagedAndValid(x, c) || !Matches(x.Keeper, x.Keeper.Path))
        {
            var got = Safety.Capture(c.DestinationPath);
            string why = "Post-stage identity/content check failed: " +
                $"pathAbsent={!File.Exists(c.SourcePath)}, " +
                $"sameId={got.Identity.VolumeSerial == c.Original.VolumeSerial && got.Identity.FileIndex == c.Original.FileIndex}, " +
                $"singleLink={got.Identity.LinkCount == 1}, " +
                $"length={got.Length == c.Original.Length}, " +
                $"created={got.CreationUtc == c.Original.CreationUtc}, " +
                $"modified={got.ModifiedUtc == c.Original.ModifiedUtc}, " +
                $"attributes={got.Attributes == c.Original.Attributes}, " +
                $"security={Convert.ToHexString(SHA256.HashData(got.SecurityDescriptor)) == c.Original.SecurityHash}, " +
                $"sha={Safety.ComputeSha256(got.Path, CancellationToken.None, allowDeleteSharing: true) == c.Original.Sha256}, " +
                $"keeper={Matches(x.Keeper, x.Keeper.Path)}";
            throw new InvalidDataException(why);
        }
        DurableJson(receipt, c.Original);
    }

    private static byte[] ReportBytes(Intent x)
    {
        var t = new StringBuilder()
            .AppendLine("Chappy Storage Compressor — 重複整理の保留一覧")
            .AppendLine("この一覧は元パスの追跡用。保留中のファイルはまだ削除されていません。")
            .AppendLine("操作ID: " + x.Id)
            .AppendLine("残したファイル: " + x.Keeper.Path)
            .AppendLine("元の最上位フォルダ: " + x.Root)
            .AppendLine();
        foreach (var (c, i) in x.Candidates.Select((value, index) => (value, index + 1)))
            t.AppendLine($"{i}. 元のパス: {c.SourcePath}")
                .AppendLine($"   保留先: {c.DestinationPath}")
                .AppendLine("   SHA-256: " + c.Original.Sha256);
        t.AppendLine("確認・ごみ箱移動は未実施です。実際の処理状態は耐中断記録で判定してください。");
        var utf = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return utf.GetPreamble().Concat(utf.GetBytes(t.ToString())).ToArray();
    }

    internal static void WriteReportSynthetic(string testRoot, string scanRoot, string id)
    {
        using var lease = MutationLease.Acquire();
        string root = OwnedRoot(testRoot, scanRoot);
        var x = Read(testRoot, root, id);
        RequireKeeper(x);
        if (x.Candidates.Where((c, i) => !ValidStageReceipt(root, id, i, c) ||
            !StagedAndValid(x, c) ||
            File.Exists(RestoreIntentPath(root, id, i)) ||
            File.Exists(TrashIntentPath(root, id, i))).Any())
            throw new InvalidDataException("Not all candidates are staged and verified");
        string report = Resolve(root, x.ReportRelative);
        byte[] expected = ReportBytes(x);
        if (File.Exists(report))
        {
            if (!File.ReadAllBytes(report).AsSpan().SequenceEqual(expected))
                throw new InvalidDataException("Existing report is foreign; cannot overwrite");
        }
        else
            DurableCreate(report, expected);
        if (!File.Exists(ReportReceiptPath(root, id)))
            DurableJson(ReportReceiptPath(root, id), Convert.ToHexString(
                SHA256.HashData(expected)));
    }

    private static (bool Confirmed, int LastSeq) Review(Intent x, int index)
    {
        string prefix = x.Id + ".item-" + index.ToString("D4") + ".review-";
        var events = Directory.EnumerateFiles(EvidenceDir(x.Root), prefix + "*.json")
            .Select(path => Path.GetFileName(path))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        bool confirmed = false;
        int last = 0;
        foreach (var filename in events)
        {
            string suffix = filename[prefix.Length..^5];
            if (!int.TryParse(suffix, out int seq) || seq != last + 1 ||
                filename != prefix + seq.ToString("D4") + ".json")
                throw new InvalidDataException("Broken review event sequence");
            var ev = JsonSerializer.Deserialize<ReviewEvent>(
                File.ReadAllBytes(Path.Combine(EvidenceDir(x.Root), filename))) ??
                throw new InvalidDataException("Empty review event");
            if (ev.Schema != 1 || ev.Id != x.Id || ev.Index != index ||
                ev.Sequence != seq)
                throw new InvalidDataException("Invalid review event");
            last = seq;
            confirmed = ev.Confirmed;
        }
        return (confirmed, last);
    }

    internal static void ConfirmSynthetic(string testRoot, string scanRoot,
        string id, int index, bool confirmed)
    {
        using var lease = MutationLease.Acquire();
        string root = OwnedRoot(testRoot, scanRoot);
        var x = Read(testRoot, root, id);
        if (index < 0 || index >= x.Candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        RequireKeeper(x);
        if (!ValidReport(x) ||
            File.Exists(TrashIntentPath(root, id, index)) ||
            File.Exists(RestoreIntentPath(root, id, index)) ||
            !ValidStageReceipt(root, id, index, x.Candidates[index]) ||
            !StagedAndValid(x, x.Candidates[index]))
            throw new InvalidDataException("Cannot confirm unverified/started cleanup");
        var (_, last) = Review(x, index);
        DurableJson(ReviewPath(root, id, index, last + 1),
            new ReviewEvent(1, id, index, last + 1, confirmed, DateTime.UtcNow));
    }

    /// <summary>
    /// SIMULATED recycle only. The test moves the generated name to another
    /// generated directory and deliberately does NOT exercise Windows' bin.
    /// No real Recycle Bin, permanent deletion, or user-file API is exposed.
    /// </summary>
    internal static void RecycleSimulatedSynthetic(string testRoot,
        string scanRoot, string id, int index,
        Action<string>? checkpoint = null)
    {
        using var lease = MutationLease.Acquire();
        string root = OwnedRoot(testRoot, scanRoot);
        var x = Read(testRoot, root, id);
        if (index < 0 || index >= x.Candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (File.Exists(TrashIntentPath(root, id, index)) ||
            File.Exists(TrashReceiptPath(root, id, index)) ||
            File.Exists(RestoreIntentPath(root, id, index)) ||
            File.Exists(RestoreReceiptPath(root, id, index)))
            throw new InvalidDataException("Recycle/restore already attempted: manual inspection required");
        RequireKeeper(x);
        var c = x.Candidates[index];
        if (!ValidReport(x) ||
            !ValidStageReceipt(root, id, index, c) ||
            !Review(x, index).Confirmed || !StagedAndValid(x, c) ||
            !Safety.ByteEqual(x.Keeper.Path, c.DestinationPath, CancellationToken.None))
            throw new InvalidDataException("Unconfirmed or modified candidate cannot be recycled");
        string destination = Path.Combine(testRoot, ".chappy-test-recycle",
            x.Id + "-" + index.ToString("D4") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new InvalidDataException("Generated recycle destination occupied");
        DurableJson(TrashIntentPath(root, id, index), new
        {
            Schema = 1, Id = id, Index = index, StagedPath = c.DestinationPath,
            SimulatedDestination = destination
        });
        checkpoint?.Invoke("after-recycle-intent-before-rename");
        using var pinned = NativeFile.PinnedFile.Open(c.DestinationPath,
            new FileIdentity(c.Original.VolumeSerial, c.Original.FileIndex, 1),
            c.Original.Sha256);
        pinned.RenameNoReplace(destination);
        checkpoint?.Invoke("after-recycle-rename-before-receipt");
        if (File.Exists(c.DestinationPath) || !Matches(c.Original, destination))
            throw new InvalidDataException("Generated recycle identity mismatch");
        DurableJson(TrashReceiptPath(root, id, index), new
        {
            Schema = 1, Id = id, Index = index,
            FakeRecyclePath = destination, c.Original.Sha256
        });
    }

    /// <summary>
    /// Restore only an independently authenticated staged synthetic candidate to
    /// its EXACT original pathname. The current path must be absent; never use
    /// File.Move(overwrite: true), delete a foreign occupant, or auto-create a
    /// missing original parent. Durable intent is written before the NTFS rename.
    /// A crash after rename is recovered only with matching file ID + SHA.
    /// This is NOT a Recycle Bin restore and has no product mutation entrypoint.
    /// </summary>
    internal static void RestoreSynthetic(string testRoot, string scanRoot,
        string id, int index, Action<string>? checkpoint = null)
    {
        using var lease = MutationLease.Acquire();
        string root = OwnedRoot(testRoot, scanRoot);
        var x = Read(testRoot, root, id);
        if (index < 0 || index >= x.Candidates.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        RequireKeeper(x);
        var c = x.Candidates[index];
        if (File.Exists(TrashIntentPath(root, id, index)) ||
            File.Exists(TrashReceiptPath(root, id, index)))
            throw new InvalidDataException(
                "Recycle was attempted: original restore requires manual review");
        if (!ValidStageReceipt(root, id, index, c) || !ValidReport(x))
            throw new InvalidDataException("Stage/report evidence is missing or altered");
        string intent = RestoreIntentPath(root, id, index);
        string receipt = RestoreReceiptPath(root, id, index);
        bool originalMatches = Matches(c.Original, c.SourcePath);
        bool stageMatches = StagedAndValid(x, c);
        if (File.Exists(receipt))
        {
            if (!File.Exists(intent) || !originalMatches ||
                File.Exists(c.DestinationPath))
                throw new InvalidDataException("Restored receipt conflicts with current NTFS file");
            return;
        }
        if (File.Exists(intent) && originalMatches &&
            !File.Exists(c.DestinationPath))
        {
            // Recovery of a verified rename interrupted before its receipt.
            DurableJson(receipt, c.Original);
            return;
        }
        if (File.Exists(c.SourcePath) || Directory.Exists(c.SourcePath) ||
            !stageMatches)
            throw new InvalidDataException("Original path occupied or staged file changed");

        // Re-check parent directories under pinned handles; the restored parent
        // must already exist, not be a reparse point or a renamed alias.
        ParentsSafe(root, c.SourcePath, create: false);
        using var parents = NativeFile.PinnedDirectories.Open(root,
            new[] { x.Keeper.Path, c.SourcePath, c.DestinationPath });
        parents.AssertUnchanged();
        if (!Matches(x.Keeper, x.Keeper.Path) ||
            !StagedAndValid(x, c) ||
            !Safety.ByteEqual(x.Keeper.Path, c.DestinationPath,
                CancellationToken.None))
            throw new InvalidDataException("Staged file/keeper changed before restoration");
        if (!File.Exists(intent))
            DurableJson(intent, new
            {
                Schema = 1, Id = id, Index = index,
                c.SourcePath, c.DestinationPath, c.Original.Sha256
            });
        checkpoint?.Invoke("after-restore-intent-before-rename");

        using var pinned = NativeFile.PinnedFile.Open(c.DestinationPath,
            new FileIdentity(c.Original.VolumeSerial, c.Original.FileIndex, 1),
            c.Original.Sha256);
        pinned.RenameNoReplace(c.SourcePath);
        checkpoint?.Invoke("after-restore-rename-before-receipt");
        if (!Matches(c.Original, c.SourcePath) ||
            File.Exists(c.DestinationPath) ||
            !Matches(x.Keeper, x.Keeper.Path))
            throw new InvalidDataException("Restored NTFS identity/content differs: manual review");
        DurableJson(receipt, c.Original);
    }

    internal static IReadOnlyList<Status> InspectSynthetic(
        string testRoot, string scanRoot, string id)
    {
        string root = OwnedRoot(testRoot, scanRoot);
        var x = Read(testRoot, root, id);
        return x.Candidates.Select((c, i) =>
        {
            bool stagedReceipt = ValidStageReceipt(root, id, i, c);
            bool startedRecycle = File.Exists(TrashIntentPath(root, id, i));
            bool doneRecycle = File.Exists(TrashReceiptPath(root, id, i));
            bool restoreIntent = File.Exists(RestoreIntentPath(root, id, i));
            bool restored = File.Exists(RestoreReceiptPath(root, id, i));
            bool originalValid = Matches(c.Original, c.SourcePath);
            bool stagedValid = StagedAndValid(x, c);
            bool reviewed = Review(x, i).Confirmed;
            string state =
                (restoreIntent && startedRecycle) || (restored && startedRecycle)
                    ? "ManualReview"
                    : restored
                        ? restoreIntent && originalValid && !File.Exists(c.DestinationPath)
                            ? "RestoredOriginal" : "ManualReview"
                        : restoreIntent
                            ? originalValid && !File.Exists(c.DestinationPath)
                                ? "InterruptedAfterRestoreRecoverable"
                                : stagedValid && !File.Exists(c.SourcePath)
                                    ? "RestoreIntentRecorded" : "ManualReview"
                        : doneRecycle
                            ? "SimulatedRecycleReceiptRequiresReview"
                            : startedRecycle
                                ? "RecycleIntentRequiresManualReview"
                            : stagedReceipt && stagedValid
                                ? reviewed ? "ConfirmedStaged" : "StagedUnconfirmed"
                            : !stagedReceipt && originalValid && !File.Exists(c.DestinationPath)
                                ? "NotStaged"
                            : !stagedReceipt && stagedValid
                                ? "InterruptedAfterStageRecoverable"
                                : "ManualReview";
            // A stale keeper always prevents all further mutation.
            if (!Matches(x.Keeper, x.Keeper.Path)) state = "ManualReview";
            return new Status(i, state, c.SourcePath, c.DestinationPath,
                reviewed, "This synthetic status is not permission for product deletion.");
        }).ToArray();
    }
#endif
}
