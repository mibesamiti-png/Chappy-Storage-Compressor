namespace ChappyStorageCompressor.Core;

public static class DedupeEngine
{
    // The shipping UI/public interface stays locked until G2/G3/G5 gates
    // are complete. Tests keep their legacy entrypoint for older fixtures.
    public static long Run(string root, IEnumerable<DuplicateGroup> groups,
        bool allowTimestampDifference, IProgress<EngineProgress>? progress,
        CancellationToken ct
#if CHAPPY_TESTING
        , bool testOnlyRecordCompletedMerges = false,
        string? testOnlyPreparedGroupId = null
#endif
        )
    {
#if CHAPPY_TESTING
        return RunCore(root, groups, allowTimestampDifference, progress, ct,
            testOnlyRecordCompletedMerges, testOnlyPreparedGroupId);
#else
        throw new NotSupportedException(
            "v1のハードリンク統合はUndoと最終安全ゲートが完了するまでロック中です。");
#endif
    }

    // Compiled into BOTH Windows builds. There is no public/WPF caller.
    // A durable group manifest, exact original NTFS IDs, and a clean journal
    // are required BEFORE even creating the store. This single core path is
    // the foundation for later normal-build consent and independent Undo.
    internal static long RunPreparedGroup(string root, DuplicateGroup group,
        string preparedGroupId, IProgress<EngineProgress>? progress,
        CancellationToken ct)
    {
        // Same generated-only boundary as CompletedGroupLedger. A forged
        // internal caller cannot bypass it by directly invoking Core.
        root = CompletedGroupLedger.RequireSynthetic(root);
        if (!Guid.TryParseExact(preparedGroupId, "N", out _))
            throw new InvalidDataException("Missing durable group authorization");
        return RunCore(root, [group], false, progress, ct,
            false, preparedGroupId);
    }

    private static long RunCore(string root, IEnumerable<DuplicateGroup> groups,
        bool allowTimestampDifference, IProgress<EngineProgress>? progress,
        CancellationToken ct, bool testOnlyRecordCompletedMerges,
        string? preparedGroupId)
    {
        root = Safety.ValidateRoot(root);
        using var mutationLease = MutationLease.Acquire();
        CapacityGuard.AssertCanStart(root);
        // Never continue a modifying operation while another one needs recovery.
        Journal.AssertCleanBeforeMerge();
        var items = groups.ToList();
        if (preparedGroupId is not null)
        {
            if (items.Count != 1 || testOnlyRecordCompletedMerges ||
                allowTimestampDifference)
                throw new InvalidDataException(
                    "Authorized transaction must have one strict durable group");
            // One implementation in both test and normal Windows builds:
            // forged, incomplete or stale manifests fail before any writes.
            CompletedGroupLedger.AssertPreparedGroup(
                root, preparedGroupId, items[0]);
        }
        // Fail closed before even creating the store directory. A caller may
        // supply a stale/malicious ScanResult; no pathname outside the selected
        // root, in our store, or beneath a junction may become a mutation
        // target just because its SHA and metadata happen to match.
        if (items.Any(g => g.Files.Any(file =>
                !Safety.ContainedRegularSource(root, file.Path))))
            throw new InvalidDataException(
                "統合元のパスが対象フォルダ外・予約領域・リンク経由、または特殊ファイルです。変更せず停止します。");
        // Pin root/ancestors and every source parent before creating even the
        // store directory. Hold these non-delete-sharing handles until Core exits.
        using var sourceParents = NativeFile.PinnedDirectories.Open(root,
            items.SelectMany(g => g.Files.Select(file => file.Path)));
        foreach (var source in items.SelectMany(group => group.Files))
            ReplacementReadiness.AssertNoPendingImageReplacement(source.Path);
        Safety.ValidateStore(root);
        // Store is now known to exist: also pin the directory receiving all
        // master links, not just the source parents.
        using var storeParents = NativeFile.PinnedDirectories.Open(root,
            items.Select(g => Path.Combine(Safety.StorePath(root),
                g.Hash + Path.GetExtension(g.Representative.Path).ToLowerInvariant())));
        int total = items.Sum(g => Math.Max(0, g.Files.Count - 1));
        int done = 0;
        long estimatedSaved = 0;
        foreach (var group in items)
        {
            ct.ThrowIfCancellationRequested();
            Journal.AssertCleanBeforeMerge();
            sourceParents.AssertUnchanged();
            storeParents.AssertUnchanged();
            if (group.BlockReason(allowTimestampDifference) is string reason)
            {
                done += Math.Max(0, group.Files.Count - 1);
                progress?.Report(new EngineProgress(done, total, "統合不可: " + reason, estimatedSaved));
                continue;
            }

#if CHAPPY_TESTING
            // Each saved completed operation currently authenticates a fixed NTFS
            // link count. A third alias would invalidate the first durable record
            // and could leave a partially merged group. Until a group-level
            // transition ledger exists, refuse all >2-file groups *before* any
            // hardlink, rename, pending journal or backup is created.
            // Part23's separate no-ledger synthetic integration is unaffected.
            if (testOnlyRecordCompletedMerges && group.Files.Count > 2)
            {
                done += group.Files.Count - 1;
                progress?.Report(new EngineProgress(done, total,
                    "保存履歴付きの3件以上の重複統合は未対応のため変更せずスキップ: " +
                    group.Representative.Path, estimatedSaved));
                continue;
            }
#endif

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
            var pinnedSourceId = NativeFile.IdentityFromHandle(sourceGuard.SafeFileHandle);
            if (!pinnedSourceId.SameFile(source.Identity) ||
                !Safety.SameSnapshot(source) ||
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
                    // Snapshot/hash full-byte proof first. Then pin the exact
                    // independent NTFS ID for rename and eventual deletion.
                    // Never keep a second data-read handle alive during the
                    // final kernel disposition (otherwise deletion is delayed).
                    using (var duplicateGuard = File.Open(duplicate.Path, FileMode.Open,
                        FileAccess.Read, FileShare.Read | FileShare.Delete))
                    {
                        if (!NativeFile.IdentityFromHandle(
                                duplicateGuard.SafeFileHandle).SameFile(duplicate.Identity) ||
                            !Safety.SameSnapshot(duplicate) || !Safety.SameSnapshot(source) ||
                            NativeFile.GetIdentity(duplicate.Path).LinkCount != 1)
                            throw new IOException("スキャン後にファイルやハードリンク状態が変更されました");
                        if (!string.Equals(Safety.ComputeSha256(duplicate.Path, ct), group.Hash,
                            StringComparison.Ordinal) ||
                            !Safety.ByteEqual(source.Path, duplicate.Path, ct))
                            throw new IOException("内容が変更されました");
                    }
                    ct.ThrowIfCancellationRequested();
                    Journal.AssertCleanBeforeMerge();
                    using var originalPin = NativeFile.PinnedFile.Open(
                        duplicate.Path, duplicate.Identity, group.Hash);
                    if (!originalPin.PathStillMatches(duplicate.Path) ||
                        !Safety.SameSnapshot(duplicate) || !Safety.SameSnapshot(source))
                        throw new IOException("最終NTFSハンドル固定前に変更されました");

                    // Don't create the extra store link until at least one actual
                    // duplicate has passed the full preflight. This keeps a failed
                    // scan from needlessly changing the source link count.
                    if (!File.Exists(masterPath))
                    {
                        // The initial preflight was measured before hashing.
                        // Recheck both NTFS metadata and journal volume
                        // immediately BEFORE creating our first new link.
                        CapacityGuard.AssertCanStart(root);
                        NativeFile.NewHardLink(masterPath, source.Path);
                        masterCreatedByUs = true;
                    }
                    if (!NativeFile.GetIdentity(masterPath).SameFile(pinnedSourceId) ||
                        !NativeFile.SameFile(masterPath, source.Path))
                        throw new IOException("統合先が固定した代表NTFS IDと異なる実体です");

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
                        // Rename by the original held NTFS ID, never by a
                        // last-moment pathname lookup; target replacement=false.
                        originalPin.RenameNoReplace(backup);
                        backupCreated = true;
                        FaultInjection.Hit("backup-renamed");
                        if (!originalPin.PathStillMatches(backup) ||
                            !Safety.SameRenamedSnapshot(duplicate, backup))
                            throw new IOException("退避ファイルの固定ID・属性が変わりました");
                        NativeFile.NewHardLink(duplicate.Path, masterPath);
                        FaultInjection.Hit("new-link-created");
                        if (!NativeFile.SameFile(duplicate.Path, masterPath))
                            throw new IOException("ハードリンクの実体確認に失敗");
                        if (!originalPin.PathStillMatches(backup) ||
                            !Safety.SameRenamedSnapshot(duplicate, backup))
                            throw new IOException("退避ファイルの固定ID・属性が変わりました。削除しません");
                        if (!Safety.ByteEqual(backup, masterPath, CancellationToken.None,
                                allowDeleteSharing: true) ||
                            !originalPin.PathStillMatches(backup) ||
                            !Safety.SameRenamedSnapshot(duplicate, backup))
                            throw new IOException("退避ファイルの最終照合に失敗。削除しません");
                        FaultInjection.Hit("pre-backup-delete");
                        // Part32: a DIFFERENT process may move the proven
                        // backup and plant a new, byte-identical file at the
                        // same pathname between the earlier proof and delete.
                        // Hash alone does not authorize deleting a pathname:
                        // re-open the exact scanned NTFS identity immediately
                        // before irreversible backup deletion.
                        if (!originalPin.PathStillMatches(backup) ||
                            !Safety.SameRenamedSnapshot(duplicate, backup) ||
                            !Safety.ByteEqual(backup, masterPath,
                                CancellationToken.None, allowDeleteSharing: true) ||
                            !NativeFile.SameFile(duplicate.Path, masterPath) ||
                            !NativeFile.GetIdentity(masterPath).SameFile(pinnedSourceId))
                            throw new InvalidDataException(
                                "Pinned backup ID or shared master changed before irreversible delete; retain evidence");
#if CHAPPY_TESTING
                        // Durable original metadata BEFORE irreversible backup
                        // deletion. Test-only opt-in; normal product code NEVER
                        // enables completed-merge ledger mutation.
                        if (testOnlyRecordCompletedMerges)
                            CompletedMergeLedger.PrepareSynthetic(root, pending, duplicate);
#endif
                        // The backup and master IDs alone do not protect
                        // against a moved ancestor changing where pathnames
                        // resolve. Revalidate all held directory identities
                        // before the irreversible pinned-ID deletion.
                        sourceParents.AssertUnchanged();
                        storeParents.AssertUnchanged();
                        // Free space can fall while copying/hashing. Refuse
                        // destroying the independent backup if either target
                        // NTFS volume or recovery journal volume became low.
                        CapacityGuard.AssertCanStart(root);
                        if (preparedGroupId is not null)
                            CompletedGroupLedger.AssertPreparedAlias(
                                root, preparedGroupId, group, duplicate,
                                backup, masterPath, id);
                        // Delete only the now-verified, independent duplicate.
                        originalPin.DeleteCurrent(requireIndependent: true);
                        FaultInjection.Hit("backup-deleted");
#if CHAPPY_TESTING
                        if (testOnlyRecordCompletedMerges)
                            CompletedMergeLedger.CommitSynthetic(root, id);
#endif
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
                            // Part32: rollback is only safe when the backup is
                            // STILL the precise pre-scan independent file.
                            // A moved/replaced pathname (even same SHA) is
                            // external evidence, never ours to clean up.
                            // Leave the pending journal and both names for
                            // explicit manual review instead of deleting a
                            // possible foreign file or hiding the original.
                            bool exactBackup = originalPin.PathStillMatches(backup) &&
                                Safety.SameRenamedSnapshot(duplicate, backup);
                            if (exactBackup)
                            {
                                // A foreign alias is NEVER deleted, even when
                                // its SHA matches. Remove only a pinned link
                                // whose ID matches the verified master.
                                if (File.Exists(duplicate.Path))
                                {
                                    if (!NativeFile.GetIdentity(masterPath)
                                            .SameFile(pinnedSourceId))
                                        throw new InvalidDataException(
                                            "Master changed during rollback");
                                    using var ownedAlias = NativeFile.PinnedFile.Open(
                                        duplicate.Path, pinnedSourceId, group.Hash,
                                        requireIndependent: false);
                                    ownedAlias.DeleteCurrent(requireIndependent: false);
                                }
                                if (!File.Exists(duplicate.Path))
                                    originalPin.RenameNoReplace(duplicate.Path);
                            }
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
                NativeFile.GetIdentity(masterPath).SameFile(pinnedSourceId))
            {
                using var ownedMaster = NativeFile.PinnedFile.Open(
                    masterPath, pinnedSourceId, group.Hash, requireIndependent: false);
                ownedMaster.DeleteCurrent(requireIndependent: false);
            }
        }
        return estimatedSaved;
    }
}
