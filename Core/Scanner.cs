namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.16: two-stage, read-only NTFS duplicate discovery.
/// Stage 1 gathers only shallow size/time/attribute metadata.
/// Stage 2 performs complete NTFS owner/ACL/ADS/identity validation and hashing
/// ONLY for sizes appearing more than once. No content is trusted from stage 1.
/// </summary>
public static class Scanner
{
    private sealed record InventoryItem(string Path, long Length, DateTime CreatedUtc,
        DateTime ModifiedUtc, FileAttributes Attributes);

    public static ScanResult Run(string root, bool mediaOnly,
        IProgress<string>? progress, CancellationToken ct,
        IProgress<ScanHashProgress>? hashProgress = null)
    {
        root = Safety.ValidateRoot(root);
        var bySize = new Dictionary<long, List<InventoryItem>>();
        var folders = new Stack<string>();
        folders.Push(root);
        int count = 0, skipped = 0, errors = 0;
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = folders.Pop();
            try
            {
                foreach (string child in Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFileName(child).Equals(Safety.StoreName,
                        StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System
                                          | FileAttributes.Offline)) != 0)
                        { skipped++; continue; }
                        folders.Push(child);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors++; }

            try
            {
                foreach (string path in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    // Ignore leftover transaction backups. An unusual file is
                    // rejected before any content is hashed, not followed.
                    if (Path.GetFileName(path).Contains(".chappy-backup-",
                        StringComparison.OrdinalIgnoreCase))
                    { skipped++; continue; }
                    if (mediaOnly && !Safety.IsMedia(path)) { skipped++; continue; }

                    try
                    {
                        var info = new FileInfo(path);
                        info.Refresh();
                        if (!info.Exists || info.Length <= 0 ||
                            !Safety.EligibleAttributes(info.Attributes))
                        { skipped++; continue; }
                        var item = new InventoryItem(info.FullName, info.Length,
                            info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes);
                        if (!bySize.TryGetValue(item.Length, out var bucket))
                            bySize[item.Length] = bucket = new List<InventoryItem>();
                        bucket.Add(item);
                        count++;
                        if (count % 100 == 0)
                            progress?.Report($"ファイルをサイズ別に調査中: {count:N0} 件");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or ArgumentException or NotSupportedException)
                    { skipped++; }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors++; }
        }

        // A unique-size file cannot form a byte-identical pair in this scan,
        // so skip expensive security-descriptor / ADS / NTFS-ID capture for it.
        // Deep checks are NEVER skipped for a candidate of a repeated size.
        var possiblePairs = bySize.Values.Where(x => x.Count > 1)
            .SelectMany(x => x).ToList();
        int captured = 0;
        var candidates = new List<FileSnapshot>(possiblePairs.Count);
        progress?.Report($"サイズが重なる候補を厳密検査中: {possiblePairs.Count:N0} / {count:N0} 件");
        foreach (var item in possiblePairs)
        {
            ct.ThrowIfCancellationRequested();
            captured++;
            try
            {
                // Always reopen and fully validate before any content read.
                // If the path changed since shallow inventory, fail closed.
                FileSnapshot snapshot = Safety.Capture(item.Path);
                if (snapshot.Length != item.Length ||
                    snapshot.CreationUtc != item.CreatedUtc ||
                    snapshot.ModifiedUtc != item.ModifiedUtc ||
                    snapshot.Attributes != item.Attributes ||
                    snapshot.Identity.LinkCount != 1)
                { skipped++; continue; }
                candidates.Add(snapshot);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            { skipped++; }
            if (captured % 100 == 0)
                progress?.Report($"厳密検査: {captured:N0} / {possiblePairs.Count:N0}");
        }

        int hashed = 0;
        // R2.17 part 3: a typed byte progress channel for the WPF bar, with
        // the old human-readable messages preserved for existing callers.
        // 'settled' includes rejected/failed candidates: it is a completion
        // metric, not an assertion that these bytes formed valid hash groups.
        long totalCandidateBytes = candidates.Sum(x => x.Length);
        long settledCandidateBytes = 0;
        int settledFiles = 0;
        hashProgress?.Report(new ScanHashProgress(0, candidates.Count, 0,
            totalCandidateBytes, 0, 0));
        ct.ThrowIfCancellationRequested();
        int hashProgressStep = Math.Max(1, (candidates.Count + 19) / 20);
        progress?.Report($"SHA-256計算を開始: 0 / {candidates.Count:N0}");
        var sameHash = new Dictionary<(long, string), List<FileSnapshot>>();
        foreach (var f in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Safety.SameSnapshot(f)) { skipped++; continue; }
                string digest = Safety.ComputeSha256(f.Path, ct,
                    hashProgress is null ? null : (read, length) =>
                        hashProgress.Report(new ScanHashProgress(settledFiles,
                            candidates.Count, settledCandidateBytes, totalCandidateBytes,
                            read, length)));
                if (!Safety.SameSnapshot(f)) { skipped++; continue; }
                f.Sha256 = digest;
                var key = (f.Length, digest);
                if (!sameHash.TryGetValue(key, out var list))
                    sameHash[key] = list = new List<FileSnapshot>();
                list.Add(f);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or System.ComponentModel.Win32Exception)
            { errors++; }
            finally
            {
                // Even a rejected candidate is now a completed unit of work.
                // Cancellation still propagates and never returns a partial result.
                settledCandidateBytes = checked(settledCandidateBytes + f.Length);
                settledFiles++;
                if (!ct.IsCancellationRequested)
                    hashProgress?.Report(new ScanHashProgress(settledFiles,
                        candidates.Count, settledCandidateBytes,
                        totalCandidateBytes, 0, 0));
            }
            hashed++;
            if (hashed % hashProgressStep == 0 || hashed == candidates.Count)
                progress?.Report($"SHA-256計算: {hashed:N0} / {candidates.Count:N0}");
        }

        var groups = new List<DuplicateGroup>();
        foreach (var hashGroup in sameHash.Values.Where(x => x.Count > 1))
        {
            ct.ThrowIfCancellationRequested();
            var clusters = new List<List<FileSnapshot>>();
            foreach (var file in hashGroup)
            {
                ct.ThrowIfCancellationRequested();
                bool joined = false;
                foreach (var cluster in clusters)
                {
                    try
                    {
                        if (Safety.ByteEqual(file.Path, cluster[0].Path, ct))
                        {
                            cluster.Add(file); joined = true; break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors++; }
                }
                if (!joined) clusters.Add(new List<FileSnapshot> { file });
            }
            foreach (var cluster in clusters.Where(x => x.Count > 1))
            {
                ct.ThrowIfCancellationRequested();
                int ids = cluster.Select(x => (x.Identity.VolumeSerial, x.Identity.FileIndex))
                    .Distinct().Count();
                if (ids < 2) continue;
                // The returned group is still revalidated by callers before
                // any estimate is accepted. No destructive operation is enabled.
                groups.Add(new DuplicateGroup { Hash = cluster[0].Sha256!, Files = cluster });
            }
        }
        groups.Sort((a, b) => b.Savings.CompareTo(a.Savings));
        progress?.Report($"完了: {groups.Count:N0}重複グループ / NTFS厳密検査 {captured:N0}件");
        return new ScanResult
        {
            Root = root, MediaOnly = mediaOnly, FileCount = count,
            Skipped = skipped, Errors = errors, Groups = groups,
            DeepInspectedFiles = captured, HashAttemptedFiles = hashed
        };
    }
}
