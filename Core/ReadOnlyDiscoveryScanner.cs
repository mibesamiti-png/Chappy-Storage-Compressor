using System.IO;
using System.Linq;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Byte-exact discovery for local NTFS/exFAT folders and entire drive roots.
/// Never captures an NTFS file identity, creates evidence, deletes, moves,
/// hard-links, or writes to the selected volume. Returned snapshots are marked
/// SearchOnly and MUST NOT enter production merge/Undo/cleanup code.
/// </summary>
public static class ReadOnlyDiscoveryScanner
{
    private sealed record Entry(string Path, long Bytes, DateTime CreatedUtc,
        DateTime ModifiedUtc, FileAttributes Attributes);

    private static bool Allowed(FileAttributes a) =>
        (a & (FileAttributes.Directory | FileAttributes.ReparsePoint |
              FileAttributes.System | FileAttributes.Offline |
              FileAttributes.Device | FileAttributes.Encrypted)) == 0;

    private static bool Same(Entry entry)
    {
        var info = new FileInfo(entry.Path);
        info.Refresh();
        return info.Exists && info.Length == entry.Bytes &&
            info.CreationTimeUtc == entry.CreatedUtc &&
            info.LastWriteTimeUtc == entry.ModifiedUtc &&
            info.Attributes == entry.Attributes && Allowed(info.Attributes);
    }

    private static bool IsManaged(string path) =>
        Safety.ExcludedFromDiscovery(Path.GetFileName(path));

    public static ScanResult Run(string root, bool mediaOnly,
        IProgress<string>? progress, CancellationToken ct,
        IProgress<ScanHashProgress>? hashProgress = null)
    {
        var selected = Safety.ResolveScanTarget(root, forceDiscovery: true);
        root = selected.Root;
        var bySize = new Dictionary<long, List<Entry>>();
        var folders = new Stack<string>();
        folders.Push(root);
        int count = 0, skipped = 0, errors = 0;
        var skips = new Dictionary<string, int>(StringComparer.Ordinal);
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        void Skip(string why)
        {
            skipped++;
            skips[why] = skips.GetValueOrDefault(why) + 1;
        }
        void Error(string why)
        {
            errors++;
            failures[why] = failures.GetValueOrDefault(why) + 1;
        }

        while (folders.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = folders.Pop();
            // Recheck each directory before descending. Never follow a
            // junction or link that was present when its parent was listed.
            try
            {
                if ((File.GetAttributes(dir) &
                     (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                { Skip("リンク・オフラインフォルダ"); continue; }
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsManaged(child))
                    { Skip("システム・管理フォルダ"); continue; }
                    try
                    {
                        if (!Allowed(File.GetAttributes(child)))
                        { Skip("リンク・システムフォルダ"); continue; }
                        folders.Push(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Error("フォルダ属性読取失敗"); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Error("フォルダ列挙失敗"); }

            try
            {
                foreach (string path in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsManaged(path) ||
                        Path.GetFileName(path).Contains(".chappy-backup-", StringComparison.OrdinalIgnoreCase))
                    { Skip("管理用・退避ファイル"); continue; }
                    if (mediaOnly && !Safety.IsMedia(path))
                    { Skip("対象外の拡張子"); continue; }
                    try
                    {
                        var info = new FileInfo(path);
                        info.Refresh();
                        if (!info.Exists || info.Length == 0)
                        { Skip("空・消失ファイル"); continue; }
                        if (!Allowed(info.Attributes))
                        { Skip("リンク・システム属性"); continue; }
                        var entry = new Entry(info.FullName, info.Length,
                            info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes);
                        if (!bySize.TryGetValue(entry.Bytes, out var list))
                            bySize[entry.Bytes] = list = new List<Entry>();
                        list.Add(entry);
                        count++;
                        if (count % 100 == 0)
                            progress?.Report($"読み取り専用でサイズ調査中: {count:N0} 件");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                        or ArgumentException or NotSupportedException)
                    { Skip("ファイル属性読取失敗"); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Error("ファイル列挙失敗"); }
        }

        var repeated = bySize.Values.Where(items => items.Count > 1)
            .SelectMany(items => items).ToArray();
        var candidates = new List<(Entry Entry, FileSnapshot Snapshot)>();
        progress?.Report($"同サイズ候補を再確認: {repeated.Length:N0} / {count:N0} 件");
        foreach (Entry e in repeated)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Same(e))
                { Skip("一次調査後の変更"); continue; }
                candidates.Add((e, new FileSnapshot
                {
                    Path = e.Path, Length = e.Bytes,
                    CreationUtc = e.CreatedUtc, ModifiedUtc = e.ModifiedUtc,
                    Attributes = e.Attributes,
                    // Deliberately NO NTFS identity or security proof.
                    Identity = new FileIdentity(0, 0, 0),
                    SecurityDescriptor = Array.Empty<byte>(),
                    SearchOnly = true
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException)
            { Skip("再検証不能"); }
        }

        long total = candidates.Sum(x => x.Snapshot.Length), settled = 0;
        int hashed = 0;
        var hashes = new Dictionary<(long Bytes, string Sha), List<(Entry Entry, FileSnapshot File)>>();
        hashProgress?.Report(new ScanHashProgress(0, candidates.Count, 0, total, 0, 0));
        foreach (var (entry, file) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Same(entry))
                { Skip("SHA直前に変更"); continue; }
                var digest = Safety.ComputeSha256(file.Path, ct,
                    hashProgress == null ? null : (bytes, length) =>
                        hashProgress.Report(new ScanHashProgress(hashed,
                            candidates.Count, settled, total, bytes, length)));
                if (!Same(entry))
                { Skip("SHA直後に変更"); continue; }
                file.Sha256 = digest;
                var key = (file.Length, digest);
                if (!hashes.TryGetValue(key, out var files))
                    hashes[key] = files = new List<(Entry, FileSnapshot)>();
                files.Add((entry, file));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException)
            { Error("SHA-256読取失敗"); }
            finally
            {
                hashed++;
                settled = checked(settled + file.Length);
                if (!ct.IsCancellationRequested)
                    hashProgress?.Report(new ScanHashProgress(hashed,
                        candidates.Count, settled, total, 0, 0));
            }
            if (hashed % Math.Max(1, (candidates.Count + 19) / 20) == 0)
                progress?.Report($"SHA-256計算（検索のみ）: {hashed:N0}/{candidates.Count:N0}");
        }

        var groups = new List<DuplicateGroup>();
        foreach (var equalHashes in hashes.Values.Where(x => x.Count > 1))
        {
            ct.ThrowIfCancellationRequested();
            var clusters = new List<List<(Entry Entry, FileSnapshot File)>>();
            foreach (var file in equalHashes)
            {
                ct.ThrowIfCancellationRequested();
                if (!Same(file.Entry))
                { Skip("最終比較前に変更"); continue; }
                bool joined = false;
                foreach (var cluster in clusters)
                {
                    try
                    {
                        if (!Same(cluster[0].Entry)) continue;
                        if (Safety.ByteEqual(file.File.Path, cluster[0].File.Path, ct) &&
                            Same(file.Entry) && Same(cluster[0].Entry))
                        {
                            cluster.Add(file);
                            joined = true;
                            break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { Error("全バイト比較失敗"); }
                }
                if (!joined) clusters.Add(new List<(Entry, FileSnapshot)> { file });
            }
            foreach (var cluster in clusters.Where(x => x.Count > 1))
            {
                // Discovery groups only: even on NTFS these snapshots have no
                // identity/ACL/ADS assurance and MUST never be merge-ready.
                if (cluster.Any(x => !Same(x.Entry)))
                { Skip("グループ確定前に変更"); continue; }
                groups.Add(new DuplicateGroup
                {
                    Hash = cluster[0].File.Sha256!,
                    Files = cluster.Select(x => x.File).ToList()
                });
            }
        }
        groups.Sort((a, b) => b.Savings.CompareTo(a.Savings));
        progress?.Report($"検索のみ完了: {groups.Count:N0}組・対象 {count:N0}件（変更なし）");
        return new ScanResult
        {
            Root = root, MediaOnly = mediaOnly,
            Groups = groups, FileCount = count, Skipped = skipped, Errors = errors,
            DeepInspectedFiles = candidates.Count,
            HashAttemptedFiles = hashed,
            SkipReasons = skips, ErrorReasons = failures,
            DiscoveryOnly = true,
            VolumeFormat = selected.Format
        };
    }
}
