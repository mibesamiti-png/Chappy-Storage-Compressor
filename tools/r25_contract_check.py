#!/usr/bin/env python3
"""R2.5 source-contract checks. Only verifies source wiring, NOT a C# build."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
core = (root/'Core/SyntheticReplacementExperiment.cs').read_text(encoding='utf-8')
tests = (root/'tests/Program.cs').read_text(encoding='utf-8')
prod = (root/'ChappyStorageCompressor.csproj').read_text(encoding='utf-8')
smoke = (root/'tests/ChappyStorageCompressor.SmokeTests.csproj').read_text(encoding='utf-8')
app = (root/'MainWindow.xaml.cs').read_text(encoding='utf-8')
checks = {
    'test-only compiler gate intact': core.startswith('#if CHAPPY_TESTING') and core.rstrip().endswith('#endif'),
    'app cannot invoke replacement': 'SyntheticReplacementExperiment' not in app,
    'production excludes test symbol': 'CHAPPY_TESTING' not in prod,
    'test target explicitly defines symbol': ';CHAPPY_TESTING' in smoke,
    'production Windows targeting enabled': '<EnableWindowsTargeting>true</EnableWindowsTargeting>' in prod,
    'smoke Windows targeting enabled': '<EnableWindowsTargeting>true</EnableWindowsTargeting>' in smoke,
    'artifact identity checker added': 'private static void RequireIndependentPlainFile' in core,
    'artifact path must be ordinary file': 'Directory.Exists(path) || !File.Exists(path)' in core,
    'file attributes checked': 'Safety.EligibleAttributes(attributes)' in core,
    'hardlink count verified': 'NativeFile.GetIdentity(path).LinkCount != 1' in core,
    'alternate streams checked': 'NativeFile.HasNamedDataStreams(path)' in core,
    'backup hash requires plain file': core.index('RequireIndependentPlainFile(path);', core.index('private static void VerifyFile')) < core.index('Hash(path) != expectedHash', core.index('private static void VerifyFile')),
    'existing rollback path checks ordinary file': 'private static string? ExistingHash' in core and 'RequireIndependentPlainFile(path);\n        return Hash(path);' in core,
    'journal size bounded': 'journalBytes is <= 0 or > 8192' in core,
    'active journal checked for shared links': 'RequireIndependentPlainFile(file);' in core,
    'prejournal temporary record checked': 'RequireIndependentPlainFile(tempJournal);' in core,
    'rollback quarantine reverified before deleting': 'VerifyFile(quarantine, record.CandidateSha, record.CandidateBytes);\n            File.Delete(quarantine);' in core,
    'postjournal external reverified': 'VerifyFile(At(dir, ExternalName), record.OriginalSha, record.OriginalBytes);\n        File.Delete(At(dir, ExternalName));' in core,
    'negative shared backup fixture': 'r25-shared-external' in tests,
    'negative shared rollback fixture': 'r25-shared-rollback' in tests,
    'negative shared journal fixture': 'r25-shared-journal' in tests,
    'negative shared temporary journal fixture': 'r25-shared-temporary-journal' in tests,
    'oversized journal fixture': 'r25-oversized-journal' in tests,
    'named stream stage fixture': 'r25-stage-ads' in tests,
    'smoke runner actually calls tests': 'TestR25ArtifactIsolation(root);' in tests,
}
for file in (root/'ChappyStorageCompressor.csproj', root/'tests/ChappyStorageCompressor.SmokeTests.csproj'):
    ET.parse(file)
for desc, ok in checks.items():
    print(('PASS' if ok else 'FAIL'), desc)
if not all(checks.values()):
    raise SystemExit(1)
print(f'R2.5 source contract: {len(checks)}/{len(checks)} PASS; NOT a C# build or NTFS test')
