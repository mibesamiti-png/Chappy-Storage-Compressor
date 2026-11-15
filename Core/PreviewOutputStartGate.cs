namespace ChappyStorageCompressor.Core;

/// <summary>
/// Fail-closed, read-only validation of the preview output's *root* entries.
/// This same set of file/directory names is used by combined consent and both
/// direct preview engines. It never deletes or repairs any user content.
/// </summary>
internal static class PreviewOutputStartGate
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ".chappy-png-batch-r16.json",
        ".chappy-png-batch-r16.progress.jsonl",
        ".chappy-png-batch-r16.lock",
        ".chappy-jpeg-r19.plan.json",
        ".chappy-jpeg-r19.progress.jsonl",
        ".chappy-jpeg-r19.lock"
    };

    private static readonly HashSet<string> Folders = new(StringComparer.OrdinalIgnoreCase)
    {
        "chappy-png-previews", "chappy-jpeg-r19-previews"
    };

    internal static void AssertKnownRoot(string output, CancellationToken ct = default,
        string? currentlyHeldLockName = null)
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(output))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > 100_010)
                throw new InvalidDataException("保存先の直下に項目が多すぎます。別フォルダを選んでください。");
            string name = Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((isDirectory && !Folders.Contains(name)) ||
                (!isDirectory && !Files.Contains(name)))
                throw new InvalidDataException(
                    "候補保存先に未確認のファイル/フォルダがあります: " + name);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System |
                               FileAttributes.Offline | FileAttributes.Encrypted |
                               FileAttributes.SparseFile | FileAttributes.ReadOnly)) != 0)
                throw new InvalidDataException("特殊属性の作業ファイル/フォルダは拒否します: " + name);
            JpegBatchPreviewEngine.CheckNoReparse(path);
            // The engine itself holds this file with FileShare.None.
            // The independent *pre-lock* check has already checked its NTFS
            // link count. Never reopen the owned live lock inside this check.
            if (!isDirectory &&
                !name.Equals(currentlyHeldLockName, StringComparison.OrdinalIgnoreCase) &&
                NativeFile.GetIdentity(path).LinkCount != 1)
                throw new InvalidDataException(
                    "作業ファイルが既存ハードリンクです。再開できません。");
        }
        ct.ThrowIfCancellationRequested();
    }
}
