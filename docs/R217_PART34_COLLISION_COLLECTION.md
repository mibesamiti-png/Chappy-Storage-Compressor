# R2.17 Part 34 — 同名ファイルの集約先と最上位報告メモ（ユーザー指定仕様）

## User-facing behavior

This is the collision policy requested for a **future extraction or independent-file restoration operation**. It is not yet wired into the product's archive decompressor (there is no active general ZIP/7z extraction path in the current Windows app) or its production independent Undo. The normal shipped operations remain unchanged.

- Existing destination data must **never be overwritten, deleted or automatically renamed**.
- If an incoming item has the same destination relative path as an existing file or folder, or a preceding incoming item, the additional item is diverted to a single folder named `同名ファイル` directly beneath the extraction/restoration **outermost output root**. Its original subdirectory structure is preserved inside this folder.
- Number the filename from ` (1)` upward, before the last extension. Example: `カメラ/動画.mp4` collides → `同名ファイル/カメラ/動画 (1).mp4`. Another copy colliding on the same path → `同名ファイル/カメラ/動画 (2).mp4`. Distinct original subfolders remain distinct.
- **Recursive same-folder nesting:** if the outer root already has a normal directory named `同名ファイル`, leave it intact and create `同名ファイル/同名ファイル`. If that also already exists, create `同名ファイル/同名ファイル/同名ファイル`. Never reuse the prior collision collection. A file, symlink or reparse point occupying any such nesting step requires **manual review rather than overwrite**.
- Only when a collision occurred, create a readable **UTF-8 with BOM** file at the **outermost root**, `同名ファイル一覧.txt`. It includes the count, original relative path, actual saved relative path and reason for each diverted item. If that report name exists, use `同名ファイル一覧 (1).txt`, `(2)`, etc. Never overwrite a prior report.
- If a parent directory name from the incoming tree is occupied by an existing ordinary **file** (e.g. `資料` existing file and incoming `資料/説明書.mp4`), divert the incoming child to `同名ファイル/資料/説明書 (1).mp4` without changing the existing `資料` file.
- A completely conflict-free operation creates no special folder and no report.
- Path traversal (`../`, `..\`), absolute paths, NTFS ADS-colon and reparse traversal are unsafe; refuse them rather than silently sanitizing user data.

## Example

```text
復元先/
├── カメラ/
│   └── 動画.mp4                  (existing; untouched)
├── 同名ファイル/
│   └── カメラ/
│       ├── 動画 (1).mp4          (incoming file #1)
│       └── 動画 (2).mp4          (incoming file #2)
└── 同名ファイル一覧.txt          (mapping report at outermost root)
```

If an existing `同名ファイル` folder is present, this run's output instead goes under the newly created nested `同名ファイル/同名ファイル` path, without merging into the prior collection.

## Implementation and tests

`Core/CollisionCollectionPlan.cs` provides a **read-only** `Inspect(root, incomingRelativePaths)`, a deterministic `Result` with original/new relative paths and reasons, and `ReportText`. No production file mutation API is part of this planner. `ProductionExtractionOrUndoEnabled = false`.

A separate `#if CHAPPY_TESTING` exercise writes **only synthetic disposable Windows NTFS fixtures with the project's exact `ChappyStorageSmoke_` sentinel** and only inside that root. It rejects any new collision appearing after the plan was inspected, checks sources are generated-only and ordinary, always uses `FileMode.CreateNew`, independently hashes copied bytes and writes the UTF-8 report after the generated copies. Existing original NTFS file IDs and hashes are checked unchanged.

The strict CI proof in `diagnostics/r217-part34-collision-collection.json` covers two same-name entries with `(1)`/`(2)`, original hierarchy, three levels of `同名ファイル` nesting, pre-existing report preserved with a numbered new report, existing file-vs-incoming-folder conflict, no-collision output, a foreign destination created after planning, traversal/ADS input refusal and source byte/hash preservation. Full earlier Part1–33, WPF and win-x64 publish gates remain required.

## Important limitations / production gate

The smoke exercise is **not** a production decompressor or safe Undo orchestrator; it does not guarantee atomic all-or-nothing extraction across multiple files if the process crashes after an individual generated copy. In production this would need a durable journal/report intent, source/output file-handle pinning, capacity checks, unknown NTFS access control handling, reparse and rename race defenses, permissions and explicit user confirmation. No user files were moved, overwritten, deleted, or restored by this change.

**Real recovered end-user capacity: 0 B.** Production deduplication, original JPG/PNG replacement, and independent Undo remain disabled. A historical 69.4% source estimate is not readiness.

**Merge rule:** full PR-head Windows CI green, then independent postmerge main CI green.
