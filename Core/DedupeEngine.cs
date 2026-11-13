namespace ChappyStorageCompressor.Core;

public static class DedupeEngine
{
    public static long Run(string root, IEnumerable<DuplicateGroup> groups, bool allowTimestampDifference,
        IProgress<EngineProgress>? progress, CancellationToken ct)
    {
#if !CHAPPY_TESTING
        // The R1.5 shipped executable is explicitly read-only for deduplication
        // until the Windows/NTFS integration gate has been signed off.
        throw new NotSupportedException(
            "R1.5のハードリンク統合はWindows実機の安全試験待ちのためロック中です。");
#endif
        root = Safety.ValidateRoot(root);
        using var mutationLease = MutationLease.Acquire();
        CapacityGuard.AssertCanStart(root);
        // Never continue a modifying operation while another one needs recovery.
        Journal.AssertCleanBeforeMerge();
        Safety.ValidateStore(root);
        var items = groups.ToList();
        int total = items.Sum(g => Math.Max(0, g.Files.Count - 1));
        int done = 0;
        long estimatedSaved = 0;
        foreach (var group in items)
        {
            ct.ThrowIfCancellationRequested();
            Journal.AssertCleanBeforeMerge();
            if (group.BlockReason(allowTimestampDifference) is string reason)
            {
                done += Math.Max(0, group.Files.Count - 1);
                progress?.Report(new EngineProgress(done, total, "統合不可: " + reason, estimatedSaved));
                continue;
            }

            var source = group.Representative;
            string storeName = group.Hash + Path.GetExtension(source.Path).ToLowerInvariant();
            string masterPath = Path.Combine(Safety.StorePath(root), storeName);
            bool masterCreatedByUs = false;
            int successfulInGroup = 0;

            // This read handle rejects writers for the duration of the group.
            // FILE_SHARE_DELETE is needed for rename/delete on the other file;
            // the operation still cannot be atomic against a separate deleter.
            using var sourceGuard = File.Open(source.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (!Safety.SameSnapshot(source) ||
                !string.Equals(Safety.ComputeSha256(source.Path, ct), group.Hash, StringComparison.Ordinal))
            {
                done += group.Files.Count - 1;
                progress?.Report(new EngineProgress(done, total,
                    "代表ファイルが変更されたためスキップ: " + source.Path, estimatedSaved));
                continue;
            }

            if (File.Exists(masterPath) && !NativeFile.SameFile(masterPath, source.Path))
            {
                done += group.Files.Count - 1;
                progress?.Report(new EngineProgress(done, total,
                    "別実体の統合先が存在します。手動確認が必要: " + masterPath, estimatedSaved));
                continue;
            }

            foreach (var duplicate in group.Files.Skip(1))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // This handle blocks third-party writers; a file already open
                    // for writing makes the merge fail safely before any rename.
                    using var duplicateGuard = File.Open(duplicate.Path, FileMode.Open,
                        FileAccess.Read, FileShare.Read | FileShare.Delete);
                    if (!Safety.SameSnapshot(duplicate) || !Safety.SameSnapshot(source) ||
                        NativeFile.GetIdentity(duplicate.Path).LinkCount != 1)
                        throw new IOException("スキャン後にファイルやハードリンク状態が変更されました");
                    if (!string.Equals(Safety.ComputeSha256(duplicate.Path, ct), group.Hash,
                        StringComparison.Ordinal) || !Safety.ByteEqual(source.Path, duplicate.Path, ct))
                        throw new IOException("内容が変更されました");
                    ct.ThrowIfCancellationRequested();
                    Journal.AssertCleanBeforeMerge();

                    // Don't create the extra store link until at least one actual
                    // duplicate has passed the full preflight. This keeps a failed
                    // scan from needlessly changing the source link count.
                    if (!File.Exists(masterPath))
                    {
                        NativeFile.NewHardLink(masterPath, source.Path);
                        masterCreatedByUs = true;
                    }
                    if (!NativeFile.SameFile(masterPath, source.Path))
                        throw new IOException("統合先が代表ファイルと異なる実体です");

                    string id = Guid.NewGuid().ToString("N");
                    string backup = duplicate.Path + ".chappy-backup-" + id;
                    var pending = new PendingOperation
                    {
                        OperationId = id,
                        OriginalPath = duplicate.Path,
                        BackupPath = backup,
                        MasterPath = masterPath,
                        Sha256 = group.Hash,
                        RecordedUtc = DateTime.UtcNow,
                        IndependentIdentity = duplicate.Identity,
                        MasterIdentity = NativeFile.GetIdentity(masterPath)
                    };
                    CapacityGuard.AssertCanStart(root);
                    Journal.Add(pending); // Durable intent record before the rename.
                    FaultInjection.Hit("journal-recorded");
                    bool backupCreated = false;
                    try
                    {
                        if (!Safety.SameSnapshot(duplicate) || !Safety.SameSnapshot(source))
                            throw new IOException("統合直前に更新されました");
                        File.Move(duplicate.Path, backup);
                        backupCreated = true;
                        FaultInjection.Hit("backup-renamed");
                        if (!Safety.SameRenamedSnapshot(duplicate, backup))
                            throw new IOException("退避ファイルの同一性・属性が変わりました");
                        NativeFile.NewHardLink(duplicate.Path, masterPath);
                        FaultInjection.Hit("new-link-created");
                        if (!NativeFile.SameFile(duplicate.Path, masterPath))
                            throw new IOException("ハードリンクの実体確認に失敗");
                        if (!Safety.SameRenamedSnapshot(duplicate, backup) ||
                            !string.Equals(Safety.ComputeSha256(backup, CancellationToken.None),
                                group.Hash, StringComparison.Ordinal))
                            throw new IOException("退避ファイルの内容・属性が変わりました。削除しません");
                        if (!Safety.ByteEqual(backup, masterPath, CancellationToken.None) ||
                            !Safety.SameRenamedSnapshot(duplicate, backup))
                            throw new IOException("退避ファイルの最終照合に失敗。削除しません");
                        FaultInjection.Hit("pre-backup-delete");
                        // Delete only the now-verified, independent duplicate.
                        File.Delete(backup);
                        FaultInjection.Hit("backup-deleted");
                        backupCreated = false;
                        successfulInGroup++;
                        estimatedSaved += duplicate.Length;
                        Journal.Completed(id);
                        progress?.Report(new EngineProgress(done + 1, total,
                            "統合: " + duplicate.Path, estimatedSaved));
                    }
#if CHAPPY_TESTING
                    catch (SimulatedPowerLossException) { throw; }
#endif
                    catch
                    {
                        if (backupCreated)
                        {
                            // Remove only OUR hard link (identified by file ID),
                            // and then attempt to put the independent file back.
                            if (File.Exists(duplicate.Path) &&
                                NativeFile.SameFile(duplicate.Path, masterPath))
                                File.Delete(duplicate.Path);
                            if (!File.Exists(duplicate.Path) && File.Exists(backup))
                                File.Move(backup, duplicate.Path);
                        }
                        // Do not silently erase a journal during exception handling.
                        // Recovery must verify the actual file before clearing it.
                        throw;
                    }
                }
                catch (OperationCanceledException) { throw; }
#if CHAPPY_TESTING
                catch (SimulatedPowerLossException) { throw; }
#endif
                catch (Exception ex)
                {
                    progress?.Report(new EngineProgress(done + 1, total,
                        "スキップ（変更/競合/アクセス等）: " + duplicate.Path + " — " + ex.Message,
                        estimatedSaved));
                    // Fail closed if any failed transaction left a pending record.
                    Journal.AssertCleanBeforeMerge();
                }
                done++;
            }
            // If no duplicate could be merged, don't keep a newly made link
            // that would otherwise hide the source from future scans.
            if (successfulInGroup == 0 && masterCreatedByUs && File.Exists(masterPath) &&
                NativeFile.SameFile(masterPath, source.Path))
                File.Delete(masterPath);
        }
        return estimatedSaved;
    }
}
