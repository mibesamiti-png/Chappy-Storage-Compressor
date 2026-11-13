#!/usr/bin/env python3
"""R2.4 source wiring checks only. Not a C# build or functional proof."""
from pathlib import Path
r = Path(__file__).resolve().parents[1]
core = (r / 'Core/SyntheticReplacementExperiment.cs').read_text(encoding='utf-8')
tests = (r / 'tests/Program.cs').read_text(encoding='utf-8')
app = (r / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
prod = (r / 'ChappyStorageCompressor.csproj').read_text(encoding='utf-8')
checks = {
    'test-only conditional build gate': core.startswith('#if CHAPPY_TESTING') and core.rstrip().endswith('#endif'),
    'production does not define test flag': 'CHAPPY_TESTING' not in prod,
    'GUI cannot call synthetic operations': 'SyntheticReplacementExperiment' not in app,
    'test sentinel still required': 'ValidateIsolatedDirectory(directory)' in core,
    'pre-journal inspection exposed only to tests': 'internal static PreparationInspection InspectPreparation' in core,
    'cleanup rechecks artifacts immediately': 'if (!InspectPreparation(dir).CanDiscard)' in core,
    'rollback/journal blocks uncommitted cleanup': 'Transaction journal exists: use rollback' in core,
    'ambiguous rollback without journal refused': 'Rollback/quarantine exists without journal' in core,
    'orphan stage without backup refused': 'Impossible preparation order' in core,
    'orphan backup SHA+size verified': 'VerifyFile(external, oldHash, source.Length)' in core,
    'orphan candidate SHA+size verified': 'VerifyFile(stage, newHash, preview.Length)' in core,
    'pending journal parsed and matched': 'JsonSerializer.Deserialize<Record>(File.ReadAllText(tempJournal))' in core,
    'origin metadata rechecked': 'Safety.SameSnapshot(source)' in core,
    'input shared hardlinks rejected': 'source.Identity.LinkCount != 1' in core,
    'reserved directory collision rejected': 'AnyEntry(artifact)' in core and 'r24-reserved-name-directory' in tests,
    'artifact reparse rejected': 'FileAttributes.ReparsePoint' in core,
    'journal deletion before synthetic external cleanup': core.split('internal static void RollBack(string directory)', 1)[1].index('File.Delete(At(dir, JournalName));') < core.split('internal static void RollBack(string directory)', 1)[1].index('File.Delete(At(dir, ExternalName));'),
    'last-moment source-change test checkpoint': 'FaultInjection.Hit("r24-before-final-preflight")' in core and 'r24-last-moment-source-change' in tests,
    'pre-journal temp crash point': 'FaultInjection.Hit("r24-after-journal-temp")' in core,
    'post-journal cleanup crash point': 'FaultInjection.Hit("r24-after-journal-cleanup")' in core,
    'Windows smoke invokes new suite': 'TestR24PreparationRecovery(root);' in tests,
    'tampered synthetic backup smoke': 'r24-prep-tamper' in tests,
    'NTFS named stream smoke': 'r24-named-stream' in tests,
    'metadata identity smoke': 'Safety.SameSnapshot(before)' in tests,
    'no production COMMIT operation': 'internal static void Commit' not in core,
}
for desc, ok in checks.items():
    print(('PASS' if ok else 'FAIL'), desc)
if not all(checks.values()):
    raise SystemExit(1)
print(f'R2.4 static contract: {len(checks)}/{len(checks)} PASS; NOT a C# build')
