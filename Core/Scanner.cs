namespace ChappyStorageCompressor.Core;

public static class Scanner
{
    public static ScanResult Run(string root, bool mediaOnly, IProgress<string>? progress, CancellationToken ct)
    {
        root = Safety.ValidateRoot(root);
        var bySize = new Dictionary<long, List<FileSnapshot>>();
        var folders = new Stack<string>();
        folders.Push(root);
        int count = 0, skipped = 0, errors = 0;
        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = folders.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Path.GetFileName(child).Equals(Safety.StoreName, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var attributes = File.GetAttributes(child);
                        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) { skipped++; continue; }
                        folders.Push(child);
                    }
                    catch { errors++; }
                }
            }
            catch { errors++; }

            try
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    // Never consume leftover transaction backups as scan candidates.
                    if (Path.GetFileName(path).Contains(".chappy-backup-", StringComparison.OrdinalIgnoreCase))
                    { skipped++; continue; }
                    if (mediaOnly && !Safety.IsMedia(path)) { skipped++; continue; }
                    try
                    {
                        var snapshot = Safety.Capture(path);
                        if (snapshot.Identity.LinkCount != 1) { skipped++; continue; }
                        if (!bySize.TryGetValue(snapshot.Length, out var group))
                            bySize[snapshot.Length] = group = new List<FileSnapshot>();
                        group.Add(snapshot);
                        count++;
                        if (count % 100 == 0) progress?.Report($"ファイルを調査中: {count:N0} 件");
                    }
                    catch { skipped++; }
                }
            }
            catch { errors++; }
        }
        progress?.Report($"容量別の候補を確認中: {count:N0} 件");
        var candidates = bySize.Values.Where(f => f.Count > 1).SelectMany(f => f).ToList();
        int hashed = 0;
        var sameHash = new Dictionary<(long, string), List<FileSnapshot>>();
        foreach (var f in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Safety.SameSnapshot(f)) { skipped++; continue; }
                var digest = Safety.ComputeSha256(f.Path, ct);
                if (!Safety.SameSnapshot(f)) { skipped++; continue; }
                f.Sha256 = digest;
                var key = (f.Length, digest);
                if (!sameHash.TryGetValue(key, out var list)) sameHash[key] = list = new List<FileSnapshot>();
                list.Add(f);
            }
            catch (OperationCanceledException) { throw; }
            catch { errors++; }
            hashed++;
            if (hashed % 100 == 0 || hashed == candidates.Count)
                progress?.Report($"SHA-256 計算: {hashed:N0} / {candidates.Count:N0}");
        }

        var groups = new List<DuplicateGroup>();
        foreach (var hashGroup in sameHash.Values.Where(x => x.Count > 1))
        {
            ct.ThrowIfCancellationRequested();
            var clusters = new List<List<FileSnapshot>>();
            foreach (var file in hashGroup)
            {
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
                    catch { errors++; }
                }
                if (!joined) clusters.Add(new List<FileSnapshot> { file });
            }
            foreach (var cluster in clusters.Where(x => x.Count > 1))
            {
                var ids = cluster.Select(x => (x.Identity.VolumeSerial, x.Identity.FileIndex)).Distinct().Count();
                if (ids < 2) continue;
                groups.Add(new DuplicateGroup { Hash = cluster[0].Sha256!, Files = cluster });
            }
        }
        groups.Sort((a, b) => b.Savings.CompareTo(a.Savings));
        progress?.Report($"完了: {groups.Count:N0} 重複グループ");
        return new ScanResult { Root = root, MediaOnly = mediaOnly, FileCount = count, Skipped = skipped, Errors = errors, Groups = groups };
    }
}
