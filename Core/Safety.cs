using System.IO;
using System.Security.Cryptography;
using System.Buffers;

namespace ChappyStorageCompressor.Core;

public static class Safety
{
    public const string StoreName = ".ChappyDedupeStore";
    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    { ".jpg", ".jpeg", ".png", ".mp4" };

    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    { "Windows", "Program Files", "Program Files (x86)", "ProgramData", "System Volume Information", "$Recycle.Bin", "$Extend" };

    public static bool IsMedia(string path) => Media.Contains(Path.GetExtension(path));

    public static string ValidateRoot(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new InvalidOperationException("親フォルダを選択してください。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("フォルダが見つかりません: " + full);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidOperationException("ネットワーク共有はR1対象外です。");
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("R1ではドライブ全体ではなく、写真・動画の親フォルダを選んでください。");
        var relative = Path.GetRelativePath(Path.GetPathRoot(full)!, full);
        foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (ForbiddenNames.Contains(component))
                throw new InvalidOperationException("Windows等の管理領域は対象外です。");
            if (component.Equals(StoreName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("統合先フォルダは選択できません。");
        }
        // A path INSIDE a junction must also be rejected, not only a junction root.
        var ancestor = full;
        var driveRoot = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
        while (!ancestor.Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("親階層にリンク・ジャンクションがあります: " + ancestor);
            var next = Path.GetDirectoryName(ancestor);
            if (next == null || next == ancestor) throw new InvalidOperationException("親階層を検証できません。");
            ancestor = Path.TrimEndingDirectorySeparator(next);
        }
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (!drive.IsReady || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("R1の統合対象はローカルNTFSボリュームに限定しています。");
        return full;
    }

    /// <summary>
    /// Scan admission is not mutation admission. NTFS nested folder scans
    /// keep their original strict Capture/ACL path. exFAT and drive roots
    /// (or an explicit search-only choice) use an isolated zero-write scanner.
    /// </summary>
    public sealed record ScanTarget(string Root, string Format, bool DiscoveryOnly);

    public static bool ExcludedFromDiscovery(string name) =>
        ForbiddenNames.Contains(name) ||
        name.Equals(StoreName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".chappy-cleanup-evidence", StringComparison.OrdinalIgnoreCase);

    public static ScanTarget ResolveScanTarget(string input, bool forceDiscovery = false)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("検索するフォルダまたはドライブを選択してください。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("ネットワーク共有・UNCパスは対象外です。");
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("フォルダが見つかりません: " + full);
        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!);
        var drive = new DriveInfo(Path.GetPathRoot(full)!);
        if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidOperationException("利用可能なローカルドライブのみ検索できます。");
        var format = drive.DriveFormat;
        if (!format.Equals("NTFS", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("exFAT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("検索対象はNTFSまたはexFATです: " + format);

        bool wholeDrive = full.Equals(root, StringComparison.OrdinalIgnoreCase);
        if (!forceDiscovery && !wholeDrive &&
            format.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            // Do not change the original strict NTFS mutation safety boundary.
            return new ScanTarget(ValidateRoot(full), format, false);
        }

        // A dedicated read-only root path admits a drive root; it never
        // loosens ValidateRoot, which all existing mutation code still uses.
        var ancestor = full;
        while (!ancestor.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            if (ExcludedFromDiscovery(Path.GetFileName(ancestor)))
                throw new InvalidOperationException("管理用・システム領域は検索できません。");
            var attributes = File.GetAttributes(ancestor);
            if ((attributes & (FileAttributes.ReparsePoint |
                FileAttributes.System | FileAttributes.Offline)) != 0)
                throw new InvalidOperationException("リンク・システムフォルダを経由できません。");
            string? parent = Path.GetDirectoryName(ancestor);
            if (parent == null || parent == ancestor)
                throw new InvalidOperationException("検索先の親階層を確認できません。");
            ancestor = Path.TrimEndingDirectorySeparator(parent);
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("ドライブルートのリンクを経由できません。");
        return new ScanTarget(full, format, true);
    }

    /// <summary>
    /// B2 shared admission boundary: never allow Core mutation to follow a
    /// supplied snapshot outside the selected NTFS root or through an
    /// unverified parent junction/reparse point. No writes and no symbolic
    /// link resolution. Call only after ValidateRoot(root).
    /// </summary>
    public static bool ContainedRegularSource(string root, string path)
    {
        try
        {
            string baseRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(baseRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            string relative = Path.GetRelativePath(baseRoot, full);
            if (relative.Split(Path.DirectorySeparatorChar)
                    .Any(component => component.Equals(StoreName,
                        StringComparison.OrdinalIgnoreCase)))
                return false;
            string? directory = Path.GetDirectoryName(full);
            while (directory is not null &&
                !directory.Equals(baseRoot, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(directory) ||
                    (File.GetAttributes(directory) &
                        (FileAttributes.ReparsePoint | FileAttributes.System |
                         FileAttributes.Offline)) != 0)
                    return false;
                string? parent = Path.GetDirectoryName(directory);
                if (parent is null || parent == directory) return false;
                directory = parent;
            }
            if (directory is null ||
                !File.Exists(full) ||
                !EligibleAttributes(File.GetAttributes(full)))
                return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static bool EligibleAttributes(FileAttributes attributes)
    {
        const FileAttributes denied = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.System |
            FileAttributes.Offline | FileAttributes.Encrypted | FileAttributes.SparseFile | FileAttributes.Compressed |
            FileAttributes.ReadOnly | FileAttributes.Temporary | FileAttributes.Device;
        return (attributes & denied) == 0;
    }

    public static FileSnapshot Capture(string path)
    {
        var fi = new FileInfo(path);
        fi.Refresh();
        if (!fi.Exists || fi.Length == 0 || !EligibleAttributes(fi.Attributes))
            throw new InvalidOperationException("圧縮済み・特殊属性・読み取り専用・空ファイル等は対象外");
        if (NativeFile.HasNamedDataStreams(fi.FullName))
            throw new InvalidOperationException("代替データストリームあり。安全のため対象外");
        return new FileSnapshot
        {
            Path = fi.FullName,
            Length = fi.Length,
            CreationUtc = fi.CreationTimeUtc,
            ModifiedUtc = fi.LastWriteTimeUtc,
            Attributes = fi.Attributes,
            Identity = NativeFile.GetIdentity(fi.FullName),
            SecurityDescriptor = NativeFile.ReadSecurity(fi.FullName),
        };
    }

    public static bool SameSnapshot(FileSnapshot original)
    {
        try
        {
            var updated = Capture(original.Path);
            return updated.Length == original.Length && updated.CreationUtc == original.CreationUtc &&
                   updated.ModifiedUtc == original.ModifiedUtc && updated.Attributes == original.Attributes &&
                   updated.Identity.SameFile(original.Identity) &&
                   updated.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor);
        }
        catch { return false; }
    }

    public static bool SameRenamedSnapshot(FileSnapshot original, string renamedPath)
    {
        try
        {
            var updated = Capture(renamedPath);
            return updated.Length == original.Length && updated.CreationUtc == original.CreationUtc &&
                   updated.ModifiedUtc == original.ModifiedUtc && updated.Attributes == original.Attributes &&
                   updated.Identity.SameFile(original.Identity) && updated.Identity.LinkCount == 1 &&
                   updated.SecurityDescriptor.AsSpan().SequenceEqual(original.SecurityDescriptor);
        }
        catch { return false; }
    }

    // R2.16: reuse modest work buffers across large media scans rather than
    // allocating 1 MiB per hash / 2 MiB per byte comparison. Return buffers
    // cleared to avoid retaining source image/video bytes in the shared pool.
    // FileStream buffering is disabled (bufferSize: 1) because each read
    // already fills the rented work buffer directly; otherwise each stream
    // creates another 256 KiB managed buffer per operation.
    private const int CompareBufferBytes = 256 * 1024;

    // R2.17 part 3: report byte-level reading at 4 MiB boundaries, or EOF.
    // The callback is only observation, never a permission to alter source data.
    // It may cancel the token; the method then aborts before returning any hash.
    private const int HashProgressStepBytes = 4 * 1024 * 1024;

    public static string ComputeSha256(string path, CancellationToken ct,
        Action<long, long>? onBytesRead = null, bool allowDeleteSharing = false)
    {
        ct.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            allowDeleteSharing ? FileShare.Read | FileShare.Delete : FileShare.Read,
            1, FileOptions.SequentialScan);
        long totalBytes = file.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CompareBufferBytes);
        long bytesRead = 0, lastReportedBytes = 0;
        try
        {
            int read;
            while ((read = file.Read(buffer, 0, CompareBufferBytes)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                bytesRead = checked(bytesRead + read);
                if (onBytesRead != null &&
                    (bytesRead - lastReportedBytes >= HashProgressStepBytes ||
                     bytesRead == totalBytes))
                {
                    onBytesRead(bytesRead, totalBytes);
                    lastReportedBytes = bytesRead;
                    ct.ThrowIfCancellationRequested();
                }
            }
            ct.ThrowIfCancellationRequested();
            // In an abnormal short read or source-size race, do not claim a
            // full read; callers still revalidate the NTFS snapshot separately.
            if (onBytesRead != null && lastReportedBytes != bytesRead)
                onBytesRead(bytesRead, totalBytes);
            ct.ThrowIfCancellationRequested();
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public static bool ByteEqual(string a, string b, CancellationToken ct,
        bool allowDeleteSharing = false)
    {
        ct.ThrowIfCancellationRequested();
        var share = allowDeleteSharing ? FileShare.Read | FileShare.Delete : FileShare.Read;
        using var one = new FileStream(a, FileMode.Open, FileAccess.Read,
            share, 1, FileOptions.SequentialScan);
        using var two = new FileStream(b, FileMode.Open, FileAccess.Read,
            share, 1, FileOptions.SequentialScan);
        if (one.Length != two.Length) return false;
        byte[] left = ArrayPool<byte>.Shared.Rent(CompareBufferBytes);
        byte[]? right = null;
        try
        {
            right = ArrayPool<byte>.Shared.Rent(CompareBufferBytes);
            int n;
            while ((n = one.Read(left, 0, CompareBufferBytes)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int offset = 0;
                while (offset < n)
                {
                    ct.ThrowIfCancellationRequested();
                    int got = two.Read(right, offset, n - offset);
                    if (got == 0) return false;
                    offset += got;
                }
                if (!left.AsSpan(0, n).SequenceEqual(right.AsSpan(0, n)))
                    return false;
            }
            return two.ReadByte() < 0;
        }
        finally
        {
            if (right != null) ArrayPool<byte>.Shared.Return(right, clearArray: true);
            ArrayPool<byte>.Shared.Return(left, clearArray: true);
        }
    }

    public static string StorePath(string root) => Path.Combine(root, StoreName);

    public static void ValidateStore(string root)
    {
        string dir = StorePath(root);
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("統合先がジャンクション/リンクです。処理を中断します。");
    }
}
