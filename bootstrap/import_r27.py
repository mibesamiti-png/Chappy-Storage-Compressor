#!/usr/bin/env python3
"""One-shot import of the checksum-pinned, user-approved R2.6 source into R2.7.

This runs on an isolated GitHub Actions runner only, and the resulting changes
are committed to a review branch rather than directly to main.
"""
from __future__ import annotations

import hashlib
import stat
import sys
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

ARCHIVE = Path("Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip")
EXPECTED = "a2b3ed077deb0a5be856a57ab32e39ebfcd0b650c572e7923dc974b4d0fff5ec"
PREFIX = PurePosixPath("ChappyStorageCompressor_R2_6")
SKIP_EXACT = {
    ".gitignore", "README.md", "SOURCE_MANIFEST.json",
    "docs/ROADMAP_STATUS.md",
}
PERMITTED_EXT = {".cs", ".csproj", ".xaml", ".bat", ".py", ".md", ".txt", ".json"}
PERMITTED_ROOT = {
    "App.xaml", "App.xaml.cs", "BUILD_EXE.bat",
    "ChappyStorageCompressor.csproj", "GlobalUsings.cs",
    "MainWindow.xaml", "MainWindow.xaml.cs",
    "RUN_DIAGNOSTICS.bat", "RUN_TESTS.bat",
    "RecoveryWindow.xaml", "RecoveryWindow.xaml.cs",
}

def safe_destination(name: str) -> Path | None:
    entry = PurePosixPath(name)
    if not entry.parts or entry.parts[0] != str(PREFIX):
        raise ValueError(f"Unexpected archive root: {name}")
    rel = PurePosixPath(*entry.parts[1:])
    if not rel.parts or any(p in (".", "..") for p in rel.parts):
        return None
    if str(rel) in SKIP_EXACT:
        return None
    if rel.parts[0] in ("docs", "tools"):
        if rel.parts[0] == "docs" and (
            "-verification" in str(rel) or "STATIC_VERIFY_RESULT" in str(rel)
            or "REFERENCE_RESULT" in str(rel)
        ):
            return None  # Historical output logs, not source.
    elif rel.parts[0] not in ("Core", "tests") and str(rel) not in PERMITTED_ROOT:
        return None  # Bundled CI + manifests are replaced by tracked CI.
    if rel.suffix.lower() not in PERMITTED_EXT:
        raise ValueError(f"Unexpected source extension: {name}")
    return Path(*rel.parts)

def main() -> None:
    if not ARCHIVE.is_file():
        raise FileNotFoundError(f"Missing approved source kit: {ARCHIVE}")
    actual = hashlib.sha256(ARCHIVE.read_bytes()).hexdigest()
    if actual != EXPECTED:
        raise ValueError(f"SourceKit checksum mismatch: {actual}")

    count = 0
    with ZipFile(ARCHIVE) as bundle:
        if bundle.testzip() is not None:
            raise ValueError("Source kit CRC check failed")
        for item in bundle.infolist():
            if item.is_dir():
                continue
            if stat.S_ISLNK(item.external_attr >> 16):
                raise ValueError(f"Symlink rejected: {item.filename}")
            dest = safe_destination(item.filename)
            if dest is None:
                continue
            if dest.exists():
                raise FileExistsError(f"Refusing to overwrite tracked file: {dest}")
            if item.file_size > 200_000:
                raise ValueError(f"Unexpectedly large source file: {item.filename}")
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(bundle.read(item))
            count += 1

    # Full R2.6 C# app uses explicit global imports; isolated tests link
    # Core/*.cs but did not include the main project's GlobalUsings.cs.
    shared = Path("GlobalUsings.cs").read_bytes()
    Path("tests/GlobalUsings.cs").write_bytes(shared)

    # A test-only fixture held the sentinel open with FileShare.None while
    # ValidateIsolatedDirectory immediately re-opened it. Dispose first.
    fixture = Path("Core/SyntheticReplacementExperiment.cs")
    original = fixture.read_text(encoding="utf-8")
    before = "        stream.Flush(true);\n        ValidateIsolatedDirectory(full);"
    after = "        stream.Flush(true);\n        stream.Dispose();\n        ValidateIsolatedDirectory(full);"
    if original.count(before) != 1:
        raise ValueError("Synthetic sentinel fix context mismatch")
    fixture.write_text(original.replace(before, after), encoding="utf-8")

    # Stack allocations in PNG chunk loops can accumulate until method exit.
    # Reuse the same small spans across iterations instead.
    inspector = Path("Core/PngInspector.cs")
    source = inspector.read_text(encoding="utf-8")
    before = (
        "        byte[] buffer = new byte[64 * 1024];\n"
        "        while (stream.Position < stream.Length)\n"
        "        {\n"
        "            ct.ThrowIfCancellationRequested();\n"
        "            Span<byte> header = stackalloc byte[8];"
    )
    after = (
        "        byte[] buffer = new byte[64 * 1024];\n"
        "        Span<byte> header = stackalloc byte[8];\n"
        "        Span<byte> ihdr = stackalloc byte[13];\n"
        "        Span<byte> trailer = stackalloc byte[4];\n"
        "        while (stream.Position < stream.Length)\n"
        "        {\n"
        "            ct.ThrowIfCancellationRequested();"
    )
    if source.count(before) != 1:
        raise ValueError("PNG inspector loop context mismatch")
    source = source.replace(before, after)
    for decl in ("            Span<byte> ihdr = stackalloc byte[13];\n",
                 "            Span<byte> trailer = stackalloc byte[4];\n"):
        if source.count(decl) != 1:
            raise ValueError("PNG inspector stack buffer context mismatch")
        source = source.replace(decl, "", 1)
    inspector.write_text(source, encoding="utf-8")

    optimizer = Path("Core/PngLosslessOptimizer.cs")
    source = optimizer.read_text(encoding="utf-8")
    before = (
        "        var chunks = new List<Chunk>();\n"
        "        while (stream.Position < stream.Length)\n"
        "        {\n"
        "            ct.ThrowIfCancellationRequested();\n"
        "            long offset = stream.Position;\n"
        "            Span<byte> header = stackalloc byte[8];"
    )
    after = (
        "        var chunks = new List<Chunk>();\n"
        "        Span<byte> header = stackalloc byte[8];\n"
        "        while (stream.Position < stream.Length)\n"
        "        {\n"
        "            ct.ThrowIfCancellationRequested();\n"
        "            long offset = stream.Position;"
    )
    if source.count(before) != 1:
        raise ValueError("PNG optimizer loop context mismatch")
    optimizer.write_text(source.replace(before, after), encoding="utf-8")

    # Historical assertions expected original R2.6 source. Keep historical
    # checks readable but do not edit them here to force false positives.
    Path("docs/R27_TRACKED_SOURCE_IMPORT.md").write_text(
        """# R2.7 — source-tracked import

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
""", encoding="utf-8"
    )
    print(f"Imported {count} reviewed source/archive files; added tracked fixes.")
    if count < 30:
        raise ValueError("Archive import count too small; aborting")

if __name__ == "__main__":
    main()
