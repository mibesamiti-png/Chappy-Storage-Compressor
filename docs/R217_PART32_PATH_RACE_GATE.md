# R2.17 Part 32 — genuine external pathname swap and delete races

## Motivation and boundary

Part31 checked external writers, shared security/DACL edits and group-checkpoint corruption before independent Undo. However the real Core merge still had a **backup pathname identity gap**: after verifying a renamed independent backup, it could delete that pathname even when a competitor moved away the verified NTFS file and planted a same-byte but **different-file-ID** replacement at the path. The exception rollback path also needed to avoid assuming any path named `.chappy-backup-` still belonged to us.

Part32 tightens **generated-only test-exercised Core behavior** and adds five real separate-process pathname races. It **does not** establish race-free NTFS transactions.

## Changes

1. **Core dedupe irreversible delete check.** Immediately after the test-injectable `pre-backup-delete` event, re-open the backup as the *same original scanned NTFS identity*, check original times/attributes/security and byte identity with the master, and verify that the alias still points to the master. If another process has substituted even an identical-bytes backup at that pathname, refuse deletion. In the error rollback path, the independent backup must again be the precise original scanned NTFS file and original SHA before any automatic deletion of a newly added link or name restoration is considered. If this cannot be proven, keep the pending journal and **leave all untrusted paths untouched**.
2. **Core independent Undo deterministic post-final-validation check.** In `CHAPPY_TESTING` only, a new callback can let a real external worker rename/delete a shared alias or delete a stage immediately **after** the previously final byte/ACL check. The group-level test hook verifies the flushed checkpoint, then Core re-opens stage/alias/master identity, exact metadata and SHA **before** its `File.Replace`. This verifies detection at the simulated window, but the callback and added probe do **not** make actual `File.Replace` atomic with respect to an arbitrary competitor that moves a name after the last probe.
3. **Five real spawned child-process adversarial tests on disposable NTFS files:**
   - after the Core renamed an original to its independent backup, the child moves that backup elsewhere and plants an unrelated new file at the alias pathname; the original file ID and foreign bytes must remain intact and the pending journal must require manual review;
   - after Core created a master hardlink and verified the backup, the child moves the original backup elsewhere and **copies identical bytes/metadata back to the old backup path** under a *different NTFS file ID*; no independent backup may be silently deleted, even though SHA-256 matches;
   - after final Undo verification, the child moves the shared alias path to another generated name and plants foreign data at the old alias pathname;
   - after final Undo verification, the child deletes the shared alias pathname;
   - after final Undo verification, the child deletes the generated independent stage pathname.

Each child can only operate below a unique `ChappyStorageSmoke_` NTFS TEMP directory with the exact `.chappy-part23-generated-only` sentinel and a strict filename/action allowlist. The parent proves each child exited cleanly and wrote a flushed signal. The Core tests verify expected NTFS identities, original and foreign SHA-256, link counts, pending/manual-review evidence and no automatic second Undo of an ambiguous state.

## Unsolved release blockers

Windows path-based `File.Move`, `File.Delete` and `File.Replace` remain inherently susceptible to a last-instruction TOCTOU when an unrelated process has delete/rename rights. Reopening a file immediately before mutation cannot absolutely bind that later pathname operation to the scanned file's ID. This part is **detection and fail-closed testing at reproducible checkpoints, not a claim of atomic compare-and-rename**. An eventual production solution must evaluate handle-relative Win32 rename/delete and user-controlled consent/rollback policies, arbitrary directory reparse changes, power-cut/flush guarantees, ACL/owner/SACL/ADS/EFS cases, and hardlink editing semantics.

Production deduplication, original JPG/PNG replacement and independent Undo remain disabled; no user media are modified and recovered user capacity is **0 B**. Historical 69.4% source estimate is not release readiness.

A full PR-head Windows CI and independent postmerge main CI must pass before declaring Part32 complete.
