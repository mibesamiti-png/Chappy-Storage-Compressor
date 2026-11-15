# R2.17 Part 17 — real routed WPF Stop / partial PNG and JPEG resume

## The missing workflow
Part 16 proved both valid candidate engines complete from an actual WPF `UnifiedBatchButton` routed `Click`, and safely resume a *fully completed* run without duplicate candidates. It did not test a partially completed batch after the user presses the WPF Stop button.

Part 17 adds a deterministic, isolated Windows CI integration test that runs the actual PNG/JPG engines with 6 valid WIC-created JPEGs and 6 valid uncompressed PNGs and the independently source-pinned official `jpegtran.exe`, but interrupts **between files**. It uses a *separate generated output directory* from the already verified Part 16 run. The WPF window adds two private **null-by-default** inline test progress taps. They forward progress to the normal rate-limited UI progress reporter, then invoke an optional per-window observer on the worker, exactly after the engine appended its journal entry for that completed file. The isolated WPF test callback uses `window.Dispatcher.Invoke` to route a genuine `StopButton` WPF `Click` synchronously back on the STA dispatcher. This avoids flaky sleeps, filesystem watchers or scanning for the first file's output.

## End-to-end generated-only checks

1. Explicitly choose the official pinned tool, separate empty output and Yes on the real combined-button handler. On first `PNG Progress(Done=1, Status=Completed)`, the test routes a real Stop Click. The WPF status must become `中断（再開可能）`, the Stop button must disable, the one PNG candidate and its plan/journal must survive, and no JPEG plan may exist yet.
2. Reset the PNG-only tap. Explicitly choose the same tool/output/Yes again. The real PNG engine resumes all six PNGs, preserving the original candidate byte hash, then the JPEG tap stops when the first real official-jpegtran JPEG candidate completes and its journal is appended. Verify one JPEG candidate, both saved plans, six PNG candidates, the first PNG candidate's identical SHA-256, unchanged originals, and correct WPF canceled UI.
3. Reset the JPEG tap. Explicitly choose/approve again. Both engines run to completion through the real WPF handler: six PNG and six JPEG candidates, the two previously partial candidate byte hashes unchanged, all original SHA-256s preserved, 100% WPF progress, Stop disabled, Merge still disabled, exactly one final result callback. The prior Part 16 full run / no-duplicate restart and Part 15 user-refusal tests run in the same suite.

The Windows CI JSON gate requires `part17RoutedPngMidBatchStopVerified`, `part17RoutedJpegMidBatchStopVerified`, `part17PartialPngCandidatePreserved`, `part17PartialJpegCandidatePreserved`, `part17TwoStopsThenFullResumeVerified`, `part17StoppedBatchSourceSha256Unchanged` all `true`, the partial candidate counts equal to one each, final counts equal six each, and `part17PhysicalMouseStopClickVerified=false`. All prior NTFS crash/ACL/ADS tests, JPEG color/Exif/pixel and Windows x64 build/publish gates remain mandatory.

## Limits and safety

This is **real routed WPF event processing plus real candidate engines**, but not a physical mouse click or interactive native dialog automation. The Stop occurs at a **verified journal-safe boundary after a candidate**, not in the middle of a PNG read or jpegtran subprocess. It does not simulate an abrupt power loss or malicious concurrent filesystem change; these have independent fault tests. In normal production, both progress-tap fields are null and the codec engines behave as before; the test never substitutes or mocks codec output.

Only disposable synthetic generated media under the test runner's unique temporary root. **No original replacement, no production hardlink deduplication, no independent Undo. Actual user disk capacity recovered is 0 B.** Source estimate 69.4% is not a release readiness metric. Require final PR-head CI and independent postmerge main Windows CI before claiming completion.
