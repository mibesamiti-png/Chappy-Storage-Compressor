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
/// A user-visible, durable CHECKLIST ONLY for an exact scanned duplicate group.
/// This stores metadata exclusively in the current user's application-data
/// folder and NEVER changes scanned files or grants file-mutation permission.
/// On load, every scanned file is revalidated before any check is restored.
/// </summary>
public static class DuplicateCleanupReviewStore
{
    public const bool CanMoveOrDeleteFiles = false;
    private const int SchemaVersion = 1;
    private const int MaxCandidates = 1000;
    private const int MaxBytes = 256 * 1024;
    private const string GeneratedSentinel = ".chappy-part23-generated-only";
    private const string GeneratedSentinelContents = "Part23 synthetic data only";

    public sealed record Snapshot(
        int Schema, string Fingerprint, string KeeperPath,
        string[] CheckedOriginalPaths, DateTime SavedUtc, bool AuthorizesMutation);

    public sealed record ReviewResult(
        bool Exists, bool Valid, IReadOnlyList<string> CheckedPaths,
        string Summary, bool CanMoveOrDelete = false);

    public static ReviewResult Save(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group, IReadOnlyCollection<string> checkedPaths,
        CancellationToken ct) =>
        SaveCore(plan, group, checkedPaths, ProductStoreDirectory(), ct);

    public static ReviewResult Load(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group, CancellationToken ct) =>
        LoadCore(plan, group, ProductStoreDirectory(), ct);

#if CHAPPY_TESTING
    // Only the existing disposable NTFS smoke root can override the product
    // application-data destination. This never accepts an arbitrary user path.
    internal static ReviewResult SaveSynthetic(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group, IReadOnlyCollection<string> paths, string ownedTestRoot,
        CancellationToken ct) =>
        SaveCore(plan, group, paths, GeneratedStore(ownedTestRoot), ct);

    internal static ReviewResult LoadSynthetic(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group, string ownedTestRoot, CancellationToken ct) =>
        LoadCore(plan, group, GeneratedStore(ownedTestRoot), ct);

    private static string GeneratedStore(string ownedTestRoot)
    {
        string root = Safety.ValidateRoot(ownedTestRoot);
        string temp = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar +
                "ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, GeneratedSentinel)) ||
            File.ReadAllText(Path.Combine(root, GeneratedSentinel)) !=
                GeneratedSentinelContents)
            throw new InvalidOperationException(
                "Review storage override is restricted to generated smoke-test NTFS");
        return Path.Combine(root, "review-metadata-only");
    }
#endif

    private static string ProductStoreDirectory()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData) ||
            !Path.IsPathFullyQualified(appData))
            throw new InvalidOperationException(
                "User application data path is unavailable");
        return Path.Combine(appData, "ChappyStorageCompressor", "DuplicateReview");
    }

    private static void CheckDirectoryPath(string directory, bool create)
    {
        // Windows user-writable metadata only. Never follow a junction in the
        // dedicated review folder. The scanned root is NEVER created/written.
        string parent = Path.GetDirectoryName(directory) ??
            throw new InvalidDataException("Invalid metadata directory");
        if (Directory.Exists(parent) &&
            (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Review parent is a reparse directory");
        if (!Directory.Exists(directory))
        {
            if (!create) return;
            Directory.CreateDirectory(directory);
        }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Review directory is a reparse directory");
    }

    private static string Fingerprint(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(group);
        if (plan.CanModifyUserFiles || CanMoveOrDeleteFiles ||
            plan.DeleteCandidates.Count is < 1 or > MaxCandidates ||
            group.Files.Count != plan.DeleteCandidates.Count + 1 ||
            !string.Equals(group.Hash, plan.GroupSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid duplicate-review group");

        string root = Safety.ValidateRoot(plan.Root);
        var snapshots = group.Files.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => new
            {
                f.Path, f.Length, f.CreationUtc, f.ModifiedUtc,
                Attributes = (int)f.Attributes,
                f.Identity.VolumeSerial, f.Identity.FileIndex,
                f.Identity.LinkCount,
                Security = Convert.ToHexString(
                    SHA256.HashData(f.SecurityDescriptor))
            }).ToArray();
        string[] candidatePaths = plan.DeleteCandidates
            .Select(c => c.OriginalPath).ToArray();
        if (!string.Equals(Path.GetFullPath(plan.KeeperPath),
                plan.KeeperPath, StringComparison.OrdinalIgnoreCase) ||
            candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                candidatePaths.Length ||
            group.Files.Count(f => string.Equals(f.Path, plan.KeeperPath,
                StringComparison.OrdinalIgnoreCase)) != 1 ||
            candidatePaths.Any(p => group.Files.Count(f => string.Equals(
                f.Path, p, StringComparison.OrdinalIgnoreCase)) != 1))
            throw new InvalidDataException("Review paths mismatch scanned snapshots");

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema = SchemaVersion, Root = root, plan.KeeperPath,
            plan.GroupSha256, plan.CollectionRelativePath,
            plan.ReportRelativePath,
            Candidates = plan.DeleteCandidates.OrderBy(x =>
                x.OriginalPath, StringComparer.OrdinalIgnoreCase).Select(c =>
                new { c.OriginalPath, c.ProposedCollectionRelativePath,
                    c.OriginalRelativePath, c.OriginalBytes }).ToArray(),
            OriginalSnapshots = snapshots
        });
        return Convert.ToHexString(SHA256.HashData(canonical));
    }

    private static string RecordPath(string directory, string fingerprint) =>
        Path.Combine(directory, fingerprint + ".review.json");

    private static ReviewResult SaveCore(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group, IReadOnlyCollection<string> checkedPaths,
        string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkedPaths);
        ct.ThrowIfCancellationRequested();
        // This is an independent second validation, not a replay of the UI's
        // previous result. The validated checks are still NEVER delete consent.
        var preflight = DuplicateCleanupPreflight.Check(plan, group,
            checkedPaths, ct);
        if (!preflight.Passed || !preflight.KeeperVerified ||
            preflight.VerifiedCount != preflight.Candidates.Count ||
            preflight.CheckedCount != checkedPaths.Count ||
            preflight.CanMoveOrDelete)
            throw new InvalidDataException(
                "Review could not be saved: files/plan changed since scan");

        string fingerprint = Fingerprint(plan, group);
        string[] selected = preflight.Candidates
            .Where(x => x.CheckedByUser && x.VerifiedNow)
            .Select(x => x.OriginalPath)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var record = new Snapshot(SchemaVersion, fingerprint, plan.KeeperPath,
            selected, DateTime.UtcNow, AuthorizesMutation: false);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(record,
            new JsonSerializerOptions { WriteIndented = true });
        if (data.Length > MaxBytes)
            throw new InvalidDataException("Review record exceeds metadata limit");
        ct.ThrowIfCancellationRequested();

        CheckDirectoryPath(directory, create: true);
        string destination = RecordPath(directory, fingerprint);
        if (File.Exists(destination) &&
            (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Review record is a file link");

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            // Overwrite is ONLY for this app's generated metadata file.
            // This is not an overwrite of any scanned file or original.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        return new ReviewResult(true, true, selected,
            $"確認内容 {selected.Length} 件をアプリ専用領域へ保存しました。削除の同意・許可ではありません。");
    }

    private static ReviewResult LoadCore(DuplicateCleanupPreview.Result plan,
        DuplicateGroup group, string directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string fingerprint = Fingerprint(plan, group);
        CheckDirectoryPath(directory, create: false);
        string path = RecordPath(directory, fingerprint);
        if (!File.Exists(path))
            return new ReviewResult(false, true, Array.Empty<string>(),
                "このグループの保存済み確認はありません。");
        try
        {
            var info = new FileInfo(path);
            if (info.Length <= 0 || info.Length > MaxBytes ||
                (info.Attributes & (FileAttributes.ReparsePoint |
                    FileAttributes.Offline)) != 0)
                throw new InvalidDataException("Invalid review-record size or attributes");
            var record = JsonSerializer.Deserialize<Snapshot>(
                File.ReadAllBytes(path)) ??
                throw new InvalidDataException("Empty saved-review record");
            if (record.Schema != SchemaVersion || record.AuthorizesMutation ||
                !string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal) ||
                !string.Equals(record.KeeperPath, plan.KeeperPath,
                    StringComparison.OrdinalIgnoreCase) ||
                record.CheckedOriginalPaths is null ||
                record.CheckedOriginalPaths.Length > MaxCandidates ||
                record.CheckedOriginalPaths.Distinct(
                    StringComparer.OrdinalIgnoreCase).Count() !=
                    record.CheckedOriginalPaths.Length ||
                record.CheckedOriginalPaths.Any(p =>
                    !plan.DeleteCandidates.Any(c =>
                        string.Equals(c.OriginalPath, p,
                            StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    "Saved review is invalid or not bound to current plan");

            // Stored checkboxes cannot be reinstated against changed files.
            // Even after verification, the UI must require a fresh final
            // preflight before enabling the METADATA SAVE button again.
            var refreshed = DuplicateCleanupPreflight.Check(plan, group,
                record.CheckedOriginalPaths, ct);
            if (!refreshed.Passed || !refreshed.KeeperVerified ||
                refreshed.VerifiedCount != refreshed.Candidates.Count ||
                refreshed.CanMoveOrDelete)
                return new ReviewResult(true, false, Array.Empty<string>(),
                    "保存後にファイルや整理予定が変わりました。再スキャンしてください。");
            return new ReviewResult(true, true, record.CheckedOriginalPaths,
                $"保存済み確認 {record.CheckedOriginalPaths.Length} 件を照合しました。" +
                " チェックを復元しても削除はできません。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            return new ReviewResult(true, false, Array.Empty<string>(),
                "保存済み確認の形式・内容を検証できません：" +
                ex.GetType().Name + "。ファイルは一切変更していません。");
        }
    }
}
