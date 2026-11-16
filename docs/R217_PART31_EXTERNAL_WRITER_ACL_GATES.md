# R2.17 Part 31 — external writer, shared ACL and durable checkpoint race gates

## Current boundary

Prior Parts 23–30 proved generated-only duplicate consolidation, multi-alias history, controlled process kills, real VHDX NTFS savings and a read-only Recovery Wizard. The production hardlink merge, original media replacement and group independent Undo remain **disabled**. These tests seek to detect competing changes, **not** make Windows path replacements globally atomic.

## New generated-only Windows proof

The new `tests/R217ConcurrentWriterAclTests.cs` starts **three real separate managed child processes**, each required to see the exact synthetic TEMP NTFS sentinel and explicitly opted-in environment guard:

1. A writer opens the generated **representative** in write mode with FileShare.ReadWrite|Delete and holds its handle. The real Core dedupe must refuse/skip before making any hardlink or transaction record.
2. A writer holds the generated **duplicate**. The real Core detects that its read-only guard cannot exclude the existing writer; no master, pending journal or original-byte mutation is permitted.
3. A writer modifies the generated duplicate's bytes **after scanning** and flushes to disk. The stale scan and original SHA must be rejected by the actual Core engine, preserving the competing writer's own content and NTFS file identity rather than overwriting it.

The test then creates two additional isolated, real, **3-filename** generated groups via `CompletedGroupLedger.MergeSynthetic` and invokes actual independent Undo with the now test-scoped pre-`File.Replace` hook.

4. The test replaces the shared master/alias's **DACL** with a real `SetFileSecurityW` call *after the independent stage was fully validated*. Ordinary SHA-256 remains unchanged; this is why checking only bytes was insufficient. The Core's final pre-cutover check now rechecks **both shared paths' original owner/group/DACL, timestamps, attributes, NTFS identity/link count and bytes**, and refuses without switching the alias identity. The group inspector reports ManualReview, and explicit retry while the external ACL remains changed also refuses. Only after the test deliberately restores the exact known original DACL, with identity/hash reverified, can explicit synthetic Undo resume.
5. The test corrupts a fully flushed **group Undo checkpoint** after staging but just before cutover. `CompletedGroupLedger` now reopens/validates the exact persistent checkpoint at that boundary and refuses the cutover. The corrupted record remains unmodified after the failed retry, the inspector shows ManualReview, and explicit **test-only** restoration of the exact known bytes permits recovery.

The Windows workflow requires a strict `diagnostics/r217-part31-concurrent-acl-gates.json` report. All previous Core safety tests, Parts1–30, WPF and published win-x64 binaries remain mandatory.

## Explicit limits

These tests provide deterministic, reproducible refusal of *observed* external writer/ACL/evidence changes on **generated NTFS files only**. They do **not** eliminate a later TOCTOU: an outsider may change or rename a pathname after our final validation or during `File.Replace`, and malicious ACL writers can race. File sharing denies normal writer handles during the guarded Core merge, but does not stop separate deleters or all privilege changes. True power loss, native Windows opportunistic locks, SACL/EFS/ADS/hardlink-path exhaustion, full real-user security inheritance and atomic global transactions remain production release blockers. No user file was consolidated, deleted or restored; recovered user capacity **0 B**.

Do not merge until full PR-head Windows CI is green; independently verify the push-triggered postmerge main workflow.
