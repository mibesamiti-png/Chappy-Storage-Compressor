using System.IO;
using System.Security.Cryptography;

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

    public static string ComputeSha256(string path, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static bool ByteEqual(string a, string b, CancellationToken ct)
    {
        using var one = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var two = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        if (one.Length != two.Length) return false;
        byte[] left = new byte[1024 * 1024], right = new byte[1024 * 1024];
        int n;
        while ((n = one.Read(left, 0, left.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            int offset = 0;
            while (offset < n)
            {
                var got = two.Read(right, offset, n - offset);
                if (got == 0) return false;
                offset += got;
            }
            if (!left.AsSpan(0, n).SequenceEqual(right.AsSpan(0, n))) return false;
        }
        return two.ReadByte() < 0;
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
