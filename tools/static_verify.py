#!/usr/bin/env python3
"""Non-Windows package structure checks. Not a C# compiler."""
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parents[1]
errors = []
def check(name, ok):
    print(('PASS' if ok else 'FAIL') + '  ' + name)
    if not ok:
        errors.append(name)

for path in [root/'App.xaml', root/'MainWindow.xaml',root/'RecoveryWindow.xaml', root/'RecoveryWindow.xaml.cs', root/'Core/RecoveryService.cs',root/'ChappyStorageCompressor.csproj',root/'tests/ChappyStorageCompressor.SmokeTests.csproj',root/'.github/workflows/build-windows.yml']:
    check('exists: '+str(path.relative_to(root)),path.is_file())
for path in (root/'App.xaml',root/'MainWindow.xaml',root/'RecoveryWindow.xaml',root/'ChappyStorageCompressor.csproj',root/'tests/ChappyStorageCompressor.SmokeTests.csproj'):
    try:
        ET.parse(path)
        check('XML: '+path.name,True)
    except Exception as ex:
        check('XML: '+path.name,False)
        print('  ',ex)

events = set(re.findall(r'(?<![A-Za-z0-9])(?:Click|TextChanged|Checked|Unchecked|SelectionChanged)="([A-Za-z_][A-Za-z_0-9]*)"', (root/'MainWindow.xaml').read_text(encoding='utf-8')))
source = (root/'MainWindow.xaml.cs').read_text(encoding='utf-8')
for event in sorted(events):
    check('XAML handler: '+event, bool(re.search(r'\b'+re.escape(event)+r'\s*\(',source)))

recovery_xaml = (root/'RecoveryWindow.xaml').read_text(encoding='utf-8')
recovery_code = (root/'RecoveryWindow.xaml.cs').read_text(encoding='utf-8')
for event in sorted(set(re.findall(r'(?<![A-Za-z0-9])(?:Click|SelectionChanged)="([A-Za-z_][A-Za-z_0-9]*)"', recovery_xaml))):
    check('Recovery XAML handler: '+event, bool(re.search(r'\b'+re.escape(event)+r'\s*\(',recovery_code)))

recovery = (root/'Core/RecoveryService.cs').read_text(encoding='utf-8')
for action in ['RestoreIndependentBackup','UndoNewHardLink','ClearVerifiedJournal','ManualReview','MalformedRecord']:
    check('recovery state: '+action, action in recovery)
# R2.17 B2: direct pathname-based File.Move/Delete are not valid proof of
# ownership. Demand the SAME recorded-ID pinned primitive as DedupeEngine.
for guard in ['ValidateContext(p)',
              'RequireFresh(p, expected, allowDeleteSharing: true)',
              'IndependentBackupMatches(p, ct, allowDeleteSharing)',
              'NativeFile.SameFile(p.OriginalPath, p.MasterPath)',
              'NativeFile.PinnedFile.Open(',
              'originalBackup.RenameNoReplace(p.OriginalPath)',
              'aliasPin.DeleteCurrent(requireIndependent: false)',
              'backupPin.RenameNoReplace(p.OriginalPath)',
              'ProvenOriginalForJournalClear(p, ct, allowDeleteSharing)']:
    check('recovery ID guard: '+guard, guard in recovery)
check('no unsafe pathname recovery moves or deletes',
      'File.Move(p.BackupPath, p.OriginalPath)' not in recovery and
      'File.Delete(p.OriginalPath)' not in recovery and
      'File.Delete(p.BackupPath)' not in recovery)
check('legacy SHA-only journals cannot authorize recovery',
      'p.IndependentIdentity is null' in recovery and
      'p.MasterIdentity is null' in recovery and
      'ManualReview' in recovery)
check('single-record apply confirmation', 'MessageBoxButton.YesNo' in recovery_code)

core = (root/'Core/DedupeEngine.cs').read_text(encoding='utf-8')
test = (root/'tests/Program.cs').read_text(encoding='utf-8')
for key in ['journal-recorded','backup-renamed','new-link-created','pre-backup-delete','backup-deleted']:
    check('fault point in app and test: '+key, key in core and key in test)

check('recovery crash checkpoint covered',
      'recovery-link-removed' in recovery and 'recovery-link-removed' in test)
check('no test injection in main project constants', 'CHAPPY_TESTING' not in (root/'ChappyStorageCompressor.csproj').read_text(encoding='utf-8'))
check('test injection compile constant present', 'CHAPPY_TESTING' in (root/'tests/ChappyStorageCompressor.SmokeTests.csproj').read_text(encoding='utf-8'))
check('journal record validation exists', 'ValidRecord' in (root/'Core/Journal.cs').read_text(encoding='utf-8'))
check('Windows test script exists', (root/'RUN_TESTS.bat').is_file())
check('R1.4: WPF singleton mutex', 'ChappyStorageCompressor.SingleInstance' in (root/'App.xaml.cs').read_text(encoding='utf-8'))
check('R1.4: merge lease', 'MutationLease.Acquire()' in core and 'MutationLease.Acquire()' in recovery)
journal_source = (root/'Core/Journal.cs').read_text(encoding='utf-8')
check('R1.4/B2: journal recovery serialized by common RecoveryService',
      'RecoveryService.Apply(' in journal_source and
      'MutationLease.Acquire()' in recovery and
      'File.Move(p.BackupPath, p.OriginalPath)' not in journal_source and
      'File.Delete(p.OriginalPath)' not in journal_source)
check('R1.4: low-space gate', 'CapacityGuard.AssertCanStart(root)' in core)
check('R1.4: PNG inspector read-only', 'FileAccess.Read' in (root/'Core/PngInspector.cs').read_text(encoding='utf-8'))
check('R1.4: GUI PNG button', 'InspectPng_Click' in source)
check('R1.4: guard and PNG Windows tests', 'TestR14Guards(root)' in test)

optimizer = (root/'Core/PngLosslessOptimizer.cs').read_text(encoding='utf-8')
check('R1.5: preview-only PNG engine exists', 'CreatePreview' in optimizer and 'FileMode.CreateNew' in optimizer)
check('R1.5: PNG raw-scanline equality gate', 'SameInflatedData(source, candidate, ct)' in optimizer)
check('R1.5: all non-IDAT chunks are compared', 'SameNonIdatChunks(source, candidate, ct)' in optimizer)
check('R1.5: no overwrite source', 'File.Delete(source)' not in optimizer and 'File.Move(candidate, source)' not in optimizer)
check('R1.5: CRC structural check and unsafe metadata guard',
      'PngInspector.Inspect(candidate, ct)' in optimizer and 'KnownAncillary.Contains' in optimizer)
check('R1.5: preview GUI wired', 'PngPreview_Click' in source and 'PngPreviewButton.IsEnabled' in source)

# G4-01/02: the existing WPF readiness report must independently recheck
# persisted PNG/JPEG bytes and image fidelity, not rely on preview-plan counts.
replacement_readiness = (root/'Core/ReplacementReadiness.cs').read_text(encoding='utf-8')
png_optimizer = (root/'Core/PngLosslessOptimizer.cs').read_text(encoding='utf-8')
jpeg_engine = (root/'Core/JpegLosslessPreviewEngine.cs').read_text(encoding='utf-8')
jpeg_audit = (root/'Core/JpegMarkerAudit.cs').read_text(encoding='utf-8')
jpeg_metadata_tests = (root/'tests/R217JpegMetadataSimdProfile.cs').read_text(encoding='utf-8')
check('G4-01: old saved PNGs rechecked with CRC, full filtered scanlines, non-IDAT metadata, WIC BGRA alpha',
      'internal static bool EquivalentForReplacement(' in png_optimizer and
      'PngInspector.Inspect(original, ct)' in png_optimizer and
      'SameNonIdatChunks(original, candidate, ct)' in png_optimizer and
      'SameInflatedData(original, candidate, ct)' in png_optimizer and
      'PixelFormats.Bgra32' in png_optimizer and
      'G4-01: 5種類のPNGで画素/透明度/非IDATチャンクを再確認:' in test and
      'G4-01: 候補のPNGメタデータ/CRCを後から改ざんした場合は拒否' in test)
check('G4-02/03/05: saved candidate must be rechecked with whole bytes, NTFS ID/ADS/link and JPEG metadata+WIC',
      'public static FidelityDecision InspectCandidatePair(' in replacement_readiness and
      'Safety.Capture(original)' in replacement_readiness and
      'FileShare.Read' in replacement_readiness and
      'Safety.ComputeSha256(original, ct)' in replacement_readiness and
      'NativeFile.GetIdentity(candidate).LinkCount != 1' in replacement_readiness and
      'JpegMarkerAudit.Inspect(original, ct)' in replacement_readiness and
      'JpegLosslessPreviewEngine.EquivalentDecodedPixels(' in replacement_readiness and
      'PngLosslessOptimizer.EquivalentForReplacement(original, candidate, ct)' in replacement_readiness and
      'PngBatchJournalTools.ReadAudit(input, output, ct)' in replacement_readiness and
      'JpegBatchPreviewEngine.Inspect(' in replacement_readiness and
      'var fidelity = InspectCandidatePair(path, saved.Path, ct);' in replacement_readiness and
      'candidate = JpegLosslessPreviewEngine.CreatePreview(original, output, tool);' in jpeg_metadata_tests and
      'var fidelity = ReplacementReadiness.InspectCandidatePair(' in jpeg_metadata_tests and
      'G4-03: 書込み可能な排他ハンドルが開いた原本では置換直前の検査を拒否' in test)

# G4-03: pre-Apply PNG/JPEG authentication must bind the saved *original*
# source hash to actual bytes, not merely compare today's source to itself.
png_queue = (root/'Core/PngBatchPreviewEngine.cs').read_text(encoding='utf-8')
png_journal = (root/'Core/PngBatchJournalTools.cs').read_text(encoding='utf-8')
jpeg_queue = (root/'Core/JpegBatchPreviewEngine.cs').read_text(encoding='utf-8')
check('G4-03: PNG source SHA persisted during preview and reread on audit/resume',
      'public string? SourceSha256 { get; set; }' in png_queue and
      'entry.SourceSha256 = originalSha;' in png_queue and
      'Safety.SameSnapshot(originalSnapshot)' in png_queue and
      'original.SourceSha256 = parsed.SourceSha256;' in png_queue and
      'item.SourceSha256 = parsed.SourceSha256;' in png_journal and
      'entry.SourceSha256 is { Length: 64 }' in png_journal and
      'entry.SourceSha256, entry.PreviewSha256' in png_journal)
check('G4-03: JPEG and PNG readiness require journal-original and journal-candidate SHA',
      'e.SourceSha256, e.CandidateSha256' in jpeg_queue and
      'candidate.SourceSha256, candidate.PreviewSha256' in replacement_readiness and
      'candidate.SourceSha256, candidate.CandidateSha256' in replacement_readiness and
      'fidelity.OriginalSha256.Equals(saved.SourceSha256,' in replacement_readiness and
      'fidelity.CandidateSha256.Equals(saved.CandidateSha256,' in replacement_readiness and
      'G4-03: PNG原本のサイズ/日時を元に戻してもSHA変化で旧候補を拒否' in test and
      'G4-03: 過去の原本SHA無しの候補台帳は不合格・手動確認のみ' in test)

# G4-03: Copy-only production Core preparation must prove independent
# volume, exact original/candidate full bytes, preserved originals, and
# fail-closed foreign output. The source-changing Apply is still LOCKED.
check('G4-03: independent NTFS backup + same-volume stage are copy-only',
      'internal static PreparedCopies PrepareVerifiedCopies(' in replacement_readiness and
      'NativeFile.PinnedDirectories.Open(sourceRoot, [original])' in replacement_readiness and
      'NativeFile.PinnedDirectories.Open(backupRoot, [])' in replacement_readiness and
      'originalDrive.Name.Equals(backupDrive.Name' in replacement_readiness and
      'copy.Identity.VolumeSerial == originalSnapshot.Identity.VolumeSerial' in replacement_readiness and
      'stagedSnapshot.Identity.VolumeSerial != originalSnapshot.Identity.VolumeSerial' in replacement_readiness and
      'FileMode.CreateNew' in replacement_readiness and
      'output.Flush(flushToDisk: true)' in replacement_readiness and
      'Safety.ByteEqual(original, backedUp, ct)' in replacement_readiness and
      'Safety.ByteEqual(candidate, staged, ct)' in replacement_readiness and
      'var stagedFidelity = InspectCandidatePair(original, staged, ct);' in replacement_readiness and
      'ProductionReplacementAuthorized => false;' in replacement_readiness and
      'ActualFreedBytes => 0;' in replacement_readiness)
check('G4-03: abnormal prep and foreign output remain safe; never mutate source path',
      'Directory.EnumerateFileSystemEntries(stageRoot).Any()' in replacement_readiness and
      'Directory.EnumerateFileSystemEntries(backupRoot).Any()' in replacement_readiness and
      'G4-03: 原本と同じNTFSボリュームへの独立バックアップを拒否' in test and
      'G4-03: 別NTFSの原本独立バックアップと元NTFS候補stageを全バイト照合' in test and
      'G4-03: 他プロセスの原本排他writer中は独立バックアップ準備を拒否' in test and
      'G4-03: 出力先の既存/異物ファイルに遭遇すれば変更せず停止' in test and
      'G4-05: 元画像が既存ハードリンク共有なら独立stage準備を拒否' in test and
      'File.Replace(' not in replacement_readiness and
      'File.Delete(' not in replacement_readiness and
      'File.Move(' not in replacement_readiness)

# G4-03/04: the copy-only preparation checkpoint persists exact original
# NTFS identity, owner/DACL and timestamps, and can be independently reloaded.
# Neither its writer nor its reader is allowed to mutate the source image.
check('G4-03: durable copy-only receipt includes original NTFS ID/owner DACL/times',
      'private sealed class PreparationReceipt' in replacement_readiness and
      'public FileSnapshot? Original { get; set; }' in replacement_readiness and
      'public FileSnapshot? IndependentBackup { get; set; }' in replacement_readiness and
      'G4ReceiptName = ".chappy-g4-preparation.json"' in replacement_readiness and
      'System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(receipt)' in replacement_readiness and
      'WriteImageEvidence(receiptPath, recordBytes);' in replacement_readiness and
      'new FileStream(path, FileMode.CreateNew, FileAccess.Write,' in replacement_readiness and
      'output.Flush(flushToDisk: true);' in replacement_readiness)
check('G4-03/04: readonly journal replay fails closed for foreign, changed or orphan copies',
      'internal static PreparationInspection InspectPreparedCopies(' in replacement_readiness and
      'record.OriginalSha256.All(Uri.IsHexDigit)' in replacement_readiness and
      'Safety.SameSnapshot(old)' in replacement_readiness and
      'Safety.SameSnapshot(backup)' in replacement_readiness and
      'Safety.ByteEqual(original, backup.Path, ct)' in replacement_readiness and
      'Safety.ByteEqual(candidate, staged.Path, ct)' in replacement_readiness and
      'var equivalence = InspectCandidatePair(original, staged.Path, ct);' in replacement_readiness and
      'ReadyForCutover => false;' in replacement_readiness and
      'G4-03: 別OSプロセスから原本・候補・stage・別volumeバックアップの記録を再読' in test and
      'G4-03: レシート作成前のプロセス死はバックアップ/stageを保全し停止' in test and
      'G4-04: 独立バックアップ1B改ざんは日時を復元しても拒否' in test)

# G4-03: a verified stage is not cutover-ready until its NTFS owner,
# group, DACL, attributes, creation/modified times match the ORIGINAL.
# Security must be applied via a pinned stage/backup handle, not a mutable
# pathname; unsupported owner/group changes are rejected without mutation.
native = (root/'Core/NativeFile.cs').read_text(encoding='utf-8')
check('G4-03: exact original NTFS metadata is copied to same-volume stage',
      'internal static void CopyOwnerGroupDaclToNewFile(' in native and
      'SetSecurityInfo(handle, 1, DaclInfo,' in native and
      'IdentityFromHandle(handle)' in native and
      'SameSid(sourceOwner, targetOwner)' in native and
      'CopyOwnerGroupDaclToNewFile(original, target, before.Identity)' in replacement_readiness and
      'ApplyOriginalMetadataToNewCopy(original, staged, originalSnapshot);' in replacement_readiness and
      'not force source DACL onto the independent backup filesystem' in replacement_readiness and
      'File.SetCreationTimeUtc(target, source.CreationUtc);' in replacement_readiness and
      'File.SetLastWriteTimeUtc(target, source.ModifiedUtc);' in replacement_readiness and
      'File.SetAttributes(target, source.Attributes);' in replacement_readiness)
check('G4-03: stored/reloaded stage and backup refuse changed NTFS metadata',
      'staged.CreationUtc != old.CreationUtc' in replacement_readiness and
      '!NativeFile.SameOwnerGroupDacl(staged.SecurityDescriptor, old.SecurityDescriptor)' in replacement_readiness and
      'Safety.SameSnapshot(backup)' in replacement_readiness and
      'ConvertSecurityDescriptorToStringSecurityDescriptorW(' in native and
      'G4-03: stageの元owner/group/DACL/日時/属性一致、別volumeバックアップの独立IDと原権限レシート保全' in test and
      'G4-03: stageの画素全バイトが正しくても元日時相違を再起動後拒否' in test)

# G4-03/04: production cutover must stay impossible until verified
# release transaction. The shared implementation MUST use the
# existing NTFS pinned-ID no-replace primitive and a durable preparation
# receipt, with real process-death recovery and byte-exact original-ID proof.
check('G4-04: generated PNG cutover/restore use pinned IDs, not File.Replace',
      '#if CHAPPY_TESTING' in replacement_readiness and
      'internal static void ApplyPreparedImage(' in replacement_readiness and
      'internal static string RestorePreparedImage(' in replacement_readiness and
      'NativeFile.PinnedFile.Open(phase.Original,' in replacement_readiness and
      'NativeFile.PinnedFile.Open(phase.Stage,' in replacement_readiness and
      'sourcePinned.RenameNoReplace(phase.Rollback);' in replacement_readiness and
      'stagePinned.RenameNoReplace(phase.Original);' in replacement_readiness and
      'candidatePinned.RenameNoReplace(phase.Quarantine);' in replacement_readiness and
      'oldPinned.RenameNoReplace(phase.Original);' in replacement_readiness and
      'File.Replace(' not in replacement_readiness and
      'File.Move(' not in replacement_readiness)
check('G4-04: synthetic cutover can only touch generated exact PNG smoke files',
      'RequireGeneratedImageScope(' in replacement_readiness and
      'Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"' in replacement_readiness and
      'Guid.TryParseExact(name[' in replacement_readiness and
      '"r16-variants", "rgba-multi.png"' in replacement_readiness and
      '"ChappyStorageSmoke_G4Backup_"' in replacement_readiness and
      'G4-04: 実プロセス死の原本ID・候補ID状態を判定:' in test and
      'G4-04: 本物のプロセス死→原本ID/ACL/時刻/全バイト復元:' in test and
      'G4-04: 二重リカバリーは冪等かつ元画像未変更:' in test)
check('G4-04: release build refuses both image mutation entry points',
      'internal bool ProductionReplacementAuthorized => false;' in replacement_readiness and
      'internal bool ReadyForCutover => false;' in replacement_readiness and
      replacement_readiness.count(
          'RequireImageMutationScope(original, candidate, stageRoot, backupRoot);\n'
          '        using var lease = MutationLease.Acquire();') == 2 and
      '#else\n        throw new NotSupportedException("画像原本の置換・復元は' in replacement_readiness and
      'public const bool ProductionMutationEnabled = false;' in
        (root/'Core/CompletedGroupLedger.cs').read_text(encoding='utf-8'))

# G4-03: product WPF can prepare exactly one journal-backed PNG/JPG image
# as independent new copies only. The trusted source/candidate SHA comes
# from a FRESH read of the saved batch journal, never a user-supplied path,
# a stale UI row, or synthetic-only cutover. No source mutation UI allowed.
wpf_main_xaml = (root/'MainWindow.xaml').read_text(encoding='utf-8')
check('G4-03: production WPF one-image copy-only handler is wired',
      'CopyOnlyPreparationButton' in wpf_main_xaml and
      'CopyOnlyPreparation_Click' in wpf_main_xaml and
      'private async void CopyOnlyPreparation_Click(' in source and
      'CopyOnlyPreparationButton.IsEnabled = !busy;' in source and
      'ReplacementReadiness.PrepareOneVerifiedPreview(' in source and
      'MessageBoxButton.YesNo' in source and
      '実際に回復した容量は0 B' in source)
check('G4-03: WPF-to-Core fresh journal re-read binds one original/candidate SHA',
      'internal static PreparedCopies PrepareOneVerifiedPreview(' in replacement_readiness and
      'var combined = CombinedCapacityReport.Inspect(root, output, ct);' in replacement_readiness and
      'var review = Inspect(combined, root, output, independentBackupDirectory, ct);' in replacement_readiness and
      'matching.Length != 1' in replacement_readiness and
      'matching[0].SourceSha256 is not { Length: 64 }' in replacement_readiness and
      'matching[0].CandidateSha256 is not { Length: 64 }' in replacement_readiness and
      'return PrepareVerifiedCopies(original, accepted.VerifiedCandidatePath,' in replacement_readiness and
      'G4-03: WPF経路は保存済み原本SHAから独立コピーを作成、元ID/DACL不変・実回復0B' in test)
check('G4-03: WPF path refuses foreign source/volume/destination without source mutation',
      'G4-03: WPFから未承認・破損原本を指定してもコピーを作らず拒否' in test and
      'G4-03: WPF経路でも別NTFSバックアップがない場合は拒否' in test and
      'G4-03: WPF経路でもstage異物を上書きせず拒否' in test and
      'G4-03: 全拒否ケース後の原本ID・権限・内容は変わらない' in test and
      'CopyOnlyPreparationButton' in source and
      'ProductionReplacementAuthorized => false;' in replacement_readiness)

# G4-02: official pinned JPEG executable provenance must be enforced by
# the actual WinExe native execution path (not only by the CI test helper).
wpf_project = (root/'ChappyStorageCompressor.csproj').read_text(encoding='utf-8')
diagnostics_batch = (root/'RUN_DIAGNOSTICS.bat').read_text(encoding='utf-8')
pinned_profile = (root/'tests/R217PinnedJpegtranProfile.cs').read_text(encoding='utf-8')
jpeg_batch_source = (root/'Core/JpegBatchPreviewEngine.cs').read_text(encoding='utf-8')
workflow = (root/'.github/workflows/build-windows.yml').read_text(encoding='utf-8')
check('G4-02: compiled SHA pin is in WPF assembly metadata, not runtime config or writable sidecar',
      'Chappy.TrustedJpegtranSha256' in wpf_project and
      'ChappyTrustedJpegtranSha256' in wpf_project and
      'AssemblyMetadataAttribute' in wpf_project and
      'Assembly.GetCustomAttributes' not in jpeg_engine and
      '.GetCustomAttributes<AssemblyMetadataAttribute>()' in jpeg_engine)
check('G4-02: same tool identity and SHA verification immediately before native launch',
      'internal static FileStream OpenTrustedTool(' in jpeg_engine and
      'SHA256.HashData(held)' in jpeg_engine and
      'FileShare.Read, 64 * 1024' in jpeg_engine and
      'NativeFile.GetIdentity(tool)' in jpeg_engine and
      'NativeFile.HasNamedDataStreams(tool)' in jpeg_engine and
      'using var pinnedExecutable = OpenTrustedTool(tool, ct);' in jpeg_engine and
      'using var pinnedExecutable = JpegLosslessPreviewEngine.OpenTrustedTool(tool, ct);' in jpeg_batch_source and
      'using (var process = Process.Start(psi)' in jpeg_engine)
check('G4-02: official CI pinned source hashes compiled and exact exe bundled into release',
      'CHAPPY_R217_SIMD_SHA256=$trustedSha' in workflow and
      'CHAPPY_R217_PINNED_SHA256=$digest' in workflow and
      'Get-FileHash -LiteralPath $tool -Algorithm SHA256' in workflow and
      'libjpeg-turbo-LICENSE.md' in workflow and
      'ChappyTrustedJpegtranSha256=%CHAPPY_R217_SIMD_SHA256%' in diagnostics_batch and
      'dist\\win-x64\\jpegtran.exe' in diagnostics_batch)
check('G4-02: genuine CI JPEG engine rejects any untrusted same-name executable before filesystem outputs',
      'fakeRejected && fakeBatchRejected' in pinned_profile and
      'mismatchedRejected' in pinned_profile and
      'OpenTrustedTool(tool)' in pinned_profile and
      'CHAPPY_R217_JPEGTRAN_COMMIT' in jpeg_engine and
      '#if CHAPPY_TESTING' in jpeg_engine)

check('R1.5/B2: public product merge stays locked',
      'public static long Run(' in core and
      '#else\n        throw new NotSupportedException' in core and
      'private static long RunCore(' in core)
check('B2: normal Windows build contains only manifest-authorized internal Core',
      'internal static long RunPreparedGroup(' in core and
      'CompletedGroupLedger.AssertPreparedGroup(' in core and
      'CompletedGroupLedger.AssertPreparedAlias(' in core and
      'testOnlyPreparedGroupId: id' not in
      (root/'Core/CompletedGroupLedger.cs').read_text(encoding='utf-8'))
group_ledger = (root/'Core/CompletedGroupLedger.cs').read_text(encoding='utf-8')
check('B2: durable manifest writer is compiled for normal Windows Core',
      'internal static string PrepareVerifiedGroup(' in group_ledger and
      'string id = PrepareVerifiedGroup(root, group);' in group_ledger and
      'using var mutationLease = MutationLease.Acquire();' in group_ledger)
check('B2: shared internal full-group commit remains gated from WPF',
      'internal static string MergeVerifiedGroup(' in group_ledger and
      'internal static void ConfirmPreparedGroup(' in group_ledger and
      'DedupeEngine.RunPreparedGroup(root, group, id, null, ct)' in group_ledger and
      'ConfirmPreparedGroup(root, id);' in group_ledger and
      'return MergeVerifiedGroup(RequireSynthetic(input), group,' in group_ledger and
      'public static void ConfirmPreparedSynthetic(' in group_ledger and
      'public static bool ProductionMutationEnabled = true' not in group_ledger and
      'MergeVerifiedGroup(' not in source)
directory_core = (root/'Core/NativeFile.cs').read_text(encoding='utf-8')
parent_race = (root/'tests/R217PathSwapRaceTests.cs').read_text(encoding='utf-8')
check('B2 G2-03: production Core pins parent directory IDs without delete sharing',
      'internal sealed class PinnedDirectories : IDisposable' in directory_core and
      'BackupSemantics | OpenReparsePoint' in directory_core and
      'denyDelete ? ShareReadWrite : ShareReadWriteDelete' in directory_core and
      'internal void AssertUnchanged()' in directory_core and
      'using var sourceParents = NativeFile.PinnedDirectories.Open(' in core and
      'using var storeParents = NativeFile.PinnedDirectories.Open(' in core and
      'sourceParents.AssertUnchanged();' in core and
      'storeParents.AssertUnchanged();' in core and
      'using var parents = NativeFile.PinnedDirectories.Open(' in group_ledger and
      'parents.AssertUnchanged();' in group_ledger and
      'parent-dir-move' in parent_race and
      'VerifyPinnedParentAgainstExternalMove(parent, check, expectThrows);' in parent_race and
      'MergeVerifiedGroup(' not in source)
group_batch_tests = (root/'tests/R217GroupLedgerResumeTests.cs').read_text(encoding='utf-8')
check('B2 G2-04/05/06: guarded internal batch and capacity checkpoints',
      'internal static IReadOnlyList<string> MergeVerifiedGroups(' in group_ledger and
      'completed.Add(MergeVerifiedGroup(root, group, ct));' in group_ledger and
      'Journal.AssertCleanBeforeMerge();' in group_ledger and
      'CapacityGuard.AssertCanStart(root);\n                        NativeFile.NewHardLink(' in core and
      'storeParents.AssertUnchanged();\n                        // Free space' in core and
      'CapacityGuard.AssertCanStart(root);\n                        if (preparedGroupId' in core and
      'VerifyMidTransactionCapacityRefusal(parent, check, expectThrows);' in group_batch_tests and
      'VerifyKilledMultiGroupBatch(parent, check, expectThrows);' in group_batch_tests and
      'self.Kill();' in group_batch_tests and
      'CompletedGroupLedger.MergeVerifiedGroups(root, groups, token)' in source and
      'private bool _allowGeneratedOnlyMergeTest;' in source and
      'CompletedGroupLedger.RequireSynthetic(root)' in source and
      'string root = RequireSynthetic(input);' in group_ledger)
check('B2 G2-05/06: real-worker crash boundaries and isolated group failure',
      'FaultInjection.Hit("group-core-verified");' in group_ledger and
      'FaultInjection.Hit("group-completed");' in group_ledger and
      'private static readonly string[] BatchCrashCheckpoints' in group_batch_tests and
      all(x in group_batch_tests for x in [
          '"journal-recorded", "backup-renamed", "new-link-created"',
          '"pre-backup-delete", "backup-deleted"',
          '"group-core-verified", "group-completed"'
      ]) and
      'foreach (string checkpoint in BatchCrashCheckpoints)' in group_batch_tests and
      'KillWorker(root, "merge-batch:" + checkpoint, signal)' in group_batch_tests and
      'VerifyBatchSourceSwapIsolation(parent, check, expectThrows);' in group_batch_tests and
      'actualCoreKillCheckpointCount = BatchCrashCheckpoints.Length' in group_batch_tests and
      'CompletedGroupLedger.MergeVerifiedGroups(root, groups, token)' in source and
      'private bool _allowGeneratedOnlyMergeTest;' in source and
      'CompletedGroupLedger.RequireSynthetic(root)' in source and
      'string root = RequireSynthetic(input);' in group_ledger)
vhdx_probe = (root/'bootstrap/Part29IsolatedVhdxProbe.ps1').read_text(encoding='utf-8')
workflow = (root/'.github/workflows/build-windows.yml').read_text(encoding='utf-8')
check('B2 G2-06: real Core per-file-ID allocation and separate VHDX free-space reconciliation',
      'GetCompressedFileSizeW' in group_batch_tests and
      'UniqueAllocated(sourceNames.Concat(masters)' in group_batch_tests and
      'baselineAllocation.Bytes - allocated.Bytes' in group_batch_tests and
      'actualCorePartialAllocationMatrix = allocationMatrix' in group_batch_tests and
      'private static IReadOnlyList<PartialAllocationEvidence> VerifyKilledMultiGroupBatch(' in group_batch_tests and
      'function UniqueAllocation([string[]]$paths)' in vhdx_probe and
      'generatedOriginalCount = 7' in vhdx_probe and
      'physicalUniqueIdAllocatedDeltaBytes = $physicalPartialDelta' in vhdx_probe and
      'unfinishedGroupPhysicalSavingsNotCreditedToReceipt' in vhdx_probe and
      'realCoreOnMountedJunction = $false' in vhdx_probe and
      '$r.actualCoreDistinctNtfsIdAllocationMeasuredAtEveryKill -ne $true' in workflow and
      '$r.groupedPartialAllocation.physicalUniqueIdAllocatedDeltaBytes -ne 8388608' in workflow and
      'CompletedGroupLedger.MergeVerifiedGroups(root, groups, token)' in source and
      'private bool _allowGeneratedOnlyMergeTest;' in source and
      'CompletedGroupLedger.RequireSynthetic(root)' in source and
      'string root = RequireSynthetic(input);' in group_ledger)
selection_core = (root/'Core/DedupeFirstWorkflowPlan.cs').read_text(encoding='utf-8')
recovery_view = (root/'Core/RecoveryEvidenceOverview.cs').read_text(encoding='utf-8')
window_xaml = (root/'MainWindow.xaml').read_text(encoding='utf-8')
ui_test = (root/'tests/UiInteractionSmoke.cs').read_text(encoding='utf-8')
check('B2 G2-02: original per-file IDs are exposed only via verified durable group manifest',
      'public static IReadOnlyList<OriginalEvidence> InspectOriginals(' in group_ledger and
      'if (!Verify(manifest, p) ||' in group_ledger and
      'OriginalFileIndex' in group_ledger and
      'OwnerDaclFingerprint' in group_ledger and
      'CompletedGroupLedger.InspectOriginals(root, ct)' in recovery_view and
      'グループ元ファイルの個別履歴' in recovery_view and
      'CompletedGroupLedger.InspectOriginals(root, token)' in source and
      'groupOriginalRecordsReadOnlyVerified = ' in group_batch_tests)
check('B2 G2-07: default WPF selected risk review is read-only; generated fixture uses shared guarded Core',
      'SelectedMergeReviewButton' in window_xaml and
      'private async void SelectedMergeReview_Click(' in source and
      'DedupeFirstWorkflowPlan.InspectSelected(root, groups, token)' in source and
      'private Func<string, bool> _confirmSelectedMergeRisk;' in source and
      'public static SelectedReview InspectSelected(' in selection_core and
      'Safety.SameSnapshot(file)' in selection_core and
      'Safety.ByteEqual(full, group.Representative.Path, ct)' in selection_core and
      'ProductionMergeAuthorized => false' in selection_core and
      'b2SelectedRiskReviewRoutedClickVerified = ' in ui_test and
      'private bool _allowGeneratedOnlyMergeTest;' in source and
      'CompletedGroupLedger.RequireSynthetic(root)' in source and
      'CompletedGroupLedger.MergeVerifiedGroups(root, groups, token)' in source and
      'string root = RequireSynthetic(input);' in group_ledger and
      'root = CompletedGroupLedger.RequireSynthetic(root);' in core and
      'b2GeneratedOnlySelectedMergeRoutedTwoConsentAndPersistedCore = ' in ui_test and
      'b2NonSentinelWpfAndInternalCoreStillLocked =' in ui_test and
      'b2GeneratedSelectionNoConsentKeepsOriginalNtfsIds = ' in ui_test)
recovery_window = (root/'RecoveryWindow.xaml.cs').read_text(encoding='utf-8')
recovery_xaml = (root/'RecoveryWindow.xaml').read_text(encoding='utf-8')
check('G3-01: restart Undo reidentification reads only authenticated persisted group and cannot invoke Restore',
      'public static IReadOnlyList<UndoCandidate> InspectUndoCandidates(' in group_ledger and
      'p.ActiveStage is not null || !Verify(x, p)' in group_ledger and
      'x.Aliases.Skip(p.Detached.Count)' in group_ledger and
      'CapacityGuard.TargetReserve' in group_ledger and
      'Journal.UnresolvedRecordFiles().Count == 0' in group_ledger and
      'IndependentUndoCrashJournal.Inspect(root, ct)' in group_ledger and
      'ProductionUndoAuthorized => false' in group_ledger and
      'review-undo:' in group_batch_tests and
      'VerifyRestartUndoReview(root, id, before, 0, check);' in group_batch_tests and
      'VerifyRestartUndoReview(root, id, before, 1, check);' in group_batch_tests and
      'UndoReviewButton' in recovery_xaml and
      'CompletedGroupLedger.InspectUndoCandidates(_selectedRoot)' in recovery_window and
      'IndependentUndoEngine.Restore(' not in recovery_window and
      'g3ReadOnlyUndoPreviewForgedHistoryRowRefused = ' in ui_test and
      '$r.g3RestartedProcessOriginalGroupFullIdProof -ne $true' in workflow)
undo_engine = (root/'Core/IndependentUndoEngine.cs').read_text(encoding='utf-8')
capacity_guard = (root/'Core/CapacityGuard.cs').read_text(encoding='utf-8')
check('G3: original independent File.Replace core is shared internally but never public or WPF-enabled',
      'internal static Result RestoreCore(' in undo_engine and
      'public static Result Restore(' in undo_engine and
      '#if !CHAPPY_TESTING\n        throw new NotSupportedException(' in undo_engine and
      'return RestoreCore(testRoot, originalIndependent' in undo_engine and
      'IndependentUndoEngine.RestoreCore(root, x.Aliases[i]' in group_ledger and
      'File.Replace(stage, alias, null);' in undo_engine and
      'SetFileSecurity(alias, ownerGroupDacl,' in undo_engine and
      'Group merge/Undo is generated NTFS testing only' in group_ledger and
      'Group merge/Undo is generated NTFS testing only' in group_ledger and
      'IndependentUndoEngine.RestoreCore(' not in source and
      'IndependentUndoEngine.Restore(' not in source)
check('G3: full-group and per-file preflight plus pre-cutover capacity are checked',
      'internal static void AssertCanRestore(' in capacity_guard and
      'CapacityGuard.AssertCanRestore(root, allRemainingBytes);' in group_ledger and
      'CapacityGuard.AssertCanRestore(root, originalIndependent.Length);' in undo_engine and
      'CapacityGuard.AssertCanRestore(root, 0, stageAlreadyAllocated: true);' in undo_engine and
      'VerifyUndoCapacityRefusals(parent, check, expectThrows);' in group_batch_tests and
      'g3IndependentWriteAfterUndoOtherOriginalsPreserved = unaffected' in group_batch_tests and
      '$r.g3ExplicitStageRecoveryAfterCapacityLossVerified -ne $true' in workflow)
crash_undo_tests = (root/'tests/R217GroupIntraAliasCrashTests.cs').read_text(encoding='utf-8')
check('G3-03/06: one production-compiled but synthetic-guarded group Undo replay and cross-ledger authorization',
      'internal static int ResumeUndoCore(' in group_ledger and
      'ResumeUndoCore(input, id, evidence, CancellationToken.None,' in group_ledger and
      'string root = RequireSynthetic(input);' in group_ledger and
      'IndependentUndoEngine.RestoreMetadataAfterCrashCore(' in group_ledger and
      'internal static void RestoreMetadataAfterCrashCore(' in undo_engine and
      'internal static void AssertUndoReceiptDirectory(' in undo_engine and
      'private static void AssertUndoCrossEvidence(' in group_ledger and
      'CompletedMergeLedger.Inspect(root, ct)' in group_ledger and
      'IndependentUndoCrashJournal.Inspect(root, ct)' in group_ledger and
      'CollisionEvidenceReview.Inspect(root, ct)' in group_ledger and
      'AssertUndoCrossEvidence(root, x, latest, evidence, ct);' in group_ledger and
      'g3CrossLedgerFiveForeignEvidenceFamiliesRefused = true' in group_batch_tests and
      'modifiedSavedUndoSidecarRefused = true' in crash_undo_tests and
      '$r.modifiedSavedUndoSidecarRefused -ne $true' in workflow and
      'ResumeUndoCore(' not in source and
      'RestoreMetadataAfterCrashCore(' not in source and
      'UndoReviewButton' in recovery_xaml)
collision_ledger = (root/'Core/CollisionRecoveryLedger.cs').read_text(encoding='utf-8')
collision_plan = (root/'Core/CollisionCollectionPlan.cs').read_text(encoding='utf-8')
check('G3-05: independently restored Undo aliases enter preexisting no-overwrite collision receipt flow',
      'internal static string BeginRestoredUndoExportSynthetic(' in collision_ledger and
      'CompletedGroupLedger.InspectOriginals(root)' in collision_ledger and
      'x.State == "VerifiedRestored" && !x.Representative' in collision_ledger and
      'CollisionCollectionPlan.Inspect(output,' in collision_ledger and
      'return BeginSynthetic(root, plan, sources);' in collision_ledger and
      'FileMode.CreateNew' in collision_ledger and
      '同名ファイル一覧.txt' in collision_plan and
      'VerifyRestoredUndoCollisionExport(parent, check, expectThrows);' in group_batch_tests and
      'g3CollisionNestedNumberedUtf8MemoAndRestartVerified = true' in group_batch_tests and
      '$r.g3PostUndoExistingCollisionLedgerExportVerified -ne $true' in workflow and
      'ProductionMutationEnabled = false' in collision_ledger)
check('G3-05: same collision writer is compiled in normal Windows core but inaccessible to public user files',
      '#if CHAPPY_TESTING' not in collision_ledger and
      'internal static string BeginSynthetic(' in collision_ledger and
      'internal static Status InspectSynthetic(' in collision_ledger and
      'internal static Status ResumeSynthetic(' in collision_ledger and
      'internal static string BeginRestoredUndoExportSynthetic(' in collision_ledger and
      'string root = RequireOwnedRoot(testRoot);' in collision_ledger and
      'Part35 only accepts generated sentinel NTFS root' in collision_ledger and
      'FileMode.CreateNew' in collision_ledger and
      'g3NormalCompiledCollisionExportAfterRoutedWpfUndo =' in ui_test and
      'ProductionMutationEnabled = false' in collision_ledger and
      '$r.g3NormalCompiledCollisionExportAfterRoutedWpfUndo -ne $true' in workflow)
check('G3-07: real routed WPF Undo requires explicit generated-only opt-in and cannot act on user media',
      'UndoExecuteButton' in recovery_xaml and
      'private bool _allowGeneratedOnlyUndoTest;' in recovery_window and
      'root.Contains("ChappyStorageSmoke_"' in recovery_window and
      'CompletedGroupLedger.InspectUndoCandidates(root)' in recovery_window and
      'CompletedGroupLedger.ResumeUndoCore(root, operationId,' in recovery_window and
      'private async void UndoExecute_Click(' in recovery_window and
      'g3GeneratedOnlyRoutedWpfCoreUndoSuccess = realWpfUndo' in ui_test and
      'g3GeneratedOnlyRoutedWpfUndoConsentDeniedWithoutMutation = undoConsentRejected' in ui_test and
      '$r.g3GeneratedOnlyRoutedWpfCoreUndoSuccess -ne $true' in workflow and
      'IndependentUndoEngine.Restore(' not in recovery_window and
      'MergeVerifiedGroup(' not in recovery_window)
check('B2: synthetic operation wrappers and Undo remain compile-gated',
      'public static string MergeSynthetic(' in group_ledger and
      '#if CHAPPY_TESTING\n    /// <summary>\n    /// Explicit TEST-ONLY confirmation' in group_ledger and
      '#if CHAPPY_TESTING\n    public static string MergeSynthetic(' in group_ledger and
      'public static int ResumeUndoSynthetic(' in group_ledger)
check('R1.5: PNG test coverage included', 'TestR15PngPreview(root)' in test and 'WritePngChunk' in test)

print(f'\nStatic checks: {len(errors)} failure(s). This does NOT test C# compilation or runtime safety.')
# R1.6 specific static contracts (not a substitute for running C#).
batch = (root/'Core/PngBatchPreviewEngine.cs').read_text(encoding='utf-8')
for key in ['PngLosslessOptimizer.CreatePreview', 'LoadProgress(progressFile, plan)',
            'AppendProgress(progressFile, entry)', 'Safety.ComputeSha256(entry.PreviewPath, ct)',
            'FileShare.None', 'Deferred', 'FileAttributes.ReparsePoint',
            'Path.GetRelativePath(input, file)']:
    check('R1.6 batch contract: '+key, key in batch)
check('R1.6 source not replaced', 'File.Delete(absolute)' not in batch and 'File.Move(absolute,' not in batch)
check('R1.6 GUI event wired', 'PngBatch_Click' in source and 'PngBatchButton.IsEnabled' in source)
check('R1.6 Windows test cases', 'TestR16PngAndBatch(root)' in test and 'CreateR16Png' in test)
check('R1.6 independent reference script', (root/'tools/r16_png_reference_check.py').is_file())

# R1.7 source-contract checks. Not C# runtime checks.
repair = (root/'Core/PngBatchJournalTools.cs').read_text(encoding='utf-8')
jpeg = (root/'Core/JpegInspector.cs').read_text(encoding='utf-8')
for word in ['RepairTail(', 'InspectTail(', 'ReadAudit(', 'ExportCsv(',
             'File.Copy(state.LogPath, backup, overwrite: false)',
             'File.Move(tmp, state.LogPath, overwrite: true)',
             'new("PartialTail"', 'new("MissingNewline"']:
    check('R1.7 journal feature: '+word, word in repair)
check('R1.7 original source untouched', 'File.Delete(source)' not in repair and
      'File.Move(source,' not in repair)
check('R1.7 GUI buttons', 'PngAudit_Click' in source and 'JpegInspect_Click' in source)
check('R1.7 JPEG header only', 'public static Header Inspect' in jpeg and 'recompress' in jpeg)
check('R1.7 tests', 'TestR17JournalAndJpeg(root)' in test)
# R1.8 contracts: source must never be replaced by JPG preview or orphan audit.
marker = (root/'Core/JpegMarkerAudit.cs').read_text(encoding='utf-8')
optimizer = (root/'Core/JpegLosslessPreviewEngine.cs').read_text(encoding='utf-8')
orphan = (root/'Core/PngOrphanAudit.cs').read_text(encoding='utf-8')
for token in ['ProtectedMarkerSha256', 'ScanCount', 'EOI', 'sawEoi']:
    check('R1.8 JPEG audit: '+token, token in marker)
for token in ['-optimize', 'EquivalentDecodedPixels(', 'sourceLock', 'Safety.ComputeSha256(source, ct)',
              'File.Delete(candidate)', 'FileShare.Read', 'FileShare.None']:
    # FileShare.None is used in the PNG queue; JPEG output is written by the external process.
    if token == 'FileShare.None': continue
    check('R1.8 jpeg candidate: '+token, token in optimizer)
for token in ['Untracked', 'ExportCsv(', 'referenced.Add(', 'File.Exists(planPath)']:
    check('R1.8 orphan audit: '+token, token in orphan)
check('R1.8 no orphan deletion', 'File.Delete(path)' not in orphan and 'File.Move(path,' not in orphan)
check('R1.8 jpeg source never deleted', 'File.Delete(source)' not in optimizer and 'File.Move(source,' not in optimizer)
check('R1.8 GUI', 'JpegPreview_Click' in source and 'PngOrphan_Click' in source)
check('R1.8 tests', 'TestR18JpegAndOrphans(root)' in test)
print(f'R1.8 checks added. Total failures: {len(errors)}')

# R1.9 safety contracts (structural only; Windows build still required).
r19 = (root/'Core/JpegBatchPreviewEngine.cs').read_text(encoding='utf-8')
for token in ['JpegLosslessPreviewEngine.CreatePreview', 'ReplayJournal(', 'Append(',
              'ValidateExistingCandidate(', 'Safety.ComputeSha256(original, ct)',
              'SourceSha256 = sourceBefore', 'FileShare.None',
              'File.Move(tmp, path)', 'VerifiedSavingBytes', 'EscapeCsv(',
              'File.Move(temp, csv)', 'MaxJournalBytes']:
    check('R1.9 JPEG batch: '+token, token in r19)
check('R1.9 never changes originals',
      'File.Delete(original)' not in r19 and 'File.Move(original,' not in r19 and
      'File.WriteAllBytes(original' not in r19)
check('R1.9 JPEG GUI', 'JpegBatch_Click' in source and
      'JpegBatchReport_Click' in source and 'JpegBatchButton.IsEnabled' in source)
check('R1.9 Windows queue tests', 'TestR19JpegQueue(root)' in test and
      'ExpectThrows(() => JpegBatchPreviewEngine.Run(' in test)
print(f'R1.9 total failures: {len(errors)}')
# R2.0 contract checks. Structural checks only; Windows compilation remains required.
r20_journal = (root/'Core/JpegBatchJournalTools.cs').read_text(encoding='utf-8')
r20_report = (root/'Core/CombinedCapacityReport.cs').read_text(encoding='utf-8')
r20_batch = (root/'Core/JpegBatchPreviewEngine.cs').read_text(encoding='utf-8')
for token in ['InspectTail(', 'RepairTail(', 'File.Copy(original, backup, overwrite: false)',
              'File.Move(temp, original, overwrite: true)', 'PartialTail', 'MissingNewline',
              'ReplayJournalBytes(', 'ValidatePlan(']:
    check('R2.0 JPG tail guard: '+token, token in r20_journal)
check('R2.0 no JPEG media mutation in repair',
      'File.Delete(source)' not in r20_journal and 'File.Move(source,' not in r20_journal)
for token in ['PngBatchJournalTools.ReadAudit(', 'JpegBatchPreviewEngine.Inspect(',
              'ActualFreedBytes', 'File.Move(temp, path)', 'File.Exists(path)',
              'EstimatedSavingBytes', 'CSV', 'Csv(']:
    check('R2.0 cross-format report: '+token, token in r20_report)
check('R2.0 actual recovered bytes always 0 for preview release',
      'new Summary(pngCount, jpegCount, count, review, saving, 0, rows)' in r20_report)
check('R2.0 validate every log row',
      'A subsequent valid record must' in r20_batch and 'logged.Status is not' in r20_batch)
check('R2.0 GUI wiring', all(token in source for token in
      ['CombinedReport_Click(', 'JpegRepair_Click(', 'CombinedReportButton.IsEnabled',
       'JpegRepairButton.IsEnabled']))
check('R2.0 runtime tests included', 'TestR20CombinedAndJpegRepair(root)' in test and
      'TestR20CombinedAndJpegRepair(string root)' in test)
print(f'R2.0 structural checks total failures: {len(errors)}')

# R2.1 structural contracts (not a compiler or runtime substitute).
r21 = (root/'Core/CombinedSavingsPlanner.cs').read_text(encoding='utf-8')
for token in ['PreviewOnlyBytes', 'DedupeOnlyBytes', 'DoubleCountAvoidedBytes',
              'Safety.SameSnapshot(file)', 'Safety.ComputeSha256(full, ct)',
              'group.BlockReason(allowTimestampDifference)',
              'return new Estimate(', 'ActualFreedBytes', 'Inside(full, root)']:
    check('R2.1 non-overlap planner: '+token, token in r21)
check('R2.1 planner does not delete/replace originals',
      'File.Delete(' not in r21 and 'File.Move(' not in r21 and 'File.WriteAll' not in r21)
check('R2.1 regression tests included', 'TestR21NonOverlappingEstimate(root)' in test)
check('R2.1 GUI estimate', 'CombinedSavingsPlanner.Analyze(' in source and
      'combinedEstimate.DoubleCountAvoidedBytes' in source)
check('R2.1 Windows diagnostic script', (root/'RUN_DIAGNOSTICS.bat').is_file())
print(f'R2.1 structural checks total failures: {len(errors)}')

# R2.2: read-only capacity preflight (structural contracts, NOT compilation).
r22 = (root/'Core/ReplacementReadiness.cs').read_text(encoding='utf-8')
for token in ['EvaluateCapacity(', 'AvailableFreeSpace', 'Safety.Capture(path)',
              'Identity.LinkCount != 1', 'CheckNoReparse(path)',
              'MeetsSpaceRequirements', 'ActualFreedBytes => 0',
              'MinimumReserveBytes', 'differentVolume', 'SourceBytes']:
    check('R2.2 preflight guard: '+token, token in r22)
for destructive in ['File.Move(', 'File.Delete(', 'File.Replace(', 'File.Copy(', 'File.WriteAllBytes(',
                    'Directory.CreateDirectory(', 'NativeFile.NewHardLink(']:
    check('R2.2 preflight never mutates: '+destructive, destructive not in r22)
# Read-only inspection and copy-only preparation are distinct methods.
# Hold the original/candidate under FileShare.Read, but allow the G4-03
# preparation helper to CreateNew files only in isolated stage/backup roots.
# Still ban writes in the read-only inspection methods themselves.
read_only_r22 = r22.split('    // G4-03: the preparation phase is intentionally COPY-ONLY:')[0]
preparation_r22 = r22.split('    // G4-03: the preparation phase is intentionally COPY-ONLY:')[1]
check('R2.2/G4 read-only source and candidate FileStream locks',
      'new FileStream(original, FileMode.Open,' in read_only_r22 and
      'new FileStream(candidate, FileMode.Open,' in read_only_r22 and
      'FileAccess.Read, FileShare.Read' in read_only_r22 and
      not any(x in read_only_r22 for x in ['FileAccess.Write', 'FileAccess.ReadWrite',
           'FileMode.Create', 'FileMode.Append', 'FileMode.OpenOrCreate',
           'FileMode.Truncate']))
check('G4-03: only CreateNew on stage/backup, never mutate source',
      'new FileStream(destination, FileMode.CreateNew,' in preparation_r22 and
      'FileAccess.Write, FileShare.None' in preparation_r22 and
      'FileMode.Open,\n            FileAccess.Read, FileShare.Read' in preparation_r22 and
      not any(x in preparation_r22 for x in [
           'FileMode.Create,', 'FileMode.OpenOrCreate', 'FileMode.Truncate',
           'FileMode.Append', 'FileAccess.ReadWrite', 'File.Replace(',
           'File.Delete(', 'File.Move(', 'File.Copy(']))
check('R2.2 GUI event wired', 'ReplacementReadiness_Click' in source and
      'ReplacementReadinessButton.IsEnabled' in source and
      'ReplacementReadiness_Click' in (root/'MainWindow.xaml').read_text(encoding='utf-8'))
check('R2.2 synthetic tests included', 'TestR22Readiness();' in test)
check('R2.2 production file replacement and public merge remain gated',
      'MergeButton.IsEnabled = false' in source and
      'File.Move(candidate, source)' not in source and
      'public static long Run(' in core and
      '#else\n        throw new NotSupportedException' in core and
      'internal static long RunPreparedGroup(' in core)
print(f'R2.2 source inspections: {len(errors)} failure(s) overall; Windows build remains REQUIRED')
if errors: sys.exit(1)
