# R2.7 — source-tracked import

- Input: SHA-256-pinned R2.6 ZIP, validated before extraction.
- New tracked files: C# WPF app, Core, tests, diagnostics scripts and design docs.
- Excluded: original bundled workflow, older README/status, manifests,
  generated verification logs, personal media and binaries.
- Fixed in tracked source: tests inherit the app's global imports; synthetic-only
  sentinel handle is disposed; PNG chunk buffer allocation is moved out of loops.
- Hardlink consolidation and original-file replacement remain disabled in the
  end-user application. Synthetic replacement tests are built separately.

## Verification gates

1. GitHub Actions Windows WPF build and synthetic C# suite must pass.
2. No mutation feature enabled in standard GUI.
3. Review source and recovery behavior before any release.
4. Update ROADMAP_STATUS.md after a verified run, not before.
