# R2.17 Part 33 — pinned Win32 NTFS file-ID rename/delete feasibility gate

## Existing problem

Parts 31 and 32 proved that the current path-based Core deduplication and independent Undo detect many external writer, owner/DACL, missing stage, and pathname-replacement races. A fundamental **time-of-check/time-of-use** gap remains: a different process can rename the source away or plant another file at the same path immediately **after** our final snapshot, before `File.Move`, `File.Delete` or `File.Replace` acts on the name. Repeating a path snapshot cannot make that final call atomic.

## Official Windows API design

Microsoft describes `SetFileInformationByHandle` as an operation on an **already-open file handle**, with `FILE_RENAME_INFO` (information class **3**) and `FILE_DISPOSITION_INFO` (class **4**). For `FileRenameInfo`, `ReplaceIfExists=FALSE` means that an occupied destination must **fail rather than overwrite**. For disposition deletion, `DELETE` access must be requested when the handle is opened, and actual removal occurs when appropriate handles close. Relevant Microsoft documentation:
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info
- https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info
- https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew

The **Part33 prototype** is `Core/SyntheticPinnedNtfsFile.cs`, compiled entirely inside `#if CHAPPY_TESTING`. It accepts only **direct-child, existing regular .mp4 test files** in a `ChappyStorageSmoke_` TEMP root with the exact Part23 generated-only sentinel. Reparse files and named alternate data streams are disallowed; ordinary source files must have one NTFS link. It opens with `CreateFileW(DELETE | FILE_READ_ATTRIBUTES)`, checks NTFS volume + file index with `GetFileInformationByHandle`, and calls `SetFileInformationByHandle` on the **held handle**, never by newly opening an untrusted pathname at the last instruction. It uses FileRenameInfo with no-replace; FileDispositionInfo deletion is tested only against a disposable independent single-link original. It reopens and checks current source metadata and SHA before optional mutation.

## Eight Windows scenarios — actual spawned competitors

`tests/R217PinnedHandleRaceTests.cs` includes **four real independent managed child processes**, each restricted to exact generated TEMP files:

1. **Postcheck source swap:** after the prototype has verified its original path and pinned its handle, a child moves the original to a different generated name and installs unrelated bytes under the old source path. The prototype's handle-based rename must move **only the original NTFS ID** to the unique target and preserve the foreign occupant under the old name. The original source bytes and file ID remain the same.
2. **Same-bytes, wrong file ID:** an external child moves the original away, copies identical bytes and timestamps into the original name but creates a **new NTFS file ID**. The strict path guard rejects this even though both SHA-256 values match; neither original nor foreign copy may be deleted.
3. **Destination arrival between check and Win32 call:** a child creates a foreign file at the intended target name after the last precheck. `ReplaceIfExists=FALSE` must cause the native API to refuse and retain both names and their original/foreign bytes.
4. **Changed source before check:** a child moves the original and places unrelated bytes at the old name. The check refuses any rename, leaving both untouched.
5. **Ordinary no-contention rename:** preserved SHA-256 and NTFS identity at the new name, no unexpected remaining original path.
6. **Ordinary disposition delete:** marks and closes **one known disposable single-link file** by its pinned handle. No user file or shared deduplicated data is eligible.
7. **Extra hardlink rejection:** opening a shared two-link fixture for destructive handle operations is refused with both names intact.
8. **Path confinement:** a target name outside the generated-only root is rejected without filesystem mutation.

The report `diagnostics/r217-part33-handle-identity-races.json` is a strict Windows CI gate. All earlier tests, actual VHDX capacity, WPF smoke and publish remain mandatory.

## Explicit nonclaims and adoption decision

This is **not wired into the production Core deduplication or independent Undo**. The production mutating entrypoints remain disabled. The current prototype demonstrates only the *identity binding of a handle-based rename* and *kernel-level occupied-destination refusal* under these generated NTFS scenarios. It is not an all-or-nothing transaction across (a) original alias, (b) backup/stage, (c) master hardlinks, and (d) durable ledger; a competitor can still delete/rename the **destination directory** or change permissions, and the source may be externally relocated even though its handle stays bound to the same ID.

The first demo **intentionally** permits moving an original that a competitor just relocated, solely within generated fixtures to prove the target is the pinned original rather than a foreign pathname occupant. That is **not approved as automatic production behavior**: a real product should normally fail closed when unexpected relocation is detected, even if a handle is still valid. Further evaluation must include directory-handle-relative paths, no-replace destination behavior under all sharing modes, registry/OS support, crash consistency, owner/ACL/SACL/ADS/EFS and explicit user opt-in before integrating handle-based mutation into the real Core engine.

**No physical power-cut testing**, no arbitrary endpoint attacker guarantee, no real-user source file change, **0 B user capacity reclaimed**. Historical 69.4% remains an old source-coverage estimate and not release readiness. PR-head Windows CI and separate postmerge main CI must both succeed before marking the Part33 feasibility gate complete.
