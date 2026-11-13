namespace ChappyStorageCompressor.Core;

/// <summary>
/// Conservative minimum-free-space gate; it does not create temporary media
/// copies and deliberately does not claim that free-space checks reserve space.
/// </summary>
public static class CapacityGuard
{
    // Hard-link creation still writes NTFS metadata; the journal is separately
    // persisted on LocalAppData (potentially a different drive).
    public const long TargetReserve = 4L * 1024 * 1024;
    public const long JournalReserve = 2L * 1024 * 1024;

    public static bool Enough(long availableBytes, long minimumBytes) =>
        availableBytes >= minimumBytes && minimumBytes >= 0;

    public static (long TargetBytes, long JournalBytes) ReadAvailable(string targetRoot)
    {
        // DriveInfo may throw for disconnected or inaccessible drives: fail closed.
        string sourceDrive = Path.GetPathRoot(Path.GetFullPath(targetRoot))
            ?? throw new IOException("対象ドライブを特定できません。");
        string journalDrive = Path.GetPathRoot(Path.GetFullPath(Journal.FolderPath))
            ?? throw new IOException("復旧記録のドライブを特定できません。");
        return (new DriveInfo(sourceDrive).AvailableFreeSpace,
                new DriveInfo(journalDrive).AvailableFreeSpace);
    }

    public static void AssertCanStart(string targetRoot)
    {
        var (target, journal) = ReadAvailable(targetRoot);
        if (!Enough(target, TargetReserve))
            throw new IOException($"対象ドライブの空きが {GroupRow.HumanSize(target)} のため統合を停止しました。" +
                $" 安全なNTFSメタデータ操作のため {GroupRow.HumanSize(TargetReserve)} 以上を必要とします。");
        if (!Enough(journal, JournalReserve))
            throw new IOException($"復旧記録ドライブの空きが {GroupRow.HumanSize(journal)} のため統合を停止しました。" +
                $" 安全な復旧記録保存のため {GroupRow.HumanSize(JournalReserve)} 以上を必要とします。");
    }
}
