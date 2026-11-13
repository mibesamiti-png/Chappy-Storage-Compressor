# R2.5 test plan — prevent hash-only rollback artifact trust

Updated: 2026-11-14 JST. All file-mutating experiments are **compiled with `CHAPPY_TESTING` only** and restricted to disposable `.bin` fixtures in a unique, sentinel-protected Windows TEMP directory. **No personal media may be used.**

## Safety defect addressed

R2.4 checked SHA-256 and byte length for staging files and the journal, but a file with identical unnamed-stream bytes could also be a *shared hardlink*, a reparse point or a file with additional NTFS named streams. Deleting or moving those objects could have unintended consequences. R2.5 now applies `RequireIndependentPlainFile` before trusting synthetic backup, stage, rollback, quarantine, source, journal or pending journal. A journal also has a strict 1..8192 byte size bound. This blocks straightforward unsafe cases, but remains **non-atomic and test-only**; there is still a time-of-check/time-of-use race.

## New Windows smoke test cases

| Fixture | Injection | Required behavior |
|---|---|---|
| `r25-shared-external` | Add another hardlink to pre-journal external backup | Inspect refuses cleanup; both names and source remain |
| `r25-shared-journal` | Add another hardlink to active transaction journal | Rollback refuses; original path and evidence stay |
| `r25-shared-rollback` | Add another hardlink to rollback data | Rollback refuses despite matching contents |
| `r25-oversized-journal` | Append beyond maximum journal size | Rollback refuses without changing original path |
| `r25-shared-temporary-journal` | Add another hardlink to pending journal before rename | Cleanup refuses |
| `r25-stage-ads` | Add NTFS named stream to stage after candidate copy | Cleanup refuses even with same unnamed-stream SHA-256 |

The smoke entry point refuses to run if TEMP is not on **NTFS**. Existing R2.3/R2.4 synthetic crash and rollback tests still run.

## Repro and evidence gates

1. On Windows 10/11 with .NET 10 SDK installed, run `RUN_DIAGNOSTICS.bat` as a standard user.
2. Keep `diagnostics/RESULT.txt`, `dotnet-info.txt`, `build.log`, `smoke-tests.log`, `publish.log`, and optionally `static-checks.log`.
3. The source kit includes CI (`.github/workflows/build-windows.yml`) but no remote CI execution is claimed. `EnableWindowsTargeting=true` allows restore/compilation attempts from non-Windows .NET SDK hosts that can download Windows targeting packs; it **does not** make WPF runnable on Linux.
4. Windows pass is not sufficient for user-data mutation: fail-closed production handles, independent backup volume, synchronized journal, concurrent write race, metadata/ADS fidelity, actual volume space measurements, and physical power-loss testing remain required.

## Verified in authoring environment

- R2.5 structural contract `tools/r25_contract_check.py`: **25/25 PASS**; source-text checks only.
- R2.5 generic filesystem reference `tools/r25_reference_check.py`: **10/10 PASS**; POSIX/Python model only, not Windows NTFS or C#.
- Prior Python reference suites: all pass in this environment.
- Windows .NET build: **NOT RUN**; .NET SDK unavailable in the authoring container.
- C# NTFS smoke tests: **NOT RUN**.
- Actual capacity recovered: **0 B**; dedupe merge, original replacement and commit remain disabled.
