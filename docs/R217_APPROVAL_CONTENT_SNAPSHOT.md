# R2.17 Part 14 — byte-bound preview approval / fail-closed repeat inspection

## Rationale

The Part 13 combined PNG/JPEG confirmation path performed a second read-only output inspection, but compared **only PNG/JPEG plan presence and entry counts**. A valid-looking same-count plan or journal could change while the confirmation dialog was visible. That recheck would then incorrectly consider the approval unchanged even though the user never reviewed the new bytes.

## Change

`CombinedBatchResumeInspector.Summary` now carries four SHA-256 fingerprints (`PngPlanSha256`, `PngJournalSha256`, `JpegPlanSha256`, `JpegJournalSha256`). Empty string denotes a file that was absent, and the separate plan-presence flags preserve that distinction.

The inspector hashes **the exact bounded byte arrays it already reads to validate plan and journal data**, rather than reopening the paths after validation. `VerifyAfterConfirmation` compares both presence/counts and these digests after the explicit Yes. Any changed validated bytes, a missing/new plan, failed inspection, or cancellation prevents either preview engine from starting.

Production preview candidates and source media are untouched by this extra check. No subprocess is started by the inspector. The existing format engines retain their separate on-run validation, journaling and cancellation.

## Generated-only matrix

The existing synthetic Windows NTFS suite extends its consent test with the following positive and negative cases:

- Valid empty output, PNG-only and two-format plans still pass if unmodified.
- An **otherwise valid PNG plan** with an appended JSON whitespace byte has the same entry count but a different SHA-256, and is refused by the approval recheck.
- The same test independently covers the otherwise valid JPEG plan.
- A PNG journal with an additional blank row and a JPEG journal changed from empty to a blank row remain parseable and same-count, but are refused based on changed hashes.
- Already-canceled approval is refused before any new IO; test sources/plans/journals remain byte-identical.
- Restore the synthetic files and prove the previously displayed approval passes again.
- All previous stranger-file, changed plan presence, corrupt/torn journals and hardlink/refuse checks must still pass.

Six new Boolean evidence keys are required by the existing `diagnostics/r217-safe-preview-resume.json` strict CI gate. The full Windows build must retain pre-existing C# tests, routed WPF Scan/Stop, synthetic NTFS crash/security tests, source-pinned jpegtran fixtures and win-x64 publish. Do not infer a test pass from code changes or this document alone.

## Precise limits

This is *not* an atomic filesystem snapshot. It does not freeze concurrent writers or hash the contents of all candidate PNG/JPG files. A candidate or a file can change after the second check; the individual preview engines still must fail closed. The existing CI does not physically click native Windows picker dialogs or guarantee screen-rendered UX. A valid-looking file modified and restored to the exact same bytes between both inspections is not detectable by SHA-256 comparison. Real disk read throughput, codec speedups and cold-cache hardware performance are out of scope.

**No real user photos/videos. No original replacement, hardlink consolidation or production Undo. Actual user disk capacity reclaimed 0 B. Historical source estimate 69.4% is not product readiness.** Only merge after final PR-head Windows CI and independently verified postmerge-main CI.
