using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// G3-05: the EXISTING Part35 collision transaction is compiled in both
/// Windows builds, but every mutating entrypoint is INTERNAL and rejects
/// anything except a disposable generated TEMP+sentinel root. There is no
/// public or normal-WPF mutation caller until all G3/G5 gates are approved.
/// </summary>
public static class CollisionRecoveryLedger
{
    public const bool ProductionMutationEnabled = false;

    // Normal-compiled internal Core with its original generated-root guard.
    // No production accessible write APIs are exposed.
    private const string FolderName = ".chappy-collision-journal";
    private const string Sentinel = ".chappy-part23-generated-only";
    private const string SentinelText = "Part23 synthetic data only";
    private const int MaxFiles = 64;

    public sealed record SourceItem(string SourcePath, string OriginalRelativePath,
        string DestinationRelativePath, bool Collected, string Reason,
        string Hash, uint Volume, ulong FileIndex, long Length,
        long CreationTicks, long ModifiedTicks, string SecurityHash);

    public sealed record Intent(int Schema, string Id, string Root,
        string CollectionRelativePath, string ReportRelativePath,
        List<SourceItem> Items);

    public sealed record Receipt(int Schema, string Id, int Index,
        string DestinationRelativePath, string Hash, uint Volume,
        ulong FileIndex, long Length, long CreationTicks, long ModifiedTicks,
        FileAttributes Attributes, string SecurityHash);

    public sealed record Status(string State, int VerifiedFiles,
        int ExpectedFiles, string Explanation)
    {
        public bool CanExplicitlyResume => State == "ExplicitResumeAvailable";
        public bool RequiresManualReview => State == "ManualReview";
    }

    private static string RequireOwnedRoot(string input)
    {
        string root = Safety.ValidateRoot(input);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, Sentinel)) ||
            File.ReadAllText(Path.Combine(root, Sentinel)) != SentinelText)
            throw new InvalidOperationException("Part35 only accepts generated sentinel NTFS root");
        return root;
    }

    private static string OwnedOutput(string testRoot, string outputRoot)
    {
        string output = Safety.ValidateRoot(outputRoot);
        if (!output.StartsWith(testRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Part35 destination is outside generated fixture");
        return output;
    }

    private static string StrictRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) ||
            Path.IsPathFullyQualified(relative) ||
            relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("Part35 invalid relative destination");
        string[] pieces = relative.Replace('\\', '/').Split('/');
        if (pieces.Any(p => p is "" or "." or ".." ||
            p.EndsWith(' ') || p.EndsWith('.') || p.Contains(':') ||
            p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Part35 unsafe filename");
        string path = Path.GetFullPath(Path.Combine(root, Path.Combine(pieces)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Part35 path escaped generated output");
        return path;
    }

    private static void ParentsNotLinked(string output, string full, bool create)
    {
        string rel = Path.GetRelativePath(output, Path.GetDirectoryName(full)!);
        string current = output;
        if (rel == ".") return;
        foreach (string piece in rel.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, piece);
            if (File.Exists(current))
                throw new InvalidDataException("Parent filename occupied by ordinary file");
            if (Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Untrusted reparse directory");
            }
            else if (create)
                Directory.CreateDirectory(current);
        }
    }

    private static string Folder(string output) => Path.Combine(output, FolderName);
    private static string ManifestPath(string output, string id) =>
        Path.Combine(Folder(output), id + ".intent.json");
    private static string ReceiptPath(string output, string id, int index) =>
        Path.Combine(Folder(output), id + ".item-" + index.ToString("D4") + ".json");
    private static string ReportReceiptPath(string output, string id) =>
        Path.Combine(Folder(output), id + ".report.json");
    private static string CompletePath(string output, string id) =>
        Path.Combine(Folder(output), id + ".complete");

    private static void CreateDurable(string destination, byte[] data)
    {
        // This is a create-only evidence chain. No update, overwrite or
        // delete is ever used, even when a crash leaves a .tmp record.
        string temporary = destination + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            file.Write(data);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, destination, overwrite: false);
    }

    private static byte[] JsonBytes<T>(T obj) =>
        JsonSerializer.SerializeToUtf8Bytes(obj,
            new JsonSerializerOptions { WriteIndented = true });

    private static (FileSnapshot Snapshot, string Hash) CaptureExact(string file)
    {
        FileSnapshot before = Safety.Capture(file);
        if (before.Identity.LinkCount != 1 ||
            NativeFile.HasNamedDataStreams(file))
            throw new InvalidDataException("Part35 source is not a single regular file");
        string hash = Safety.ComputeSha256(file, CancellationToken.None);
        if (!Safety.SameSnapshot(before) ||
            Safety.ComputeSha256(file, CancellationToken.None) != hash)
            throw new InvalidDataException("Part35 source changed while computing hash");
        return (before, hash);
    }

    private static bool SourceMatches(SourceItem item)
    {
        try
        {
            FileSnapshot snapshot = Safety.Capture(item.SourcePath);
            return snapshot.Identity.VolumeSerial == item.Volume &&
                snapshot.Identity.FileIndex == item.FileIndex &&
                snapshot.Identity.LinkCount == 1 &&
                snapshot.Length == item.Length &&
                snapshot.CreationUtc.Ticks == item.CreationTicks &&
                snapshot.ModifiedUtc.Ticks == item.ModifiedTicks &&
                Convert.ToHexString(SHA256.HashData(snapshot.SecurityDescriptor)) ==
                    item.SecurityHash &&
                Safety.ComputeSha256(item.SourcePath, CancellationToken.None) ==
                    item.Hash &&
                Safety.SameSnapshot(snapshot);
        }
        catch { return false; }
    }

    private static string RequireSource(string testRoot, string source)
    {
        string full = Path.GetFullPath(source);
        string legacy = Path.Combine(testRoot, "part35-incoming");
        bool generatedIncoming = full.StartsWith(legacy + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(full) ||
            !full.StartsWith(testRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Part35 source outside generated test root");
        ParentsNotLinked(testRoot, full, create: false);
        if (!generatedIncoming)
        {
            // G3-05: a second allowed source is an already INDEPENDENT alias
            // authenticated from this root's completed->restored group ledger.
            // A shared master, prepared alias, unverified foreign path or
            // replaced source is never treated as a collision export source.
            var current = Safety.Capture(full);
            var entry = CompletedGroupLedger.InspectOriginals(testRoot)
                .SingleOrDefault(x => x.State == "VerifiedRestored" &&
                    !x.Representative && x.Path.Equals(full,
                        StringComparison.OrdinalIgnoreCase));
            if (entry is null || current.Identity.LinkCount != 1 ||
                current.Length != entry.Length ||
                current.CreationUtc != entry.CreationUtc ||
                current.ModifiedUtc != entry.ModifiedUtc ||
                current.Attributes != entry.Attributes ||
                Convert.ToHexString(SHA256.HashData(current.SecurityDescriptor)) !=
                    entry.OwnerDaclFingerprint ||
                Safety.ComputeSha256(full, CancellationToken.None) != entry.Sha256)
                throw new InvalidDataException(
                    "Only verified independently restored group aliases may be exported");
        }
        return full;
    }

    // G3-05 synthetic integration: the existing group Undo restores every
    // alias FIRST. Only then can the existing collision collection transaction
    // copy independently restored files to a separate destination, without
    // touching another file that occupies the requested output name.
    // The collision journal remains the one already used by Part35.
    internal static string BeginRestoredUndoExportSynthetic(string testRoot,
        string operationId, string outputRoot)
    {
        string root = RequireOwnedRoot(testRoot);
        string output = OwnedOutput(root, outputRoot);
        var groups = CompletedGroupLedger.Inspect(root);
        if (!Guid.TryParseExact(operationId, "N", out _) ||
            groups.Entries.Count(x => x.OperationId == operationId &&
                x.State == "VerifiedRestored") != 1 ||
            groups.Entries.Any(x => x.State == "ManualReview"))
            throw new InvalidDataException(
                "Unverified or incomplete group Undo cannot be collision-exported");
        var aliases = CompletedGroupLedger.InspectOriginals(root)
            .Where(x => x.OperationId == operationId &&
                x.State == "VerifiedRestored" && !x.Representative)
            .ToArray();
        if (aliases.Length is < 1 or > 32)
            throw new InvalidDataException("No complete independent group aliases");
        var sources = aliases.Select(x =>
            (Source: RequireSource(root, x.Path),
             Relative: Path.GetRelativePath(root, x.Path))).ToArray();
        var plan = CollisionCollectionPlan.Inspect(output,
            sources.Select(x => x.Relative));
        return BeginSynthetic(root, plan, sources);
    }

    private static Intent Read(string testRoot, string output, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new InvalidDataException("Part35 malformed journal ID");
        string folder = Folder(output);
        if (!Directory.Exists(folder) ||
            (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Part35 missing/unsafe durable journal folder");
        string path = ManifestPath(output, id);
        var manifest = JsonSerializer.Deserialize<Intent>(File.ReadAllBytes(path)) ??
            throw new InvalidDataException("Part35 empty durable intent");
        if (manifest.Schema != 1 || manifest.Id != id ||
            !manifest.Root.Equals(output, StringComparison.OrdinalIgnoreCase) ||
            manifest.Items is null || manifest.Items.Count is < 1 or > MaxFiles ||
            manifest.Items.Any(x => x is null) ||
            manifest.Items.Any(x => x.Hash.Length != 64 || !x.Hash.All(Uri.IsHexDigit) ||
                x.SecurityHash.Length != 64 || !x.SecurityHash.All(Uri.IsHexDigit) ||
                x.Length <= 0 || x.FileIndex == 0 || x.Volume == 0 ||
                x.CreationTicks <= 0 || x.ModifiedTicks <= 0))
            throw new InvalidDataException("Part35 malformed intent structure");
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SourceItem item in manifest.Items)
        {
            RequireSource(testRoot, item.SourcePath);
            if (item.OriginalRelativePath !=
                Path.GetRelativePath(output,
                    StrictRelative(output, item.OriginalRelativePath)) ||
                item.DestinationRelativePath !=
                Path.GetRelativePath(output,
                    StrictRelative(output, item.DestinationRelativePath)) ||
                !targetNames.Add(item.DestinationRelativePath))
                throw new InvalidDataException("Part35 invalid or duplicate output paths");
            // Never write into or overwrite the journal itself.
            if (item.DestinationRelativePath.Equals(FolderName,
                    StringComparison.OrdinalIgnoreCase) ||
                item.DestinationRelativePath.StartsWith(FolderName +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Part35 journal path is not an output");
        }
        if (manifest.Items.Any(x => x.Collected) !=
            !string.IsNullOrEmpty(manifest.CollectionRelativePath))
            throw new InvalidDataException("Part35 inconsistent collision folder");
        if (manifest.Items.Any(x => x.Collected) !=
            !string.IsNullOrEmpty(manifest.ReportRelativePath))
            throw new InvalidDataException("Part35 inconsistent report path");
        if (manifest.ReportRelativePath.Length > 0)
        {
            string report = StrictRelative(output, manifest.ReportRelativePath);
            if (!Path.GetDirectoryName(report)!.Equals(output,
                    StringComparison.OrdinalIgnoreCase) ||
                targetNames.Contains(manifest.ReportRelativePath))
                throw new InvalidDataException("Part35 report overwrites an output");
        }
        return manifest;
    }

    private static Receipt ReadReceipt(string path, string id, int index,
        string targetRelative)
    {
        var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllBytes(path)) ??
            throw new InvalidDataException("Part35 empty receipt");
        if (receipt.Schema != 1 || receipt.Id != id || receipt.Index != index ||
            !receipt.DestinationRelativePath.Equals(targetRelative,
                StringComparison.OrdinalIgnoreCase) ||
            receipt.Hash.Length != 64 || !receipt.Hash.All(Uri.IsHexDigit) ||
            receipt.Volume == 0 || receipt.FileIndex == 0 || receipt.Length <= 0 ||
            receipt.CreationTicks <= 0 || receipt.ModifiedTicks <= 0 ||
            receipt.SecurityHash.Length != 64 ||
            !receipt.SecurityHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Part35 malformed exact output receipt");
        return receipt;
    }

    private static bool TargetMatches(string output, Receipt receipt)
    {
        try
        {
            string path = StrictRelative(output, receipt.DestinationRelativePath);
            ParentsNotLinked(output, path, create: false);
            FileSnapshot now = Safety.Capture(path);
            return now.Identity.VolumeSerial == receipt.Volume &&
                now.Identity.FileIndex == receipt.FileIndex &&
                now.Identity.LinkCount == 1 &&
                now.Length == receipt.Length &&
                now.CreationUtc.Ticks == receipt.CreationTicks &&
                now.ModifiedUtc.Ticks == receipt.ModifiedTicks &&
                now.Attributes == receipt.Attributes &&
                Convert.ToHexString(SHA256.HashData(now.SecurityDescriptor)) ==
                    receipt.SecurityHash &&
                Safety.ComputeSha256(path, CancellationToken.None) == receipt.Hash &&
                Safety.SameSnapshot(now);
        }
        catch { return false; }
    }

    private static Receipt MakeReceipt(string output, string id, int index,
        string relative, string hash)
    {
        var snapshot = Safety.Capture(StrictRelative(output, relative));
        if (snapshot.Identity.LinkCount != 1 ||
            Safety.ComputeSha256(snapshot.Path, CancellationToken.None) != hash ||
            !Safety.SameSnapshot(snapshot))
            throw new InvalidDataException("Part35 output failed before durable receipt");
        return new Receipt(1, id, index, relative, hash,
            snapshot.Identity.VolumeSerial, snapshot.Identity.FileIndex,
            snapshot.Length, snapshot.CreationUtc.Ticks, snapshot.ModifiedUtc.Ticks,
            snapshot.Attributes,
            Convert.ToHexString(SHA256.HashData(snapshot.SecurityDescriptor)));
    }

    private static CollisionCollectionPlan.Result Plan(Intent x) =>
        new(x.Root, x.CollectionRelativePath, x.ReportRelativePath,
            x.Items.Select(i => new CollisionCollectionPlan.Entry(
                i.OriginalRelativePath, i.DestinationRelativePath, i.Collected,
                i.Reason)).ToList());

    private static byte[] ReportBytes(Intent x)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return utf8.GetPreamble().Concat(utf8.GetBytes(
            CollisionCollectionPlan.ReportText(Plan(x)))).ToArray();
    }

    internal static string BeginSynthetic(string testRoot, CollisionCollectionPlan.Result plan,
        IReadOnlyList<(string Source, string Relative)> incoming)
    {
        using var lease = MutationLease.Acquire();
        string root = RequireOwnedRoot(testRoot);
        string output = OwnedOutput(root, plan.Root);
        if (incoming.Count is < 1 or > MaxFiles ||
            incoming.Count != plan.Entries.Count)
            throw new InvalidDataException("Part35 invalid source count");
        var fresh = CollisionCollectionPlan.Inspect(output,
            incoming.Select(x => x.Relative));
        if (!fresh.Entries.SequenceEqual(plan.Entries) ||
            fresh.CollectionRelativePath != plan.CollectionRelativePath ||
            fresh.ReportRelativePath != plan.ReportRelativePath)
            throw new InvalidDataException("Part35 preview stale before intent");
        string evidence = Folder(output);
        if (File.Exists(evidence) ||
            (Directory.Exists(evidence) &&
             (File.GetAttributes(evidence) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("Part35 untrusted journal path");
        // Do not silently start another conflicting extraction while
        // previous incomplete evidence is present.
        if (Directory.Exists(evidence) &&
            Directory.EnumerateFileSystemEntries(evidence).Any())
            throw new InvalidDataException("Part35 existing journal requires explicit inspection");
        var items = new List<SourceItem>();
        for (int i = 0; i < incoming.Count; i++)
        {
            string source = RequireSource(root, incoming[i].Source);
            if (incoming[i].Relative != plan.Entries[i].OriginalRelativePath)
                throw new InvalidDataException("Part35 reordered source list");
            string destination = StrictRelative(output,
                plan.Entries[i].DestinationRelativePath);
            ParentsNotLinked(output, destination, create: false);
            if (File.Exists(destination) || Directory.Exists(destination))
                throw new InvalidDataException("Part35 destination occupied after preview");
            var (snapshot, hash) = CaptureExact(source);
            items.Add(new SourceItem(source, incoming[i].Relative,
                plan.Entries[i].DestinationRelativePath,
                plan.Entries[i].Collected, plan.Entries[i].Reason, hash,
                snapshot.Identity.VolumeSerial, snapshot.Identity.FileIndex,
                snapshot.Length, snapshot.CreationUtc.Ticks,
                snapshot.ModifiedUtc.Ticks,
                Convert.ToHexString(SHA256.HashData(snapshot.SecurityDescriptor))));
        }
        if (plan.Conflicts > 0)
        {
            string report = StrictRelative(output, plan.ReportRelativePath);
            if (File.Exists(report) || Directory.Exists(report))
                throw new InvalidDataException("Part35 report occupied after preview");
        }
        Directory.CreateDirectory(evidence);
        string id = Guid.NewGuid().ToString("N");
        var intent = new Intent(1, id, output, plan.CollectionRelativePath,
            plan.ReportRelativePath, items);
        CreateDurable(ManifestPath(output, id), JsonBytes(intent));
        return id;
    }

    internal static Status InspectSynthetic(string testRoot, string outputRoot, string id)
    {
        try
        {
            string root = RequireOwnedRoot(testRoot);
            string output = OwnedOutput(root, outputRoot);
            var intent = Read(root, output, id);
            string folder = Folder(output);
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { Path.GetFileName(ManifestPath(output, id)), Path.GetFileName(CompletePath(output, id)) };
            for (int i = 0; i < intent.Items.Count; i++)
                allowed.Add(Path.GetFileName(ReceiptPath(output, id, i)));
            if (intent.ReportRelativePath.Length > 0)
                allowed.Add(Path.GetFileName(ReportReceiptPath(output, id)));
            // Any extra file, including a half-flushed .tmp, could be a
            // foreign record: never infer completion from missing receipts.
            if (Directory.EnumerateFileSystemEntries(folder)
                .Any(p => !allowed.Contains(Path.GetFileName(p))))
                return new Status("ManualReview", 0, intent.Items.Count,
                    "未知・未完了の証拠ファイルあり。勝手に整理しません");
            int complete = 0;
            bool missingReceiptFound = false;
            for (int i = 0; i < intent.Items.Count; i++)
            {
                SourceItem item = intent.Items[i];
                if (!SourceMatches(item))
                    return new Status("ManualReview", complete, intent.Items.Count,
                        "元データのID・SHA・所有権/ACL/属性が記録と不一致");
                string dest = StrictRelative(output, item.DestinationRelativePath);
                ParentsNotLinked(output, dest, create: false);
                string receiptPath = ReceiptPath(output, id, i);
                if (!File.Exists(receiptPath))
                {
                    missingReceiptFound = true;
                    if (File.Exists(dest) || Directory.Exists(dest))
                        return new Status("ManualReview", complete, intent.Items.Count,
                            "書き出し済みの可能性があるが耐久レシートが無い");
                    continue;
                }
                if (missingReceiptFound)
                    return new Status("ManualReview", complete, intent.Items.Count,
                        "後続レシートだけが存在し、順番が一致しない");
                var receipt = ReadReceipt(receiptPath, id, i,
                    item.DestinationRelativePath);
                if (receipt.Hash != item.Hash ||
                    receipt.Length != item.Length ||
                    !TargetMatches(output, receipt))
                    return new Status("ManualReview", complete, intent.Items.Count,
                        "保存済みファイルとレシートのNTFS実体または内容が不一致");
                complete++;
            }
            string reportReceipt = ReportReceiptPath(output, id);
            if (intent.ReportRelativePath.Length > 0)
            {
                string reportPath = StrictRelative(output, intent.ReportRelativePath);
                if (File.Exists(reportReceipt))
                {
                    if (complete != intent.Items.Count)
                        return new Status("ManualReview", complete, intent.Items.Count,
                            "出力前に報告メモだけが完了扱い");
                    byte[] expected = ReportBytes(intent);
                    var receipt = ReadReceipt(reportReceipt, id, intent.Items.Count,
                        intent.ReportRelativePath);
                    if (receipt.Hash != Convert.ToHexString(SHA256.HashData(expected)) ||
                        !TargetMatches(output, receipt))
                        return new Status("ManualReview", complete, intent.Items.Count,
                            "報告メモのID・内容または位置が変更");
                }
                else if (File.Exists(reportPath) || Directory.Exists(reportPath))
                    return new Status("ManualReview", complete, intent.Items.Count,
                        "報告メモが書き出されたがレシートが無い");
            }
            if (File.Exists(CompletePath(output, id)))
            {
                if (complete != intent.Items.Count ||
                    (intent.ReportRelativePath.Length > 0 &&
                     !File.Exists(reportReceipt)) ||
                    File.ReadAllText(CompletePath(output, id)) != "Part35 verified complete")
                    return new Status("ManualReview", complete, intent.Items.Count,
                        "完了マーカーと書き出し・報告記録が矛盾");
                return new Status("VerifiedCompleted", complete, intent.Items.Count,
                    "全出力と報告メモをNTFSファイルID・SHAで検証済み");
            }
            return new Status("ExplicitResumeAvailable", complete, intent.Items.Count,
                "記録済みの出力を保護し、明示的な続行のみ可能");
        }
        catch (Exception ex)
        {
            return new Status("ManualReview", 0, 0,
                "証拠を読み込めません。何も変更しません: " + ex.GetType().Name);
        }
    }

    internal static Status ResumeSynthetic(string testRoot, string outputRoot, string id,
        Action<string>? afterDurablePhase = null)
    {
        using var lease = MutationLease.Acquire();
        string root = RequireOwnedRoot(testRoot);
        string output = OwnedOutput(root, outputRoot);
        var status = InspectSynthetic(root, output, id);
        if (!status.CanExplicitlyResume)
            throw new InvalidDataException(
                "Part35 evidence untrusted or already completed: " + status.State);
        Intent intent = Read(root, output, id);
        // Every receipt-authenticated prior output is immutable. Do not
        // replay, overwrite or assign it another collision number.
        for (int i = status.VerifiedFiles; i < intent.Items.Count; i++)
        {
            if (InspectSynthetic(root, output, id).State != "ExplicitResumeAvailable")
                throw new InvalidDataException("Part35 evidence changed mid-resume");
            SourceItem item = intent.Items[i];
            if (!SourceMatches(item))
                throw new InvalidDataException("Part35 input file changed before copy");
            string target = StrictRelative(output, item.DestinationRelativePath);
            ParentsNotLinked(output, target, create: true);
            // FileMode.CreateNew refuses any external file appearing at the
            // planned filename after the preview.
            using (var read = new FileStream(item.SourcePath, FileMode.Open,
                FileAccess.Read, FileShare.Read))
            using (var dest = new FileStream(target, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                read.CopyTo(dest);
                dest.Flush(flushToDisk: true);
            }
            afterDurablePhase?.Invoke("copy-flushed-before-receipt-" + (i + 1));
            if (!SourceMatches(item))
                throw new InvalidDataException("Part35 copied input changed before receipt");
            var receipt = MakeReceipt(output, id, i,
                item.DestinationRelativePath, item.Hash);
            CreateDurable(ReceiptPath(output, id, i), JsonBytes(receipt));
            afterDurablePhase?.Invoke("item-receipt-" + (i + 1));
        }
        if (intent.ReportRelativePath.Length > 0 &&
            !File.Exists(ReportReceiptPath(output, id)))
        {
            if (InspectSynthetic(root, output, id).State != "ExplicitResumeAvailable")
                throw new InvalidDataException("Part35 evidence changed before memo");
            string path = StrictRelative(output, intent.ReportRelativePath);
            byte[] bytes = ReportBytes(intent);
            using (var dest = new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                dest.Write(bytes);
                dest.Flush(flushToDisk: true);
            }
            afterDurablePhase?.Invoke("report-flushed-before-receipt");
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            var receipt = MakeReceipt(output, id, intent.Items.Count,
                intent.ReportRelativePath, hash);
            CreateDurable(ReportReceiptPath(output, id), JsonBytes(receipt));
            afterDurablePhase?.Invoke("report-receipt");
        }
        if (InspectSynthetic(root, output, id).State != "ExplicitResumeAvailable")
            throw new InvalidDataException("Part35 final receipt proof not complete");
        CreateDurable(CompletePath(output, id),
            Encoding.UTF8.GetBytes("Part35 verified complete"));
        afterDurablePhase?.Invoke("complete-written");
        Status done = InspectSynthetic(root, output, id);
        if (done.State != "VerifiedCompleted")
            throw new InvalidDataException("Part35 end state requires manual review");
        return done;
    }
}
