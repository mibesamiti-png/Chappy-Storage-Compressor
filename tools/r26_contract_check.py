#!/usr/bin/env python3
"""R2.6 source contract checks. Structural only; NOT a C# compiler or NTFS test."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[1]
core = (root / 'Core/SyntheticReplacementExperiment.cs').read_text(encoding='utf-8')
test = (root / 'tests/Program.cs').read_text(encoding='utf-8')
app = (root / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
prod = (root / 'ChappyStorageCompressor.csproj').read_text(encoding='utf-8')
tests_project = (root / 'tests/ChappyStorageCompressor.SmokeTests.csproj').read_text(encoding='utf-8')
runner = (root / 'RUN_DIAGNOSTICS.bat').read_text(encoding='utf-8')
workflow = (root / '.github/workflows/build-windows.yml').read_text(encoding='utf-8')

ET.fromstring(prod)
ET.fromstring(tests_project)
verify_start = core.index('    private static void VerifyFile(')
verify_end = core.index('    private static string? ExistingHash(', verify_start)
verify = core[verify_start:verify_end]
cutover_start = core.index('        FaultInjection.Hit("r24-before-final-preflight");')
cutover_end = core.index('        File.Replace(stage, original, rollback);', cutover_start)
cutover = core[cutover_start:cutover_end]

checks = {
    'experiment remains test-only': core.startswith('#if CHAPPY_TESTING') and core.rstrip().endswith('#endif'),
    'normal WPF app has no mutation experiment entry': 'SyntheticReplacementExperiment' not in app,
    'production project has no CHAPPY_TESTING define': 'CHAPPY_TESTING' not in prod,
    'synthetic project retains test symbol': 'CHAPPY_TESTING' in tests_project,
    'source version updated': '<Version>0.2.6</Version>' in prod,
    'before-read file snapshot': 'FileSnapshot before = Safety.Capture(path);' in verify,
    'length checked against pre-read snapshot': 'before.Length != expectedBytes' in verify,
    'sha-256 checked against expectation': 'Hash(path) != expectedHash' in verify,
    'post-read fault checkpoint': 'FaultInjection.Hit("r26-after-artifact-hash");' in verify,
    'post-read plain-file safety check': verify.count('RequireIndependentPlainFile(path);') == 2,
    'post-read identity+metadata recheck': 'Safety.SameSnapshot(before)' in verify,
    'preflight rechecks original': 'VerifyFile(original, oldHash, oldBytes);' in cutover,
    'preflight rechecks candidate': 'VerifyFile(candidate, newHash, newBytes);' in cutover,
    'preflight rechecks stage': 'VerifyFile(stage, newHash, newBytes);' in cutover,
    'preflight rechecks backup': 'VerifyFile(external, oldHash, oldBytes);' in cutover,
    'preflight preserves input snapshots': 'Safety.SameSnapshot(originalSnapshot)' in cutover and 'Safety.SameSnapshot(candidateSnapshot)' in cutover,
    'no cutover before stage/backup checks': cutover.index('VerifyFile(stage,') < cutover.index('VerifyFile(external,'),
    'new synthetic smoke cases registered': 'TestR26LateMutationAndIdentity(root);' in test,
    'stage tamper fixture': 'r26-late-' in test and 'stage-bytes' in test,
    'external backup tamper fixture': 'backup-bytes' in test,
    'late added hardlink fixture': 'stage-hardlink' in test,
    'same-bytes inode swap fixture': 'r26-same-bytes-different-identity' in test,
    'smoke test asserts original identity preservation': 'SameFile(originalIdentity)' in test,
    'runner executes structural checks': 'r26_contract_check.py' in runner,
    'runner executes filesystem reference': 'r26_reference_check.py' in runner,
    'CI executes structural check': 'r26_contract_check.py' in workflow,
    'CI executes reference check': 'r26_reference_check.py' in workflow,
}
for description, okay in checks.items():
    print(('PASS' if okay else 'FAIL') + '  ' + description)
if not all(checks.values()):
    raise SystemExit(1)
print(f'R2.6 source contract: {len(checks)}/{len(checks)} PASS; C# build and Windows NTFS tests NOT RUN')
