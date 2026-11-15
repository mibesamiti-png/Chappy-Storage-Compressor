namespace ChappyStorageCompressor.Core;

/// <summary>
/// Reject candidate-directory entries which have no validated plan/journal
/// reference. A crashed writer may leave a correctly named but incomplete
/// candidate. Never auto-delete unknown evidence.
/// </summary>
internal static class PreviewReferenceGate
{
    internal static void RejectUnreferenced(string directory,
        IEnumerable<string?> referencePaths, CancellationToken ct = default)
    {
        if (!Directory.Exists(directory)) return;
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in referencePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
                referenced.Add(Path.GetFullPath(path));
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path) ||
                !referenced.Contains(Path.GetFullPath(path)))
                throw new InvalidDataException(
                    "再開記録に参照のない候補が残っています。自動削除や自動再開はせず、手動で確認してください: " +
                    Path.GetFileName(path));
        }
    }
}
