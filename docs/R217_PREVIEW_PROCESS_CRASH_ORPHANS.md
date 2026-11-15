# R2.17 Part 19 — abrupt PNG candidate-process death and fail-closed orphan recovery

## Real generated-only crash test

The **standalone Core smoke-test executable** has a `CHAPPY_TESTING`-only, guarded child mode accepting an input folder, output folder and checkpoint file exclusively inside its own unique `ChappyStorageSmoke_...` temporary NTFS tree. The parent generates a valid, intentionally uncompressed PNG (512 x 320) and starts a separate Core-test process. The child invokes the genuine `PngBatchPreviewEngine.Run`, writing a real durable plan before the first candidate. A test-only `testOnlyAfterInflatedWrite` callback fires only after the source inflater has supplied at least one real block to the candidate compression stream. The child writes an isolated checkpoint **outside the preview destination**, then uses `Process.GetCurrentProcess().Kill()` to exit abruptly: no normal cancellation token, exception cleanup or finalizers. The parent waits for the child to exit, with a 45-second kill-on-timeout guard.

Expected aftermath: the plan persists; there is exactly one candidate-looking PNG file in `chappy-png-previews`, with no durable progress record referencing it. The original PNG hash and timestamp are unchanged. **The remaining candidate may be 0 bytes:** `TerminateProcess` does not unwind/flush the managed `FileStream` buffer, even though the test checkpoint proves one real inflater block entered the in-process encoder. The strict test records the actual remaining size, requires the file to exist, and refuses it regardless of size. It MUST NOT be accepted merely because its filename has a plausible GUID suffix.

## Production safety improvement

`PreviewReferenceGate.RejectUnreferenced` compares every file directly inside an existing candidate folder against the canonical paths from the **validated** plan plus replayed journal. The combined read-only resume preflight applies this check after both formats' plans and logs are validated. The direct PNG/JPEG queue engines also independently apply the same check **before processing any items**, so a direct format-specific button cannot skip the combined preflight.

- Any unreferenced PNG/JPEG candidate is **refused**: no journal repair, candidate deletion, automatic quarantine, or automatic resume.
- Evidence stays intact: both file bytes and the original saved plan are compared before/after the refusal; no user-owned file is touched.
- A separate generated-only test deliberately introduces correctly named but unreferenced PNG/JPG files and checks both combined and direct format paths reject while preserving their bytes.
- To prove an ordinary retry can work, **only the isolated test** manually deletes the exact synthetic orphan it created, then independently checks the previously saved plan and restarts the real PNG candidate generator. The engine creates one verified candidate and a proper journal.

## Strict CI proof and boundaries

Windows CI now additionally requires `diagnostics/r217-preview-process-crash.json` with successful subprocess termination, mid-inflated-block checkpoint, a single orphan and no journal, combined/direct refusal, unchanged evidence and SHA-256/timestamp, test-only manual clearance and one successful resumed candidate. It also requires the existing read-only inspector's extended evidence for both *valid-looking* PNG and JPEG orphans.

**What is not proven:** The killed executable is the independent **Core smoke-test worker**, not the full WPF app. This is a real process termination but not a power cut, arbitrary disk cache-loss boundary, or kill of an actual native JPEG encoder process; JPEG orphans are tested synthetically for rejection only. The checkpoint does not prove how much data reached persistent storage, merely that an inflated block entered the candidate compressor before process termination.

All test data are synthetic. There is no physical desktop mouse automation, production original replacement/hardlink consolidation/Undo, or real freed user bytes (**0 B**). Historical 69.4% source estimate is not a release-readiness metric. Independently verify postmerge main push CI; never infer that status from a green PR-head run.
