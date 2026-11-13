using System.IO;

namespace ChappyStorageCompressor.Core;

public sealed record FileIdentity(uint VolumeSerial, ulong FileIndex, uint LinkCount)
{
    public bool SameFile(FileIdentity other) => VolumeSerial == other.VolumeSerial && FileIndex == other.FileIndex;
}

public sealed class FileSnapshot
{
    public required string Path { get; init; }
    public required long Length { get; init; }
    public required DateTime CreationUtc { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required FileAttributes Attributes { get; init; }
    public required FileIdentity Identity { get; init; }
    public required byte[] SecurityDescriptor { get; init; }
    public string? Sha256 { get; set; }
}

public sealed class DuplicateGroup
{
    public required string Hash { get; init; }
    public required List<FileSnapshot> Files { get; init; }
    public FileSnapshot Representative => Files[0];
    public long Savings => Representative.Length * (Files.Count - 1);

    public string? BlockReason(bool allowTimestampDifference)
    {
        if (Files.Count < 2) return "重複なし";
        if (Files.Any(f => f.Identity.LinkCount != 1)) return "既存ハードリンクあり";
        var a = Representative;
        foreach (var b in Files.Skip(1))
        {
            if (a.Identity.SameFile(b.Identity)) return "すでに同一実体";
            if (a.Identity.VolumeSerial != b.Identity.VolumeSerial) return "異なるボリューム";
            if (a.Attributes != b.Attributes) return "ファイル属性の違い";
            if (!a.SecurityDescriptor.AsSpan().SequenceEqual(b.SecurityDescriptor)) return "アクセス権等の違い";
            if (!allowTimestampDifference && (a.ModifiedUtc != b.ModifiedUtc || a.CreationUtc != b.CreationUtc))
                return "日時の違い（設定で許可可）";
        }
        return null;
    }
}

public sealed class ScanResult
{
    public required string Root { get; init; }
    public required bool MediaOnly { get; init; }
    public required List<DuplicateGroup> Groups { get; init; }
    public int FileCount { get; init; }
    public int Skipped { get; init; }
    public int Errors { get; init; }
}

public sealed class GroupRow
{
    public required DuplicateGroup Group { get; init; }
    public required string RepresentativeDisplay { get; init; }
    public int Count => Group.Files.Count;
    public string FileSizeText => HumanSize(Group.Representative.Length);
    public string SavingsText => HumanSize(Group.Savings);
    public required string Status { get; init; }
    public static string HumanSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int index = 0;
        while (value >= 1024 && index < u.Length - 1) { value /= 1024; index++; }
        return $"{value:N1} {u[index]}";
    }
}

public sealed record EngineProgress(int Done, int Total, string Message, long SavingsBytes);

public sealed class PendingOperation
{
    public required string OperationId { get; init; }
    public required string OriginalPath { get; init; }
    public required string BackupPath { get; init; }
    public required string MasterPath { get; init; }
    public required string Sha256 { get; init; }
    public required DateTime RecordedUtc { get; init; }
    // R1.3+: optional for compatibility with R1.1/R1.2 recovery records.
    public FileIdentity? IndependentIdentity { get; init; }
    public FileIdentity? MasterIdentity { get; init; }
}
