using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Read-only in production. A separate *completed* ledger is needed because
/// the existing Journal tracks only in-flight transactions and removes each
/// successful pending entry. All writes are behind CHAPPY_TESTING plus a
/// generated NTFS sandbox sentinel. No user-owned data may be mutated.
/// </summary>
public static class CompletedMergeLedger
{
    private const string FolderName = ".chappy-completed-merge";
    public const bool ProductionMutationEnabled = false;
    private const int MaxRecordBytes = 128 * 1024;

    // No SDDL/owner bytes in UI or logs. The record stores the original
    // binary security descriptor solely for synthetic Undo verification.
    private sealed class Record
    {
        public Record() { } // JsonSerializer needs an accessible parameterless constructor.
        public int Schema { get; set; }
        public string Id { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Master { get; set; } = "";
        public string Backup { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Length { get; set; }
        public long CreationUtcTicks { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public FileAttributes Attributes { get; set; }
        public uint OriginalVolume { get; set; }
        public ulong OriginalIndex { get; set; }
        public uint MasterVolume { get; set; }
        public ulong MasterIndex { get; set; }
        public uint ExpectedLinkCount { get; set; }
        public byte[] SecurityDescriptor { get; set; } = Array.Empty<byte>();
    }

    public sealed record Entry(string OperationId, string RelativeAlias,
        string State, long OriginalBytes, string Explanation);

    public sealed record Summary(IReadOnlyList<Entry> Entries)
    {
        public int VerifiedCompleted => Entries.Count(x => x.State == "VerifiedCompleted");
        public int VerifiedRestored => Entries.Count(x => x.State == "VerifiedRestored");
        public int PreparedNeedsConfirmation => Entries.Count(x => x.State == "PreparedNeedsConfirmation");
        public int NeedsReview => Entries.Count(x =>
            x.State is "ManualReview" or "Malformed" or "PreparedPendingRecovery");
        public long CompletedLogicalBytes => Entries.Where(x =>
            x.State == "VerifiedCompleted").Sum(x => x.OriginalBytes);
        public bool CanRunProductionUndo => false;
        public string Describe() =>
            "【重複統合の保存済み履歴：読み取り専用】\n" +
            $"検証済み統合: {VerifiedCompleted}件 / 独立化済み: {VerifiedRestored}件\n" +
            $"完了確認待ち: {PreparedNeedsConfirmation}件 / 要手動確認: {NeedsReview}件\n" +
            $"統合による論理削減見込の記録: {GroupRow.HumanSize(CompletedLogicalBytes)}\n" +
            "※ 未記録の統合を推測せず、不明な履歴を削除・修復しません。\n" +
            "※ 本番統合・本番独立Undoは無効。安全な履歴でも自動的にファイルを変更しません。";
    }

    private static string PathFor(string root) =>
        Path.Combine(Safety.StorePath(root), FolderName);

    private static bool Under(string parent, string child) =>
        Path.GetFullPath(child).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string FileFor(string root, string id, string phase) =>
        Path.Combine(PathFor(root), id + "." + phase + ".json");

    private static bool SafeSubpath(string root, string path)
    {
        if (!Under(root, path)) return false;
        string current = Path.GetDirectoryName(Path.GetFullPath(path))!;
        while (!current.Equals(Path.TrimEndingDirectorySeparator(root),
            StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(current) ||
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
            current = Path.GetDirectoryName(current) ??
                throw new InvalidDataException("Ledger path parent traversal failed");
        }
        return true;
    }

    private static bool ValidRecord(Record x, string root, string file, string id)
    {
        try
        {
            if (x.Schema != 1 || !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(id, "N", out _) ||
                x.Hash.Length != 64 || !x.Hash.All(Uri.IsHexDigit) ||
                x.Length <= 0 || x.CreationUtcTicks <= 0 || x.ModifiedUtcTicks <= 0 ||
                x.ExpectedLinkCount < 3 ||
                x.OriginalVolume == 0 || x.OriginalIndex == 0 ||
                x.MasterVolume != x.OriginalVolume || x.MasterIndex == 0 ||
                x.OriginalIndex == x.MasterIndex ||
                x.SecurityDescriptor.Length is < 20 or > 65536 ||
                !Path.IsPathFullyQualified(x.Alias) || !Path.IsPathFullyQualified(x.Master) ||
                !Path.IsPathFullyQualified(x.Backup) ||
                !SafeSubpath(root, x.Alias) || Under(Safety.StorePath(root), x.Alias) ||
                !SafeSubpath(root, x.Master) || !Under(Safety.StorePath(root), x.Master) ||
                !Path.GetFullPath(x.Backup).Equals(
                    Path.GetFullPath(x.Alias + ".chappy-backup-" + id),
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(file).StartsWith(PathFor(root) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch { return false; }
    }

    private static Record Read(string root, string file, string id)
    {
        FileAttributes attributes = File.GetAttributes(file);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                           FileAttributes.System | FileAttributes.Offline |
                           FileAttributes.Encrypted | FileAttributes.SparseFile)) != 0 ||
            NativeFile.GetIdentity(file).LinkCount != 1 ||
            NativeFile.HasNamedDataStreams(file))
            throw new InvalidDataException("Unknown NTFS ledger file attributes/streams/links");
        var length = new FileInfo(file).Length;
        if (length is <= 10 or > MaxRecordBytes)
            throw new InvalidDataException("Unsafe completed-merge ledger size");
        Record record = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(file)) ??
            throw new InvalidDataException("Empty completed-merge ledger entry");
        if (!ValidRecord(record, root, file, id))
            throw new InvalidDataException("Completed-merge ledger path/schema/identity validation failed");
        return record;
    }

    private static FileSnapshot Original(Record x) => new()
    {
        Path = x.Alias, Length = x.Length,
        CreationUtc = new DateTime(x.CreationUtcTicks, DateTimeKind.Utc),
        ModifiedUtc = new DateTime(x.ModifiedUtcTicks, DateTimeKind.Utc),
        Attributes = x.Attributes,
        Identity = new FileIdentity(x.OriginalVolume, x.OriginalIndex, 1),
        SecurityDescriptor = x.SecurityDescriptor.ToArray(),
        Sha256 = x.Hash
    };

    private static bool Linked(Record x)
    {
        try
        {
            if (File.Exists(x.Backup) || !File.Exists(x.Alias) || !File.Exists(x.Master))
                return false;
            FileSnapshot alias = Safety.Capture(x.Alias), master = Safety.Capture(x.Master);
            var initial = Original(x);
            if (!alias.Identity.SameFile(master.Identity) ||
                alias.Identity.VolumeSerial != x.MasterVolume ||
                alias.Identity.FileIndex != x.MasterIndex ||
                alias.Identity.LinkCount != x.ExpectedLinkCount ||
                master.Identity.LinkCount != x.ExpectedLinkCount ||
                alias.Length != initial.Length || alias.CreationUtc != initial.CreationUtc ||
                alias.ModifiedUtc != initial.ModifiedUtc || alias.Attributes != initial.Attributes ||
                !alias.SecurityDescriptor.AsSpan().SequenceEqual(initial.SecurityDescriptor) ||
                !string.Equals(Safety.ComputeSha256(x.Alias, CancellationToken.None),
                    x.Hash, StringComparison.OrdinalIgnoreCase) ||
                !Safety.ByteEqual(x.Alias, x.Master, CancellationToken.None))
                return false;
            return true;
        }
        catch { return false; }
    }

    private static bool Restored(Record x)
    {
        try
        {
            if (!File.Exists(x.Alias) || File.Exists(x.Backup)) return false;
            var actual = Safety.Capture(x.Alias);
            var initial = Original(x);
            return actual.Identity.LinkCount == 1 &&
                   actual.Identity.VolumeSerial == x.OriginalVolume &&
                   actual.Identity.FileIndex != x.MasterIndex &&
                   actual.Length == initial.Length &&
                   actual.Attributes == initial.Attributes &&
                   actual.CreationUtc == initial.CreationUtc &&
                   actual.ModifiedUtc == initial.ModifiedUtc &&
                   actual.SecurityDescriptor.AsSpan().SequenceEqual(initial.SecurityDescriptor) &&
                   string.Equals(Safety.ComputeSha256(x.Alias, CancellationToken.None),
                       x.Hash, StringComparison.OrdinalIgnoreCase) &&
                   (!File.Exists(x.Master) || !NativeFile.SameFile(x.Alias, x.Master));
        }
        catch { return false; }
    }

    public static Summary Inspect(string inputRoot, CancellationToken ct = default)
    {
        string root = Safety.ValidateRoot(inputRoot);
        string store = Safety.StorePath(root), folder = PathFor(root);
        if (!Directory.Exists(folder)) return new Summary(Array.Empty<Entry>());
        if (!Directory.Exists(store) ||
            (File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            return new Summary(new[] {
                new Entry("", "(ledger)", "Malformed", 0,
                    "リンク・ジャンクションの台帳は読み取りも信頼できません")
            });
        var entries = new List<Entry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = Journal.Pending().Select(x => x.OperationId).ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string file in Directory.EnumerateFileSystemEntries(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 100_000)
                throw new InvalidDataException("Completed-merge ledger contains too many entries");
            string name = Path.GetFileName(file);
            string phase = name.EndsWith(".prepared.json", StringComparison.OrdinalIgnoreCase) ?
                "prepared" : name.EndsWith(".completed.json", StringComparison.OrdinalIgnoreCase) ?
                "completed" : name.EndsWith(".restored.json", StringComparison.OrdinalIgnoreCase) ?
                "restored" : "";
            string suffix = "." + phase + ".json";
            string id = phase.Length > 0 ? name[..^suffix.Length] : "";
            try
            {
                if (phase.Length == 0 || !Guid.TryParseExact(id, "N", out _) ||
                    !ids.Add(id))
                    throw new InvalidDataException("Unknown, duplicate, or unfinished ledger name");
                Record record = Read(root, file, id);
                bool pendingJournal = pending.Contains(id);
                string state = phase switch
                {
                    "prepared" when pendingJournal => "PreparedPendingRecovery",
                    "prepared" when Linked(record) => "PreparedNeedsConfirmation",
                    "completed" when !pendingJournal && Linked(record) => "VerifiedCompleted",
                    "restored" when !pendingJournal && Restored(record) => "VerifiedRestored",
                    _ => "ManualReview"
                };
                entries.Add(new Entry(id, Path.GetRelativePath(root, record.Alias),
                    state, record.Length, state == "ManualReview" ?
                    "保存されたNTFS同一性・原本メタデータ・リンク数・内容の照合不一致" :
                    "証拠を読み取り専用で再照合済み（自動操作なし）"));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or
                UnauthorizedAccessException or JsonException or ArgumentException or
                System.ComponentModel.Win32Exception)
            {
                entries.Add(new Entry(id, "(untrusted ledger)", "Malformed", 0,
                    "未確認の台帳エントリ。削除・自動修復しない"));
            }
        }
        return new Summary(entries);
    }

#if CHAPPY_TESTING
    private static string RequireSynthetic(string rawRoot)
    {
        string root = Safety.ValidateRoot(rawRoot);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string sentinel = Path.Combine(root, ".chappy-part23-generated-only");
        if (!Under(temp, root) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sentinel) ||
            File.ReadAllText(sentinel) != "Part23 synthetic data only")
            throw new InvalidOperationException("Completed merge ledger is test-only and refuses user paths");
        return root;
    }

    private static void DurableCreate(string dest, Record payload)
    {
        string temp = dest + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, payload);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, dest);
    }

    /// <summary>
    /// Write metadata and a disk-flushed intent BEFORE deleting the last
    /// independent backup; the regular pending journal still remains.
    /// </summary>
    internal static void PrepareSynthetic(string rawRoot, PendingOperation pending,
        FileSnapshot independent)
    {
        string root = RequireSynthetic(rawRoot);
        if (pending.IndependentIdentity is null ||
            !pending.IndependentIdentity.SameFile(independent.Identity) ||
            pending.IndependentIdentity.LinkCount != 1 ||
            !Path.GetFullPath(pending.OriginalPath).Equals(
                Path.GetFullPath(independent.Path), StringComparison.OrdinalIgnoreCase) ||
            !Journal.Pending().Any(x => x.OperationId == pending.OperationId) ||
            !Safety.SameRenamedSnapshot(independent, pending.BackupPath) ||
            !string.Equals(Safety.ComputeSha256(pending.BackupPath,
                CancellationToken.None, allowDeleteSharing: true),
                pending.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !NativeFile.SameFile(independent.Path, pending.MasterPath))
            throw new InvalidDataException("Pre-deletion completed ledger intent failed independent backup proof");
        var master = NativeFile.GetIdentity(pending.MasterPath);
        var record = new Record
        {
            Schema = 1, Id = pending.OperationId,
            Alias = independent.Path, Master = pending.MasterPath, Backup = pending.BackupPath,
            Hash = pending.Sha256, Length = independent.Length,
            CreationUtcTicks = independent.CreationUtc.Ticks,
            ModifiedUtcTicks = independent.ModifiedUtc.Ticks,
            Attributes = independent.Attributes,
            OriginalVolume = independent.Identity.VolumeSerial,
            OriginalIndex = independent.Identity.FileIndex,
            MasterVolume = master.VolumeSerial, MasterIndex = master.FileIndex,
            ExpectedLinkCount = master.LinkCount,
            SecurityDescriptor = independent.SecurityDescriptor.ToArray()
        };
        string folder = PathFor(root);
        if (Directory.Exists(folder) &&
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Unknown ledger folder link");
        Directory.CreateDirectory(folder);
        if (Inspect(root).Entries.Any(e => e.State is "Malformed" or
                "PreparedNeedsConfirmation" or "PreparedPendingRecovery" or "ManualReview"))
            throw new InvalidDataException("Unresolved completed ledger blocks a new synthetic merge");
        DurableCreate(FileFor(root, record.Id, "prepared"), record);
    }

    internal static void CommitSynthetic(string rawRoot, string id)
    {
        string root = RequireSynthetic(rawRoot);
        string prepared = FileFor(root, id, "prepared");
        var record = Read(root, prepared, id);
        if (!Linked(record) || !Journal.Pending().Any(x => x.OperationId == id))
            throw new InvalidDataException("Completed ledger final commit refused changed proof/journal");
        File.Move(prepared, FileFor(root, id, "completed"));
    }

    /// <summary>
    /// Explicit synthetic recovery after a process crash. Never automatically
    /// promotes prepared evidence. Requires the old pending journal to have
    /// been independently inspected and explicitly cleared first.
    /// </summary>
    public static void ConfirmPreparedSynthetic(string rawRoot, string id)
    {
        string root = RequireSynthetic(rawRoot);
        if (Journal.Pending().Any() || Journal.UnresolvedRecordFiles().Any())
            throw new InvalidOperationException("Pending/malformed recovery journal blocks approval");
        var summary = Inspect(root);
        if (summary.Entries.Count(e => e.OperationId == id &&
                e.State == "PreparedNeedsConfirmation") != 1 ||
            summary.Entries.Any(e => e.State is "Malformed" or "ManualReview" or
                "PreparedPendingRecovery"))
            throw new InvalidDataException("Untrusted prepared ledger cannot be promoted");
        Record x = Read(root, FileFor(root, id, "prepared"), id);
        if (!Linked(x)) throw new InvalidDataException("Prepared merge changed before confirmation");
        File.Move(FileFor(root, id, "prepared"), FileFor(root, id, "completed"));
    }

    public static FileSnapshot LoadOriginalForSyntheticUndo(string rawRoot, string id)
    {
        string root = RequireSynthetic(rawRoot);
        // A previous Undo process could have died while the alias still
        // shares the original NTFS ID. The completed *merge* ledger alone
        // cannot authorize a second Undo while that attempt is unresolved.
        if (IndependentUndoCrashJournal.Inspect(root).Entries.Any(e =>
                e.OperationId == id &&
                e.Phase is not ("aborted" or "finished")))
            throw new InvalidDataException("Interrupted Undo evidence must be independently reviewed before a new Undo");
        if (Inspect(root).Entries.Count(e => e.OperationId == id &&
                e.State == "VerifiedCompleted") != 1)
            throw new InvalidDataException("No verified completed ledger available for independent Undo");
        return Original(Read(root, FileFor(root, id, "completed"), id));
    }

    /// <summary>
    /// Test-only read of original metadata across a genuine Undo cutover.
    /// Do NOT infer original attributes/security from the replaced alias:
    /// those must originate from the saved completed ledger.
    /// </summary>
    internal static FileSnapshot LoadOriginalForSyntheticUndoRecovery(
        string rawRoot, string id, string expectedAlias, string expectedMaster,
        string expectedHash, uint expectedLinks)
    {
        string root = RequireSynthetic(rawRoot);
        if (Journal.Pending().Count != 0 || Journal.UnresolvedRecordFiles().Count != 0)
            throw new InvalidDataException("Incomplete merge recovery journal blocks Undo recovery");
        string completed = FileFor(root, id, "completed");
        string restored = FileFor(root, id, "restored");
        if (File.Exists(completed) == File.Exists(restored))
            throw new InvalidDataException("Exactly one completed or restored ledger is required");
        Record record = Read(root, File.Exists(completed) ? completed : restored, id);
        if (!Path.GetFullPath(record.Alias).Equals(Path.GetFullPath(expectedAlias),
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(record.Master).Equals(Path.GetFullPath(expectedMaster),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(record.Hash, expectedHash, StringComparison.OrdinalIgnoreCase) ||
            record.ExpectedLinkCount != expectedLinks ||
            !File.Exists(record.Alias) || !File.Exists(record.Master))
            throw new InvalidDataException("Completed ledger does not match submitted Undo operation");
        var masterId = NativeFile.GetIdentity(record.Master);
        var aliasId = NativeFile.GetIdentity(record.Alias);
        if (masterId.VolumeSerial != record.MasterVolume ||
            masterId.FileIndex != record.MasterIndex ||
            aliasId.VolumeSerial != record.MasterVolume ||
            masterId.LinkCount !=
                (aliasId.SameFile(masterId) ? expectedLinks : expectedLinks - 1) ||
            aliasId.LinkCount !=
                (aliasId.SameFile(masterId) ? expectedLinks : 1) ||
            !string.Equals(Safety.ComputeSha256(record.Alias, CancellationToken.None),
                record.Hash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Safety.ComputeSha256(record.Master, CancellationToken.None),
                record.Hash, StringComparison.OrdinalIgnoreCase) ||
            !Safety.ByteEqual(record.Alias, record.Master, CancellationToken.None))
            throw new InvalidDataException("Master/alias identity or bytes diverged during Undo recovery");
        return Original(record);
    }

    public static void MarkRestoredSynthetic(string rawRoot, string id)
    {
        string root = RequireSynthetic(rawRoot);
        var file = FileFor(root, id, "completed");
        Record x = Read(root, file, id);
        if (Journal.Pending().Any() || !Restored(x))
            throw new InvalidDataException("Independently restored bytes/identity/metadata not verified");
        File.Move(file, FileFor(root, id, "restored"));
    }
#endif
}
