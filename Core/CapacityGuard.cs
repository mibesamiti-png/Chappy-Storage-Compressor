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

#if CHAPPY_TESTING
    // Strictly synthetic Core tests may inject low-space observations at
    // deterministic preflight windows. No such override exists in WPF/Release.
    internal static Func<string, (long TargetBytes, long JournalBytes)>?
        TestOnlyReadAvailable;
#endif

    public static (long TargetBytes, long JournalBytes) ReadAvailable(string targetRoot)
    {
#if CHAPPY_TESTING
        if (TestOnlyReadAvailable is { } synthetic)
            return synthetic(targetRoot);
#endif
        // DriveInfo may throw for disconnected or inaccessible drives: fail closed.
        string sourceDrive = Path.GetPathRoot(Path.GetFullPath(targetRoot))
            ?? throw new IOException("対象ドライブを特定できません。");
        string journalDrive = Path.GetPathRoot(Path.GetFullPath(Journal.FolderPath))
            ?? throw new IOException("復旧記録のドライブを特定できません。");
        return (new DriveInfo(sourceDrive).AvailableFreeSpace,
                new DriveInfo(journalDrive).AvailableFreeSpace);
    }

    // G3: a full independent copy must fit BEFORE creating its stage.
    // Immediately before File.Replace, the stage already occupies space,
    // but retain both target metadata reserve and journal reserve.
    // Observing available space is never a reservation or permission for
    // ordinary user-media Undo: that is still disabled.
    internal static void AssertCanRestore(string targetRoot,
        long additionalCopyBytes, bool stageAlreadyAllocated = false)
    {
        if (additionalCopyBytes < 0 ||
            additionalCopyBytes > long.MaxValue - TargetReserve)
            throw new IOException("独立Undoの必要容量が不正です。");
        long requiredTarget = checked(TargetReserve +
            (stageAlreadyAllocated ? 0L : additionalCopyBytes));
        var (target, journal) = ReadAvailable(targetRoot);
        if (!Enough(target, requiredTarget))
            throw new IOException(
                "独立Undoのコピーに必要な対象ドライブ空き容量が不足: " +
                $"観測 {GroupRow.HumanSize(target)} / 必要 {GroupRow.HumanSize(requiredTarget)}。");
        if (!Enough(journal, JournalReserve))
            throw new IOException(
                "独立Undoの復旧台帳に必要な空き容量が不足: " +
                $"観測 {GroupRow.HumanSize(journal)} / 必要 {GroupRow.HumanSize(JournalReserve)}。");
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
