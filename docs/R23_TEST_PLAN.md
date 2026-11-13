# R2.3 isolated cutover / rollback experiment — test plan

**Release status: prototype only. Not compiled into the application and not authorized for personal files.**

## Test code

- `Core/SyntheticReplacementExperiment.cs` (`#if CHAPPY_TESTING`)
- `tests/Program.cs` → `TestR23SyntheticReplacement(root)`
- `tools/r23_contract_check.py` — test gating and code/GUI contract
- `tools/r23_reference_check.py` — executable cross-platform *reference model*, **not** an NTFS test

## Preconditions and filesystem boundary

1. Windows NTFS scratch filesystem, unique path underneath `Path.GetTempPath()/ChappyStorageSmoke_{GUID}`.
2. Each experiment subfolder has a synthetic-only sentinel; fixed `.bin` filenames are used. Paths with reparse points, missing sentinel, an already linked source, named streams, unsafe file attributes, or active writer are refused.
3. Source/candidate bytes are random files generated at runtime. The smaller candidate is **not** a real PNG/JPEG optimization output; therefore this test does not establish lossless image semantics.
4. Backup directory is on the same scratch volume. It simulates separate-folder backup semantics, **not** a physically independent device. It must not be mistaken for disaster recovery.

## Failure checkpoints and expected outcome

| Injected stop | Before/after journal | Expected file behavior |
|---|---|---|
| `r23-after-external-copy` | Before | Original unchanged; no mutation journal |
| `r23-after-stage-copy` | Before | Original unchanged; candidate staging may remain |
| `r23-after-journal` | After journal, before cutover | Journal retained; original unchanged; rollback discards safe stage |
| `r23-after-replace` | After cutover | Candidate at original path; old original in rollback file; recovery restores old bytes |
| `r23-after-quarantine` | Restoration in progress | Original temporarily absent; old backup restorable |
| `r23-after-restore` | Restoration after move | Old original restored; cleanup resumes safely |

Other tests: normal cutover and rollback preserve original NTFS file identity; tampered external backup refuses recovery; active writer refuses before copying; source link count >1 refuses; missing sentinel refuses. `File.Replace` uses the same volume for `stage` and `original`.

## Unproven risks / release blockers

- .NET/C# build not yet run; no Windows smoke results yet.
- NTFS `File.Replace` metadata and security descriptor behavior, file-id invariance, directory-entry durability and disk/OS sudden-power-loss behavior.
- Concurrent writer/rename race exists between exclusive-access probe and cutover. Production code must coordinate writes or fail closed.
- No durable commit policy, no production rollback recovery and no safe deletion of backup.
- No validated quality-equivalence contract from PNG/JPG preview engine into replacement transaction.
- Low-space behavior requires tested staging space, independent backup and actual free-space measurement.

## Commands and release rule

On Windows run `RUN_DIAGNOSTICS.bat` and inspect the three logs. On Linux/macOS run `python tools/r23_contract_check.py` and `python tools/r23_reference_check.py`; passing these **cannot authorize real media modification**. No production release without a full green Windows/NTFS test matrix and human-confirmed opt-in process.
