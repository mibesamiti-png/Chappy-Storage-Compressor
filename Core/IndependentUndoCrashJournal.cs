using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Production exposes ONLY read-only inspection. Test-only restart decisions
/// explicitly distinguish a still-shared alias from an already independent one.
/// Never infer "success" merely from a journal filename.
/// </summary>
public static class IndependentUndoCrashJournal
{
    public const bool ProductionMutationEnabled = false;
    private const int MaxBytes = 128 * 1024;
    private const string FolderName = ".chappy-independent-undo-recovery";
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    {
        "intent", "staged", "cutover", "metadata", "finished", "aborted"
    };

    private sealed class Record
    {
        public Record() { }
        public int Schema { get; set; }
        public string Id { get; set; } = "";
        public string Alias { get; set; } = "";
        public string Master { get; set; } = "";
        public string Stage { get; set; } = "";
        public string Hash { get; set; } = "";
        public uint ExpectedLinks { get; set; }
        public uint MasterVolume { get; set; }
        public ulong MasterIndex { get; set; }
        public long OriginalLength { get; set; }
        public long CreationTicks { get; set; }
        public long ModifiedTicks { get; set; }
        public FileAttributes OriginalAttributes { get; set; }
        public byte[] Security { get; set; } = Array.Empty<byte>();
    }

    public sealed record Entry(string OperationId, string Phase,
        string State, string RelativeAlias, string Explanation);
    public sealed record Summary(IReadOnlyList<Entry> Entries)
    {
        public int Incomplete => Entries.Count(e => e.State is
            "SharedNeedsExplicitRetry" or "CutoverNeedsRecovery" or
            "MetadataNeedsHistoryUpdate");
        public int ManualReview => Entries.Count(e => e.State == "ManualReview");
        public int Finished => Entries.Count(e => e.State == "VerifiedFinished");
        public string Describe() =>
            "【独立Undo中断履歴：読み取り専用】\n" +
            $"未完了: {Incomplete}件 / 要手動確認: {ManualReview}件 / 復旧確認済み: {Finished}件\n" +
            "※ 状態を表示するだけです。勝手にファイルを削除・置換・修復しません。";
    }

    private static string Folder(string root) =>
        Path.Combine(Safety.StorePath(root), FolderName);
    private static string PathFor(string root, string id, string phase) =>
        Path.Combine(Folder(root), id + "." + phase + ".json");
    private static bool Under(string parent, string child) =>
        Path.GetFullPath(child).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SafeParents(string root, string file)
    {
        if (!Under(root, file)) return false;
        string dir = Path.GetDirectoryName(Path.GetFullPath(file))!;
        while (!dir.Equals(Path.TrimEndingDirectorySeparator(root),
            StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir) ||
                (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                return false;
            dir = Path.GetDirectoryName(dir) ??
                throw new InvalidDataException("Undo journal parent directory missing");
        }
        return true;
    }

    private static bool Validate(Record x, string root, string id)
    {
        try
        {
            if (x.Schema != 1 || !Guid.TryParseExact(id, "N", out _) ||
                !string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase) ||
                x.Hash.Length != 64 || !x.Hash.All(Uri.IsHexDigit) ||
                x.ExpectedLinks < 3 || x.MasterVolume == 0 || x.MasterIndex == 0 ||
                x.OriginalLength <= 0 || x.CreationTicks <= 0 ||
                x.ModifiedTicks <= 0 || x.Security.Length is < 20 or > 65536 ||
                !Path.IsPathFullyQualified(x.Alias) ||
                !Path.IsPathFullyQualified(x.Master) ||
                !Path.IsPathFullyQualified(x.Stage) ||
                !SafeParents(root, x.Alias) ||
                Under(Safety.StorePath(root), x.Alias) ||
                !SafeParents(root, x.Master) ||
                !Under(Safety.StorePath(root), x.Master) ||
                !SafeParents(root, x.Stage) ||
                !Path.GetDirectoryName(Path.GetFullPath(x.Alias))!.Equals(
                    Path.GetDirectoryName(Path.GetFullPath(x.Stage)),
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(x.Stage).StartsWith(".chappy-part23-undo-",
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(x.Stage).EndsWith(".stage",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch { return false; }
    }

    private static Record Read(string root, string file, string id)
    {
        var attr = File.GetAttributes(file);
        if ((attr & (FileAttributes.Directory | FileAttributes.ReparsePoint |
            FileAttributes.System | FileAttributes.Encrypted |
            FileAttributes.Offline | FileAttributes.SparseFile)) != 0 ||
            NativeFile.GetIdentity(file).LinkCount != 1 ||
            NativeFile.HasNamedDataStreams(file))
            throw new InvalidDataException("Undo journal file has unsafe attributes/streams/links");
        long length = new FileInfo(file).Length;
        if (length < 16 || length > MaxBytes)
            throw new InvalidDataException("Undo journal size invalid");
        Record record = JsonSerializer.Deserialize<Record>(File.ReadAllBytes(file)) ??
            throw new InvalidDataException("Undo journal is empty");
        if (!Validate(record, root, id))
            throw new InvalidDataException("Undo journal path/schema mismatch");
        return record;
    }

    private static string StateOf(string root, Record record, string phase)
    {
        try
        {
            string master = record.Master, alias = record.Alias;
            if (!File.Exists(master) || !File.Exists(alias) ||
                Journal.Pending().Count != 0 || Journal.UnresolvedRecordFiles().Count != 0)
                return "ManualReview";
            var masterId = NativeFile.GetIdentity(master);
            var aliasId = NativeFile.GetIdentity(alias);
            if (masterId.VolumeSerial != record.MasterVolume ||
                masterId.FileIndex != record.MasterIndex ||
                !string.Equals(Safety.ComputeSha256(alias, CancellationToken.None),
                    record.Hash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Safety.ComputeSha256(master, CancellationToken.None),
                    record.Hash, StringComparison.OrdinalIgnoreCase) ||
                !Safety.ByteEqual(alias, master, CancellationToken.None))
                return "ManualReview";

            bool shared = aliasId.SameFile(masterId);
            if (shared)
            {
                if (phase is "finished" or "cutover" or "metadata" ||
                    masterId.LinkCount != record.ExpectedLinks)
                    return "ManualReview";
                if (File.Exists(record.Stage))
                {
                    var stage = Safety.Capture(record.Stage);
                    if (stage.Identity.LinkCount != 1 ||
                        stage.Identity.SameFile(masterId) ||
                        stage.Identity.VolumeSerial != masterId.VolumeSerial ||
                        stage.Length != record.OriginalLength ||
                        stage.Attributes != record.OriginalAttributes ||
                        stage.CreationUtc.Ticks != record.CreationTicks ||
                        stage.ModifiedUtc.Ticks != record.ModifiedTicks ||
                        !stage.SecurityDescriptor.AsSpan().SequenceEqual(record.Security) ||
                        !string.Equals(Safety.ComputeSha256(record.Stage,
                            CancellationToken.None), record.Hash,
                            StringComparison.OrdinalIgnoreCase))
                        return "ManualReview";
                }
                return phase == "aborted" ? "VerifiedAborted" :
                    "SharedNeedsExplicitRetry";
            }
            if (aliasId.VolumeSerial != masterId.VolumeSerial ||
                aliasId.LinkCount != 1 ||
                masterId.LinkCount != record.ExpectedLinks - 1 ||
                File.Exists(record.Stage) || phase is "intent" or "aborted")
                return "ManualReview";

            var initial = new FileSnapshot
            {
                Path = alias, Length = record.OriginalLength,
                CreationUtc = new DateTime(record.CreationTicks, DateTimeKind.Utc),
                ModifiedUtc = new DateTime(record.ModifiedTicks, DateTimeKind.Utc),
                Attributes = record.OriginalAttributes,
                Identity = new FileIdentity(record.MasterVolume, 0, 1),
                SecurityDescriptor = record.Security
            };
            var now = Safety.Capture(alias);
            bool metadataMatch = now.Length == initial.Length &&
                now.Attributes == initial.Attributes &&
                now.CreationUtc == initial.CreationUtc &&
                now.ModifiedUtc == initial.ModifiedUtc &&
                now.SecurityDescriptor.AsSpan().SequenceEqual(initial.SecurityDescriptor);
            if (phase == "finished")
                return metadataMatch &&
                    CompletedMergeLedger.Inspect(root).VerifiedRestored > 0 ?
                    "VerifiedFinished" : "ManualReview";
            return metadataMatch ? "MetadataNeedsHistoryUpdate" :
                "CutoverNeedsRecovery";
        }
        catch { return "ManualReview"; }
    }

    public static Summary Inspect(string inputRoot, CancellationToken ct = default)
    {
        string root = Safety.ValidateRoot(inputRoot);
        string folder = Folder(root), store = Safety.StorePath(root);
        if (!Directory.Exists(folder)) return new Summary(Array.Empty<Entry>());
        if (!Directory.Exists(store) ||
            (File.GetAttributes(store) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            return new Summary(new[] {
                new Entry("", "", "ManualReview", "(untrusted)",
                    "NTFS link in Undo journal directory")
            });
        var entries = new List<Entry>();
        int n = 0;
        foreach (string file in Directory.EnumerateFileSystemEntries(folder))
        {
            ct.ThrowIfCancellationRequested();
            if (++n > 100_000)
                throw new InvalidDataException("Too many Undo crash records");
            string name = Path.GetFileName(file);
            string[] parts = name.Split('.');
            string id = parts.Length == 3 ? parts[0] : "";
            string phase = parts.Length == 3 ? parts[1] : "";
            try
            {
                if (parts.Length != 3 || parts[2] != "json" ||
                    !Guid.TryParseExact(id, "N", out _) || !Phases.Contains(phase))
                    throw new InvalidDataException("Invalid Undo crash record name");
                Record record = Read(root, file, id);
                string state = StateOf(root, record, phase);
                entries.Add(new Entry(id, phase, state,
                    Path.GetRelativePath(root, record.Alias),
                    state == "ManualReview" ? "変更・異物を検出。証拠を保持し手動確認" :
                    "保存済みNTFS状態を再照合済み。自動操作なし"));
            }
            catch (Exception e) when (e is IOException or InvalidDataException or
                JsonException or UnauthorizedAccessException or ArgumentException or
                System.ComponentModel.Win32Exception)
            {
                entries.Add(new Entry(id, phase, "ManualReview",
                    "(untrusted)", "未検証のUndo記録。内容は変更しません"));
            }
        }
        return new Summary(entries);
    }

#if CHAPPY_TESTING
    private static string RequireSynthetic(string input)
    {
        string root = Safety.ValidateRoot(input);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string sentinel = Path.Combine(root, ".chappy-part23-generated-only");
        if (!Under(temp, root) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sentinel) ||
            File.ReadAllText(sentinel) != "Part23 synthetic data only")
            throw new InvalidOperationException("Undo crash writer refuses non-generated root");
        return root;
    }

    private static void Transition(string root, string id, string from, string to)
    {
        string prev = PathFor(root, id, from), next = PathFor(root, id, to);
        if (File.Exists(next)) throw new InvalidDataException("Undo phase collision");
        Read(root, prev, id);
        File.Move(prev, next);
    }

    internal static void BeginSynthetic(string input, string id, FileSnapshot original,
        string masterPath, string stage, string hash, uint expectedLinks)
    {
        string root = RequireSynthetic(input);
        var known = CompletedMergeLedger.LoadOriginalForSyntheticUndo(root, id);
        string alias = Path.GetFullPath(original.Path);
        string master = Path.GetFullPath(masterPath);
        if (!known.Path.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
            known.Identity.VolumeSerial != original.Identity.VolumeSerial ||
            known.Identity.FileIndex != original.Identity.FileIndex ||
            known.Length != original.Length ||
            known.CreationUtc != original.CreationUtc ||
            known.ModifiedUtc != original.ModifiedUtc ||
            known.Attributes != original.Attributes ||
            !known.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor) ||
            !string.Equals(hash, known.Sha256, StringComparison.OrdinalIgnoreCase) &&
                known.Sha256 is not null ||
            expectedLinks < 3)
            throw new InvalidDataException("Loaded completed ledger differs from Undo source snapshot");
        string dir = Folder(root);
        if (Directory.Exists(dir) &&
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Undo crash journal folder is a link");
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.EnumerateFiles(dir, id + ".*.json"))
            if (!file.EndsWith(".aborted.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An unresolved Undo attempt already exists");
        var identity = NativeFile.GetIdentity(master);
        var record = new Record
        {
            Schema = 1, Id = id, Alias = alias, Master = master,
            Stage = Path.GetFullPath(stage), Hash = hash,
            ExpectedLinks = expectedLinks,
            MasterVolume = identity.VolumeSerial,
            MasterIndex = identity.FileIndex,
            OriginalLength = original.Length,
            CreationTicks = original.CreationUtc.Ticks,
            ModifiedTicks = original.ModifiedUtc.Ticks,
            OriginalAttributes = original.Attributes,
            Security = original.SecurityDescriptor.ToArray()
        };
        if (!Validate(record, root, id) ||
            !NativeFile.SameFile(alias, master) ||
            identity.LinkCount != expectedLinks)
            throw new InvalidDataException("Undo intent would have invalid paths/NTFS identity");
        string path = PathFor(root, id, "intent");
        using (var stream = new FileStream(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(flushToDisk: true);
        }
    }

    internal static void MarkStageSynthetic(string root, string id) =>
        Transition(RequireSynthetic(root), id, "intent", "staged");
    internal static void MarkCutoverSynthetic(string root, string id) =>
        Transition(RequireSynthetic(root), id, "staged", "cutover");
    internal static void MarkMetadataSynthetic(string root, string id) =>
        Transition(RequireSynthetic(root), id, "cutover", "metadata");

    // EXPLICIT synthetic-only recovery. Do not delete an untrusted artifact
    // or "roll back" an already-cutover alias to shared links.
    public static string ResumeSynthetic(string input, string id,
        string evidenceFolder)
    {
        string root = RequireSynthetic(input);
        if (Journal.Pending().Any() || Journal.UnresolvedRecordFiles().Any())
            throw new InvalidDataException("Pending merge journal blocks synthetic Undo recovery");
        var summary = Inspect(root);
        var active = summary.Entries.Where(e => e.OperationId == id &&
            e.Phase is not ("aborted" or "finished")).ToArray();
        if (active.Length != 1 || summary.ManualReview != 0)
            throw new InvalidDataException("Missing or ambiguous Undo record. Manual review required.");
        var entry = active[0];
        var record = Read(root, PathFor(root, id, entry.Phase), id);
        var original = CompletedMergeLedger.LoadOriginalForSyntheticUndoRecovery(
            root, id, record.Alias, record.Master, record.Hash, record.ExpectedLinks);
        if (original.Length != record.OriginalLength ||
            original.CreationUtc.Ticks != record.CreationTicks ||
            original.ModifiedUtc.Ticks != record.ModifiedTicks ||
            original.Attributes != record.OriginalAttributes ||
            !original.SecurityDescriptor.AsSpan().SequenceEqual(record.Security))
            throw new InvalidDataException("Persistent completed ledger and Undo intent disagree");

        if (entry.State == "SharedNeedsExplicitRetry")
        {
            // The shared file never changed; after explicit trusted-stage
            // inspection, clear ONLY our exact generated attempt and retry
            // through the real IndependentUndoEngine.
            if (File.Exists(record.Stage))
            {
                var stage = Safety.Capture(record.Stage);
                if (stage.Identity.LinkCount != 1 ||
                    stage.Identity.VolumeSerial != record.MasterVolume ||
                    stage.Identity.SameFile(NativeFile.GetIdentity(record.Master)) ||
                    !string.Equals(Safety.ComputeSha256(record.Stage, CancellationToken.None),
                        record.Hash, StringComparison.OrdinalIgnoreCase) ||
                    !stage.SecurityDescriptor.AsSpan().SequenceEqual(record.Security) ||
                    stage.Length != record.OriginalLength ||
                    stage.CreationUtc.Ticks != record.CreationTicks ||
                    stage.ModifiedUtc.Ticks != record.ModifiedTicks ||
                    stage.Attributes != record.OriginalAttributes)
                    throw new InvalidDataException("Synthetic stage changed; no cleanup permitted");
                File.Delete(record.Stage);
            }
            Transition(root, id, entry.Phase, "aborted");
            Directory.CreateDirectory(evidenceFolder);
            IndependentUndoEngine.Restore(root, original, record.Master, record.Hash,
                record.ExpectedLinks, evidenceFolder,
                testOnlyCompletedOperationId: id);
            CompletedMergeLedger.MarkRestoredSynthetic(root, id);
            Transition(root, id, "metadata", "finished");
            return "RetriedFromShared";
        }
        if (entry.State is "CutoverNeedsRecovery" or "MetadataNeedsHistoryUpdate")
        {
            IndependentUndoEngine.RestoreMetadataAfterCrashSynthetic(
                root, original, record.Master, record.Hash, record.ExpectedLinks);
            if (CompletedMergeLedger.Inspect(root).VerifiedRestored == 0)
                CompletedMergeLedger.MarkRestoredSynthetic(root, id);
            Transition(root, id, entry.Phase, "finished");
            return "RepairedIndependent";
        }
        throw new InvalidDataException("Undo attempt state is not safe to resume");
    }
#endif
}
