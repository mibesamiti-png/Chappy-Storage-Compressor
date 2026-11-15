# R2.17 part 16 — real PNG+JPG candidate creation / restart from WPF Click

## What Part 16 tests

The previously proven Part 15 WPF routed button test supplied synthetic picker outcomes and refused operations, so no codec ran. Part 16 extends **the actual production `UnifiedBatch_Click` routed event flow** through a fully approved (Yes) combined preview and a second explicit approved restart. Production still displays Windows native file/folder pickers and message boxes. Only the **per-window private dialog adapters** are replaced by deterministic test callbacks on an isolated STA `MainWindow`.

The Windows CI sources a verified official libjpeg-turbo 3.2.1 `jpegtran.exe` built separately from pinned upstream commit `7b4993786d856cbccabfc0ed2f923ba000e326cc`, supplied through `CHAPPY_R217_PINNED_JPEGTRAN`. The test does not use a dummy executable for the successful path.

The UI harness creates 6 valid WIC-encoded JPEG and 6 valid uncompressed PNG synthetic sources under a newly generated disposable test root, then:

1. Routes the real `UnifiedBatchButton` Click. The actual output inspector and media preflight run, the test confirms displayed six PNG and six JPEG sources and explicitly approves.
2. Both actual candidate engines run, generating a minimum of four PNG and three JPEG candidates. The WPF UI must finish with 100% progress and the expected result text; `Stop` becomes disabled and the production Merge action stays locked.
3. The test reads the existing private plan and journal state, hashes all source originals and the separately generated candidates, and saves the original journal bytes.
4. Routes the **same real WPF Click again**, requiring the tool and output pickers and explicit approval a second time. The completed preview resumes with unchanged plan/journal hashes, candidate counts and candidate bytes; original SHA-256s remain unchanged.
5. The original Part 15 refusal tests (tool picker Cancel, output picker Cancel, No consent, cancel while consent is open, foreign output injected after Yes and retry needing fresh approval) remain in the same test runner.

When the pinned tool environment variable is absent outside CI, this real-source success stage is explicitly **SKIPPED** rather than pretending to run. CI requires `officialJpegtranWpfBatchActuallyExecuted=true`, `officialJpegtranWpfBatchResumeVerified=true`, 12 generated sources, at least four PNG and three JPEG candidate files, and prior evidence fields. The test's final result adapter is a native message box by default in production and an in-process callback solely for tests.

## Limits and next work

This is **not visual desktop or native dialog physical-click automation**; the test passes deterministic selections in the process and really runs format engines on generated files. A high-load cancellation between individual in-progress PNG/JPEG files and a restart of partially completed work are not simulated here; the earlier format-engine resume tests and cancellation token tests cover related but distinct routes. Candidate pixels, Exif and ICC are tested separately in the already mandatory codec CI suites.

This is **candidate-only**. No user photo/video, original replacement, production hardlink merge, production Undo or real freed disk bytes. Actual user capacity reclaimed: **0 B**. The old 69.4% source-development estimate is not product readiness. Do not claim Part 16 release complete until both PR-head and independently observed postmerge main Windows CI success.
