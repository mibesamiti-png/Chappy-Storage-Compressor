# R2.14 — PNG + JPG one-entry batch preview (personal media)

## New user workflow

From the normal Windows WPF application, select the photo/video parent folder and click **「PNG＋JPGまとめて無劣化候補作成／再開」**.

1. Choose the existing official `jpegtran.exe` from libjpeg-turbo, required for JPEG's independent decoded-pixel safety test. The old PNG-only button remains available if JPEG processing is not needed.
2. Choose **one output folder** outside the source tree. Existing PNG and JPG plans and journals remain separate in that folder, so the operation can resume without a new manifest format or migration.
3. A read-only lightweight inventory lists the approximate PNG/JPG/JPEG file counts, input byte totals, current output free space, special/empty files excluded from this **inventory only**, and scan errors.
4. Confirm before processing. PNG batch runs first, then JPEG. The user can stop/cancel; both existing engines keep their own resumable logs and candidates.
5. The progress bar and a single end-of-run message show candidate counts and hypothetical byte differences. **Actual recovered capacity remains 0 B**, because the originals were never replaced or deleted.

The inventory estimates based on filename extension, length and basic attributes. It is not image decoding, full source authentication, or guaranteed candidate qualification. The two existing image engines perform their own stronger validity, losslessness, free-space and journal checks independently. The scan can be cancelled.

## Important safeguards

- The source folder and output folder must be separate, non-overlapping, local paths. Reparse/junction candidate output is rejected by existing engine checks; source root requires an NTFS local personal media folder.
- No original image/video is changed, hardlinked or deleted, and there is no MP4 recompression.
- Any disk-full deferral during PNG stops the combined run **before JPEG**, leaving both queues resumable. If an individual candidate fails, its existing engine records it and continues according to its established rules.
- No cached results override engine validation. Reusing the same input/output/tool enables the individual existing resumable journals.
- A count of 100,000 or more for either type is handled by the existing batch constraints; the preflight refuses counts exceeding 100,000.
- Candidates **consume additional disk space**. Source-size totals are only an advisory high-water comparison against current output space, not a precise space requirement or capacity-recovery claim.
- Rare cross-account ACL/EFS/SACL cases are deferred. Special files remain subject to fail-closed engine protections; the preflight's candidate counts can differ from eventual eligible outputs.

## Windows synthetic tests

`R214PersonalBatchPreflightTests` in the existing smoke suite verifies PNG/JPG/JPEG count and byte totals, MP4 exclusion, empty and read-only inventory exclusions, free-space display, overlapping folder refusal, cancellation, no output writes, and no original changes. Prior R2.8–R2.12 NTFS probes and the original 359+ synthetic checks remain required. The WPF project is compiled on the Windows runner.

## Next

Speed up large collection planning/reporting from real benchmark evidence, reduce the number of manual dialogs needed for repeat runs, and improve JPG/PNG candidate throughput *without changing originals*. User media mutation (dedupe merge or original replacement) remains disabled until separately approved safety/recovery release gates.
