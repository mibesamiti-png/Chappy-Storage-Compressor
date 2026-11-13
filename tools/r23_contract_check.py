#!/usr/bin/env python3
"""Check source gating and regression wiring; not compilation or runtime tests."""
from pathlib import Path
root = Path(__file__).resolve().parents[1]
core = (root / 'Core/SyntheticReplacementExperiment.cs').read_text(encoding='utf-8')
tests = (root / 'tests/Program.cs').read_text(encoding='utf-8')
app = (root / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
csproj = (root / 'ChappyStorageCompressor.csproj').read_text(encoding='utf-8')
testproj = (root / 'tests/ChappyStorageCompressor.SmokeTests.csproj').read_text(encoding='utf-8')
checks = {
  'synthetic experiment compiled only in test assembly': core.strip().startswith('#if CHAPPY_TESTING') and core.strip().endswith('#endif'),
  'production project does not define CHAPPY_TESTING': 'CHAPPY_TESTING' not in csproj,
  'test project defines CHAPPY_TESTING': 'CHAPPY_TESTING' in testproj,
  'test project links Core sources': r'..\Core\*.cs' in testproj,
  'GUI has no synthetic replacement entry point': 'SyntheticReplacementExperiment' not in app,
  'test-only isolation sentinel required': 'ValidateIsolatedDirectory' in core and 'SentinelName' in core,
  'unrelated root paths rejected': 'ChappyStorageSmoke_' in core and 'Path.GetTempPath()' in core,
  'reject shared hardlinks': 'Identity.LinkCount != 1' in core,
  'reject changed source snapshots': 'Safety.SameSnapshot' in core,
  'refuse an active writer': 'FileShare.None' in core and 'r23-active-writer' in tests,
  'verified backup before replacement': 'VerifyFile(external, oldHash, oldBytes);' in core,
  'journal flushed to physical media': 'stream.Flush(true)' in core,
  'Windows synthetic File.Replace present': 'File.Replace(stage, original, rollback)' in core,
  'rollback requires verified external backup': 'Record record = ReadRecord(dir);' in core and 'VerifyFile(At(directory, ExternalName)' in core,
  'manual review for mismatched paths': 'Ambiguous cutover state' in core,
  'no silent commit': 'NO commit / delete-original operation' in core,
  'six fault injection points covered': all(n in tests for n in ['r23-after-external-copy','r23-after-stage-copy','r23-after-journal','r23-after-replace','r23-after-quarantine','r23-after-restore']),
  'test case invoked by smoke entrypoint': 'TestR23SyntheticReplacement(root);' in tests,
  'negative tamper tests included': 'r23-tampered' in tests,
  'negative hardlink test included': 'r23-hardlinked' in tests,
}
for name, good in checks.items():
    print(('PASS' if good else 'FAIL'), name)
if not all(checks.values()):
    raise SystemExit('R2.3 contract checks failed')
print(f'R2.3 structural contract: {len(checks)}/{len(checks)} PASS (NOT a C# build)')
