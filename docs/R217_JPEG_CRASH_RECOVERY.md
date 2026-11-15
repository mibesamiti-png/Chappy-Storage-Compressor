# R2.17 Part 20 — native jpegtran forced exit / genuine JPEG orphan after batch worker crash

## Why two independent failure points?

Part 18 checked **cooperative cancellation after a JPEG child starts**; Part 19 checked an **abruptly killed PNG writer** leaving a zero-byte unjournaled candidate. Part 20 tests both the *external native JPEG tool* and the *managed JPEG queue's candidate/journal gap*, using only fully generated files on the disposable Windows CI volume.

### Case A: kill the native encoder itself

The existing Core-only image-priority suite builds valid WIC JPEGs and runs the real `JpegLosslessPreviewEngine.CreatePreview` with the official-source-pinned SIMD `jpegtran.exe`. Its existing `CHAPPY_TESTING` child-PID callback calls `Kill(entireProcessTree: true)` directly against that exact running child **without cancelling the managed token**. It waits for the child to exit. The normal encoder engine must then detect its nonzero exit, raise a codec failure, discard all unverified candidate bytes from its own unique output directory and leave source SHA-256 and modification time untouched. A retry without the hook must generate exactly one smaller valid JPEG with identical JPEG marker audit and WIC decoded pixels.

This deliberately kills immediately after a native child is created. It proves native process death handling, **not** that the child had encoded image blocks before termination. The test asserts a native PID was observed and a forced kill truly happened, rather than a child that already exited cleanly.

### Case B: kill the managed batch process after a valid official JPEG exists, before its journal

The standalone Core smoke test has a second strictly guarded child mode for Part 20. The disposable child loads one valid synthetic WIC JPEG, runs the ordinary JPEG batch and official source-pinned SIMD tool, and waits for the normal JPEG candidate engine to finish. Only if the candidate was accepted (smaller file, marker audit and WIC pixel match) does a **test-only** callback execute between real `CreatePreview` return and the queue's first `Append` call. The callback records the exact candidate path outside the preview output root and immediately kills **its own managed process** using `Process.Kill`; normal `finally` cleanup and journal writing never run.

The parent process verifies the child exited nonzero and the existing output has one real valid JPEG orphan with no journal entry, plus a durable JPEG plan, preserved synthetic source SHA-256 and timestamp. The parent independently compares full WIC pixels and marker audit, checks both the combined read-only preflight and the direct JPEG resume refuse the orphan, and confirms the source, candidate and plan hashes have not changed. **No automatic orphan deletion occurs.** The parent explicitly deletes only its known disposable test orphan, reuses the old plan, resumes to one verified candidate with durable journal, and executes a second resume proving no duplicates, no altered candidate and no new journal records.

The native encoder has **completed** before this crash: it is a deterministic *post-encode/pre-journal* managed process failure, not an active native child killed mid-encoding.

## Required Windows CI evidence

- Existing `diagnostics/r217-priority-images-memory.json` retains all previous codec/memory, Part 18 and source protection flags and now requires `part20OfficialNativeJpegtranChildPidObserved`, `part20OfficialNativeJpegtranForceKillIssued`, `part20NativeNonzeroExitRefusedAndOrphanCleaned`, `part20AfterNativeKillRetryPixelsAndMarkersMatch`, `part20SourceHashAndMtimeUnchanged` all true.
- The independent `diagnostics/r217-jpeg-crash-recovery.json` must report a real child exit with a valid JPEG orphan, strict dual-resume refusal and byte-exact evidence preservation, successful explicit synthetic-only clearance, correct resumption with official codec and an idempotent second resume. It explicitly records `killedNativeJpegtranDuringActiveEncoding=false` because Case B deliberately crashes after the native tool completes.

All earlier independent Windows NTFS fault injection, WPF routed Click/Stop/Resume, source-pinned baseline and SIMD jpegtran, ICC/Exif/pixel, user-file exclusion, build and publish gates remain mandatory.

## Safety and nonclaims

Neither test runs on personal media or an arbitrary user-supplied folder. Native/test-only callbacks are compiled only in `CHAPPY_TESTING` Core smoke builds; the production WPF assembly does not expose them. The tests do not emulate a sudden power loss, do not prove atomic filesystem transactions, do not terminate the full WPF GUI process, and do not physically manipulate native dialogs. They must never auto-delete an unknown orphan in a real user's output root.

The original-file replacement, production hardlink merge and independent Undo remain **disabled**. Recovered real-user disk space **0 B**. Historical source implementation estimate **69.4%** is unchanged, not a readiness score. Merge only on green final PR-head CI; main push-triggered CI is a distinct verification requirement.
