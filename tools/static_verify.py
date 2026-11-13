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
for guard in ['ValidateContext(p)', 'RequireFresh(p, expected)',
              'IndependentBackupMatches(p, ct)', 'NativeFile.SameFile(p.OriginalPath, p.MasterPath)',
              'File.Move(p.BackupPath, p.OriginalPath)', 'File.Delete(p.OriginalPath)']:
    check('recovery guard: '+guard, guard in recovery)
check('no automatic backup deletion in recovery', 'File.Delete(p.BackupPath)' not in recovery)
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
check('R1.4: journal operations serialized', 'MutationLease.Acquire()' in (root/'Core/Journal.cs').read_text(encoding='utf-8'))
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
check('R1.5: production merge hard-gated', '#if !CHAPPY_TESTING' in core and 'throw new NotSupportedException' in core)
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
                    'FileStream(', 'Directory.CreateDirectory(', 'NativeFile.NewHardLink(']:
    check('R2.2 preflight never mutates: '+destructive, destructive not in r22)
check('R2.2 GUI event wired', 'ReplacementReadiness_Click' in source and
      'ReplacementReadinessButton.IsEnabled' in source and
      'ReplacementReadiness_Click' in (root/'MainWindow.xaml').read_text(encoding='utf-8'))
check('R2.2 synthetic tests included', 'TestR22Readiness();' in test)
check('R2.2 production file replacement remains gated',
      'MergeButton.IsEnabled = false' in source and
      'File.Move(candidate, source)' not in source and
      '#if !CHAPPY_TESTING' in core)
print(f'R2.2 source inspections: {len(errors)} failure(s) overall; Windows build remains REQUIRED')
if errors: sys.exit(1)
