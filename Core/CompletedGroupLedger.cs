using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Group-level, generated-NTFS-only integration for 3+ duplicate filenames.
/// The normal product has read-only inspection; no production merge/Undo API.
/// </summary>
public static class CompletedGroupLedger
{
    public const bool ProductionMutationEnabled = false;
    private const string Name = ".chappy-group-completed";
    private const int MaxBytes = 1024 * 1024;

    private sealed class Manifest
    {
        public Manifest() { } // public parameterless constructor for JSON rehydration
        public int Schema { get; set; } = 1;
        public string Id { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Master { get; set; } = "";
        public FileSnapshot Representative { get; set; } = null!;
        public List<FileSnapshot> Aliases { get; set; } = [];
    }
    private sealed class Checkpoint
    {
        public Checkpoint() { } // public parameterless constructor for JSON rehydration
        public int Schema { get; set; } = 1;
        public string Id { get; set; } = "";
        public List<FileIdentity> Detached { get; set; } = [];
    }
    public sealed record Entry(string OperationId, string State, int AliasCount, int DetachedCount);
    public sealed record Summary(IReadOnlyList<Entry> Entries)
    {
        public int VerifiedCompleted => Entries.Count(x => x.State == "VerifiedCompleted");
        public int VerifiedRestored => Entries.Count(x => x.State == "VerifiedRestored");
        public int ExplicitResumeRequired => Entries.Count(x => x.State == "ExplicitResumeRequired");
        public int NeedsReview => Entries.Count(x => x.State == "ManualReview");
        public bool CanMutateUserFiles => false;
    }

    private static string Folder(string root) => Path.Combine(Safety.StorePath(root), Name);
    private static string PathFor(string root, string id, string phase) =>
        Path.Combine(Folder(root), id + "." + phase + ".json");

    private static bool Under(string parent, string child) =>
        Path.GetFullPath(child).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) +
            Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Safe(string root, string path, bool store = false)
    {
        if (!Path.IsPathFullyQualified(path) || !Under(root, path) ||
            (!store && Under(Safety.StorePath(root), path))) return false;
        string? p = Path.GetDirectoryName(Path.GetFullPath(path));
        while (p != null && !p.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(p) ||
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                return false;
            p = Path.GetDirectoryName(p);
        }
        return p != null;
    }

    private static void AssertEvidence(string file)
    {
        var attrs = File.GetAttributes(file);
        if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory |
            FileAttributes.System | FileAttributes.Offline | FileAttributes.Encrypted |
            FileAttributes.SparseFile)) != 0 ||
            NativeFile.GetIdentity(file).LinkCount != 1 ||
            NativeFile.HasNamedDataStreams(file) ||
            new FileInfo(file).Length is <= 10 or > MaxBytes)
            throw new InvalidDataException("Untrusted group ledger evidence");
    }

    private static void Validate(string root, Manifest x, string id)
    {
        if (x.Schema != 1 || x.Id != id || !Guid.TryParseExact(id, "N", out _) ||
            x.Hash.Length != 64 || !x.Hash.All(Uri.IsHexDigit) ||
            x.Representative is null || x.Aliases is null ||
            x.Aliases.Count is < 2 or > 32 ||
            !Safe(root, x.Representative.Path) ||
            !Safe(root, x.Master, store: true) ||
            !Under(Safety.StorePath(root), x.Master) ||
            !x.Master.Equals(Path.Combine(Safety.StorePath(root), x.Hash +
                Path.GetExtension(x.Representative.Path).ToLowerInvariant()),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid group ledger header");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalIds = new HashSet<ulong>();
        var originals = new[] { x.Representative }.Concat(x.Aliases);
        foreach (FileSnapshot s in originals)
        {
            if (s is null || !Safe(root, s.Path) || !paths.Add(s.Path) ||
                s.Length <= 0 || s.Length != x.Representative.Length ||
                s.Identity is null || s.Identity.VolumeSerial == 0 ||
                s.Identity.VolumeSerial != x.Representative.Identity.VolumeSerial ||
                s.Identity.LinkCount != 1 || s.Identity.FileIndex == 0 ||
                !originalIds.Add(s.Identity.FileIndex) ||
                s.CreationUtc.Ticks <= 0 || s.ModifiedUtc.Ticks <= 0 ||
                s.SecurityDescriptor is null ||
                s.SecurityDescriptor.Length is < 20 or > 65536 ||
                !Safety.EligibleAttributes(s.Attributes))
                throw new InvalidDataException("Invalid original multi-alias identity/metadata");
        }
    }

    private static Manifest Read(string root, string id, string phase)
    {
        var file = PathFor(root, id, phase);
        AssertEvidence(file);
        var x = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(file)) ??
            throw new InvalidDataException("Empty group ledger");
        Validate(root, x, id);
        return x;
    }

    private static Checkpoint ReadCheckpoint(string root, Manifest x)
    {
        string file = PathFor(root, x.Id, "checkpoint");
        if (!File.Exists(file)) return new Checkpoint { Id = x.Id };
        AssertEvidence(file);
        var p = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllBytes(file)) ??
            throw new InvalidDataException("Empty Undo progress");
        if (p.Schema != 1 || p.Id != x.Id || p.Detached is null ||
            p.Detached.Count > x.Aliases.Count)
            throw new InvalidDataException("Invalid Undo checkpoint length/id");
        for (int i = 0; i < p.Detached.Count; i++)
        {
            var id = p.Detached[i];
            if (id is null || id.LinkCount != 1 ||
                id.VolumeSerial != x.Representative.Identity.VolumeSerial ||
                id.FileIndex == 0 ||
                id.FileIndex == x.Representative.Identity.FileIndex ||
                p.Detached.Take(i).Any(other => other.FileIndex == id.FileIndex))
                throw new InvalidDataException("Invalid Undo independent identities");
        }
        return p;
    }

    private static void AtomicWrite(string file, object payload, bool overwrite = false)
    {
        string tmp = file + ".tmp";
        using (var s = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(s, payload);
            s.Flush(flushToDisk: true);
        }
        File.Move(tmp, file, overwrite);
    }

    private static bool Exact(FileSnapshot original, FileSnapshot actual) =>
        original.Length == actual.Length &&
        original.CreationUtc == actual.CreationUtc &&
        original.ModifiedUtc == actual.ModifiedUtc &&
        original.Attributes == actual.Attributes &&
        original.SecurityDescriptor.AsSpan().SequenceEqual(actual.SecurityDescriptor);

    // Authoritative expected shared-link count is derived from the original
    // group manifest and durable, prefix-ordered independent Undo checkpoints.
    // Unknown aliases cannot be accepted by simply trusting the live link count.
    private static bool Verify(Manifest x, Checkpoint p)
    {
        try
        {
            var rep = Safety.Capture(x.Representative.Path);
            var master = Safety.Capture(x.Master);
            uint remaining = checked((uint)(x.Aliases.Count - p.Detached.Count));
            uint links = 2 + remaining; // representative + store master + shared aliases
            if (!rep.Identity.SameFile(x.Representative.Identity) ||
                !master.Identity.SameFile(rep.Identity) ||
                rep.Identity.LinkCount != links || master.Identity.LinkCount != links ||
                !Exact(x.Representative, rep) ||
                Safety.ComputeSha256(rep.Path, CancellationToken.None) != x.Hash ||
                Safety.ComputeSha256(master.Path, CancellationToken.None) != x.Hash)
                return false;
            for (int i = 0; i < x.Aliases.Count; i++)
            {
                FileSnapshot a = Safety.Capture(x.Aliases[i].Path);
                if (!Exact(x.Aliases[i], a) ||
                    Safety.ComputeSha256(a.Path, CancellationToken.None) != x.Hash ||
                    !Safety.ByteEqual(a.Path, rep.Path, CancellationToken.None))
                    return false;
                if (i < p.Detached.Count)
                {
                    if (!a.Identity.SameFile(p.Detached[i]) ||
                        a.Identity.LinkCount != 1 ||
                        a.Identity.SameFile(master.Identity)) return false;
                }
                else if (!a.Identity.SameFile(master.Identity) ||
                    a.Identity.LinkCount != links) return false;
            }
            return true;
        }
        catch { return false; }
    }

    public static Summary Inspect(string inputRoot)
    {
        string root = Safety.ValidateRoot(inputRoot), folder = Folder(root);
        if (!Directory.Exists(folder)) return new Summary([]);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            return new Summary([new Entry("", "ManualReview", 0, 0)]);
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFileSystemEntries(folder))
        {
            string file = Path.GetFileName(path);
            if (file.EndsWith(".checkpoint.json", StringComparison.OrdinalIgnoreCase))
                continue;
            string phase = file.EndsWith(".prepared.json", StringComparison.OrdinalIgnoreCase) ?
                "prepared" : file.EndsWith(".completed.json", StringComparison.OrdinalIgnoreCase) ?
                "completed" : file.EndsWith(".restored.json", StringComparison.OrdinalIgnoreCase) ?
                "restored" : "";
            string id = phase.Length == 0 ? "" : file[..^("." + phase + ".json").Length];
            try
            {
                if (phase.Length == 0 || !Guid.TryParseExact(id, "N", out _) ||
                    !seen.Add(id))
                    throw new InvalidDataException("Unknown or duplicate group evidence");
                Manifest x = Read(root, id, phase);
                Checkpoint p = ReadCheckpoint(root, x);
                string state = phase switch
                {
                    "prepared" => "ManualReview",
                    "completed" when Verify(x, p) && p.Detached.Count == 0 =>
                        "VerifiedCompleted",
                    "completed" when Verify(x, p) && p.Detached.Count > 0 =>
                        "ExplicitResumeRequired",
                    "restored" when Verify(x, p) && p.Detached.Count == x.Aliases.Count =>
                        "VerifiedRestored",
                    _ => "ManualReview"
                };
                entries.Add(new Entry(id, state, x.Aliases.Count + 1, p.Detached.Count));
            }
            catch (Exception ex)
            {
#if CHAPPY_TESTING
                Console.WriteLine("PART27 generated-only ledger inspection refusal: " +
                    ex.GetType().Name + " / " + ex.Message);
#endif
                entries.Add(new Entry(id, "ManualReview", 0, 0));
            }
        }
        return new Summary(entries);
    }

#if CHAPPY_TESTING
    private static string RequireSynthetic(string input)
    {
        string root = Safety.ValidateRoot(input);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string marker = Path.Combine(root, ".chappy-part23-generated-only");
        if (!Under(temp, root) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(marker) ||
            File.ReadAllText(marker) != "Part23 synthetic data only")
            throw new InvalidOperationException("Group merge/Undo is generated NTFS testing only");
        return root;
    }

    public static string MergeSynthetic(string input, DuplicateGroup group)
    {
        string root = RequireSynthetic(input);
        if (group.Files.Count is < 3 or > 33 ||
            group.BlockReason(false) is not null ||
            Journal.Pending().Any() || Journal.UnresolvedRecordFiles().Any())
            throw new InvalidDataException("Only clean 3-33 independent exact duplicates supported");
        foreach (FileSnapshot s in group.Files)
        {
            if (!Safe(root, s.Path) || !Safety.SameSnapshot(s) ||
                Safety.ComputeSha256(s.Path, CancellationToken.None) != group.Hash ||
                !Safety.ByteEqual(s.Path, group.Representative.Path, CancellationToken.None))
                throw new InvalidDataException("Changed group source cannot be persisted");
        }
        string master = Path.Combine(Safety.StorePath(root),
            group.Hash + Path.GetExtension(group.Representative.Path).ToLowerInvariant());
        if (File.Exists(master))
            throw new InvalidDataException("Unexpected existing group master");
        if (Inspect(root).Entries.Any(e => e.State != "VerifiedRestored"))
            throw new InvalidDataException("Unresolved group history blocks new test merge");
        Safety.ValidateStore(root);
        string folder = Folder(root);
        Directory.CreateDirectory(folder);
        if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Untrusted group history directory");
        string id = Guid.NewGuid().ToString("N");
        Manifest x = new()
        {
            Id = id, Hash = group.Hash, Master = master,
            Representative = group.Representative,
            Aliases = group.Files.Skip(1).ToList()
        };
        Validate(root, x, id);
        AtomicWrite(PathFor(root, id, "prepared"), x);
        // Use the REAL Core engine, not a synthetic prelinked imitation.
        // The old per-alias ledger is incompatible with changing link counts,
        // so the group-level manifest is the sole completed-operation proof.
        long expected = group.Savings;
        long actual = DedupeEngine.Run(root, [group], false, null,
            CancellationToken.None, testOnlyRecordCompletedMerges: false);
        var empty = new Checkpoint { Id = id };
        if (actual != expected || Journal.Pending().Any() ||
            Journal.UnresolvedRecordFiles().Any() || !Verify(x, empty))
            throw new InvalidDataException("Group merge incomplete: retain prepared manifest for manual review");
        File.Move(PathFor(root, id, "prepared"), PathFor(root, id, "completed"));
        return id;
    }

    public static int ResumeUndoSynthetic(string input, string id, string evidence,
        Action<int>? afterDurableAlias = null)
    {
        string root = RequireSynthetic(input);
        if (!Safe(root, evidence) || !Directory.Exists(evidence) ||
            Journal.Pending().Any() || Journal.UnresolvedRecordFiles().Any())
            throw new InvalidOperationException("Unsafe group Undo evidence/root or unresolved journal");
        var summary = Inspect(root);
        if (summary.Entries.Any(e => e.State == "ManualReview") ||
            summary.Entries.Count(e => e.OperationId == id &&
                e.State is "VerifiedCompleted" or "ExplicitResumeRequired") != 1)
            throw new InvalidDataException("Group Undo cannot proceed from unverified history");
        Manifest x = Read(root, id, "completed");
        Checkpoint p = ReadCheckpoint(root, x);
        if (!Verify(x, p))
            throw new InvalidDataException("Group identity or metadata changed");
        if (!File.Exists(PathFor(root, id, "checkpoint")))
            AtomicWrite(PathFor(root, id, "checkpoint"), p);
        for (int i = p.Detached.Count; i < x.Aliases.Count; i++)
        {
            uint expectedLinks = checked((uint)(2 + x.Aliases.Count - i));
            IndependentUndoEngine.Restore(root, x.Aliases[i], x.Master, x.Hash,
                expectedLinks, evidence);
            var actual = Safety.Capture(x.Aliases[i].Path);
            if (actual.Identity.LinkCount != 1 ||
                actual.Identity.SameFile(x.Representative.Identity) ||
                !Exact(x.Aliases[i], actual) ||
                Safety.ComputeSha256(actual.Path, CancellationToken.None) != x.Hash)
                throw new InvalidDataException("Independent group Undo incomplete; do not record success");
            p.Detached.Add(actual.Identity);
            AtomicWrite(PathFor(root, id, "checkpoint"), p, overwrite: true);
            if (!Verify(x, p))
                throw new InvalidDataException("Group Undo checkpoint failed full revalidation");
            afterDurableAlias?.Invoke(i + 1);
        }
        if (!Verify(x, p))
            throw new InvalidDataException("Full group Undo proof failed");
        File.Move(PathFor(root, id, "completed"), PathFor(root, id, "restored"));
        return p.Detached.Count;
    }
#endif
}
