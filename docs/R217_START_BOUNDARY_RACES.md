# R2.17 Part 22 — source/output race gates at the actual preview engine entry

## Problem addressed

Earlier Part 15/21 WPF consent checks compared the output plan/journal digest and source inventory after Yes, but changes between the **last UI recheck and the first engine's lock/plan/candidate** were still possible. In addition, direct PNG/JPEG format buttons previously lacked the combined inspector's full output-root unknown-file allowlist. A third-party file added at the wrong time could be left beside a generated plan or output candidate without an immediate refusal.

## Production improvement

One shared `Core/PreviewOutputStartGate.cs` checks the actual output folder's root: only known PNG/JPEG plan, journal, lock and preview-directory names of correct types may exist. It refuses foreign files, unknown directories, reparse points, special NTFS attributes and non-unique hardlinked state files without deleting evidence. The combined read-only inspector uses **the same gate** as both production engines. When the WPF combined run supplies its **explicitly approved input snapshot**, both engines call this stricter gate:

1. **Before** opening their own exclusive lock, to reject an unrecognized root without writing a single lock or plan.
2. Immediately after obtaining their own lock, skipping a second open of that exact live `FileShare.None` lock while rechecking all other file identities.
3. After an input scan and **before** the first new plan is saved.
4. After loading/saving a real plan and **before** opening the preview directory/first candidate.

Older standalone direct-format calls without a WPF approval snapshot retain their existing plan/journal and candidate validation behavior, including tolerance for legitimate R1.7 journal-repair evidence backups and exported CSVs beside an existing PNG plan. **They do not gain the new root allowlist guarantee by default**; their independent orphan rejection remains in effect. The combined read-only inspector continues to reject unfamiliar root files, as before.\n\nThe WPF combined-click handler passes its original metadata-only `PersonalMediaBatchPreflight.Summary` to **both** preview engine runs. Each engine verifies this approved inventory against a fresh input scan before it acquires the lock and again after plan save/replay before creating any candidate. Source file names, file sizes, UTC modified time, attributes, skipped-special and scan-error counts are compared; volatile disk free space is intentionally not. Existing per-file source hashes, metadata checks and independent candidate validation remain unchanged.

## Deterministic generated-only Windows tests

- A foreign output file is inserted after displayed consent but **before** an explicitly approved PNG or JPEG engine begins. Both must refuse and leave the original folder containing exactly that unmodified foreign file, without creating a lock, plan, journal or candidate.
- A test-only hook in each Core engine inserts a foreign file after the plan is saved and before creating the candidate directory. Both must refuse, preserve the saved plan and foreign file bytes, and leave no candidate or journal. The combined inspector separately refuses the same folder. After the artificial test explicitly deletes only its own foreign file, the saved PNG/JPEG plan is reused for a real successful candidate and journal; JPEG must have identical protected JPEG markers and WIC RGB pixels.
- The same test-only hook creates an additional PNG input or retimestamps a JPEG input **after** the plan is saved. Because the engine received the user's original approved inventory, it refuses before creating a candidate. Restoring the synthetic source permits resuming the saved plan.
- A separate WPF STA/routed Click test injects a new PNG or foreign output file **immediately after the final UI preflight and before invoking the real PNG engine**. The real WPF handler must surface an error with no plan or lock written, preserving the original media hashes and injected foreign evidence. Its injection hook is private, null by default, and never executes in production.

Strict Windows CI requires `r217-preview-start-race.json` plus new fields on `r217-wpf-routed-click.json`, preserving all previous scan, generated NTFS hardlink/ACL/ADS, live PNG/JPEG encoder, WPF Stop/resume, crash/recovery, source-pin verification, ICC/Exif/pixel and win-x64 build/publish gates.

## Important nonclaims

Even repeated checks do **not** provide atomic filesystem snapshots. A malicious or competing process can mutate content immediately after a validation; no exclusive lock is held on the entire input tree, and the metadata snapshot does not hash all input file content. Candidate engines still independently protect original media and verify candidates; genuine power loss and production WPF process termination have not been exercised. Source inventory rechecks impose extra cost for large batches, which should be measured before release.

No automatic deletion of unknown files, no real user media touched, production original replacement, hardlink merge and independent Undo remain disabled. Actual end-user capacity recovered **0 B**, historic source estimate **69.4%** (not release readiness) unchanged. Final PR-head CI and independent postmerge main push CI are separate gates.
