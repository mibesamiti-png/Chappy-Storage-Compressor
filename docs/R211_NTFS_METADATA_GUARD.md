# R2.11 — NTFS owner, DACL and named-stream fail-closed preflight

## Scope

An isolated **GitHub Actions Windows NTFS synthetic-only** probe, not a production media editor or completed undo feature.

- The new `bootstrap/NtfsMetadataGuardProbe.cs` creates unique UUID-named, sentinel-verified directories within runner `RUNNER_TEMP`. It accepts **no command-line file path**.
- Every test generates a 256 KiB original and byte-identical independent stage.
- The probe captures **owner + DACL security descriptor as SDDL**, file ID (volume and index), hardlink count, SHA-256, size, ordinary file attributes, creation UTC and last-write UTC ticks, and checks no alternate data streams exist.
- The owner/DACL descriptor stays only in the process memory. It is **not included** in GitHub logs/JSON, where SIDs and access policies could become sensitive.
- Preflight compares two consecutive snapshots. Differences, added stream names, forbidden file attributes and shared aliases force a refusal, even if byte hashes are unchanged.
- Changes to security metadata are made only by `icacls` on disposable test files, granting a standard built-in SID rather than referring to a personal account.
- No `File.Replace`, deletion or hardlink consolidation is performed in the guard test. Thus the **original bytes remain untouched on every negative test**.

## R2.11 negative test matrix

| Scenario | Expected behavior |
|---|---|
| DACL changes on the original after fingerprint capture | Refuse despite identical original bytes |
| DACL changes on the prepared stage | Refuse without replacing original |
| NTFS ADS appended to original | Refuse; named stream remains readable |
| Staged file swapped for different ID containing equal bytes | Refuse because physical identity changed |
| Extra hardlink name created for stage | Refuse when link count increases |
| Source gains read-only attribute | Refuse |
| Exclusive open writer blocks the source | Refuse |

Each fixture must first pass a clean preflight. Workflow fails unless **7/7 clean baselines, 7/7 unsafe refusals, 2/2 DACL mutation detection, 1 ADS injection detection**, and all original byte hashes remain intact. The result is saved in `diagnostics/r211-ntfs-metadata.json`.

## What this DOES NOT prove

- Exact owner/ACL preservation through actual production undo/replacement.
- Preservation of arbitrary named streams, EFS, NTFS compression, SACL audit rules and inherited ACL behavior after moving a file.
- Prevention of TOCTOU races between a final path check and a later commit.
- Unexpected reparse points or arbitrary user-selected paths are not accepted by this synthetic harness.
- Hardware power-off / filesystem journal crash consistency / protected backups.
- Dedicated VHDX disk isolation or the Windows GUI's behavior.

The test is a **safe refusal gate**, not an ACL preservation implementation. Production hardlink consolidation, original-file replacement, and independent undo remain disabled. Related: [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2), [R3 Issue #6](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/6).

## Verification

Run `dotnet run --project bootstrap/NtfsMetadataGuardProbe.csproj -c Release` from the Windows CI checkout; it deliberately rejects any non-GitHub Actions runner. Check the uploaded `r211-ntfs-metadata.json` alongside the existing R2.8, R2.9 and R2.10 diagnostics.

Documentation on the Windows APIs used: [GetNamedSecurityInfoW](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-getnamedsecurityinfow) and [FindFirstStreamW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-findfirststreamw).

## Verified Windows CI evidence

[R2.11 Windows Actions run #43501540844](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43501540844) **succeeded**, including the isolated metadata probe, baseline WPF build, 359 C# safety checks, R2.8 NTFS space measurement, R2.9 process-kill journal replay, R2.10 hardlink independent restoration, and self-contained win-x64 publish.

- Clean preflight samples: **7/7**
- Unsafe modification scenarios correctly refused: **7/7**
- Original/staged file owner+DACL descriptor modifications detected: **2/2**
- Named ADS injection correctly detected: **1/1**
- Exact original SHA-256 bytes were preserved in all negative cases

Machine-readable evidence: `diagnostics/r211-ntfs-metadata.json` in the workflow's `chappy-windows-diagnostics` artifact. No raw SDDL or account SID is emitted.

The test uses `icacls` to alter only temporary generated files, not user content. The ability to **detect** an ACL/owner state change does not by itself prove that any production copy, replace or undo algorithm correctly **preserves** ACL/owner information.
