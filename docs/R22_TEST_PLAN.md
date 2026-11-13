# R2.2 — Capacity preflight / Windows acceptance gates

Updated: 2026-11-14 JST. Implementation stage only. No actual replacement or merge is enabled in production.

## What shipped

- `Core/ReplacementReadiness.cs` calculates **advisory, per-candidate** minimum storage requirements and screens source file identity, attributes, NTFS named streams, and link count.
- The GUI button `容量回復の事前安全診断` chooses an output-record folder and a proposed external backup directory, then reports preliminary counts and top reasons for rejection.
- Fixed **64 MiB reserve** beyond candidate staging size on the input volume and beyond original size on the backup volume. This threshold is intentionally conservative and can be insufficient on unusual configurations.
- No source/candidate file modification, file copies, or directory creation in the new component. Even a passing advisory must not unlock replacement.

## Test matrix

| Category | Expected gate | Execution in this environment |
|---|---|---|
| Source 100 B, candidate 70 B, both exact reserve boundaries | PASS as *capacity only* | Independent Python formula: PASS |
| Candidate equal/larger than source | Fail | Independent Python: PASS |
| One byte short of source staging capacity | Fail | Independent Python: PASS |
| One byte short of backup capacity | Fail | Independent Python: PASS |
| Backup same volume as source | Fail | Independent Python: PASS |
| Negative/zero/huge inputs | Fail without overflow | Independent Python: PASS |
| WPF button, safe reading, production mutation guard | Structural checks | Python source inspection: PASS |
| Real NTFS file identity, named stream / sparse / reparse detection | Must reject unsafe inputs | C# Windows test pending |
| Runtime smoke test of `TestR22Readiness()` | All checks pass | C# Windows test pending |
| Reboot/crash during future file replacement | Bitwise original recovery | Not implemented, NOT tested |
| Verify backup on a genuinely independent *device* | Separate-device confidence beyond drive letter | Not implemented, NOT tested |
| Measure real free-space recovery | Actual bytes freed > 0 | Not implemented, 0 B |

## Windows execution

1. Extract source ZIP to a writable **test** folder.
2. Install .NET 10 SDK and run `RUN_DIAGNOSTICS.bat`.
3. Inspect `diagnostics/RESULT.txt`, `build.log`, `smoke-tests.log`, `publish.log`.
4. Do not enable merge/replacement even if diagnostics are green.
5. Report any error log back to the developer; perform subsequent integration tests using synthetic files on an isolated NTFS test volume.

## Acceptance criteria for actual replacement (future)

1. Candidate must be freshly revalidated for decoded content and relevant metadata; backup must be verified by hash before source modification.
2. Store a durable, recoverable journal before each state transition; prove every crash boundary including incomplete writes.
3. Verify correct security descriptor, timestamps, alternate streams, hard link count, file identity, and behavior for external concurrent writes.
4. Prove single-volume atomic transition where possible and fail closed on ambiguous states; never trust a preflight report as authorization.
5. Require verified independent backup capacity and a restore test; different volume letters alone do not ensure separate physical devices.
6. Require Windows CI green, isolated NTFS integration suite green, manual review, then *explicit* opt-in release flag (not done in R2.2).
