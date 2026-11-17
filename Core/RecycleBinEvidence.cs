#if CHAPPY_TESTING
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Independent, READ-ONLY verification of the Windows $I/$R recycle pair.
/// Scoped to CHAPPY_TESTING. A path disappearing or a successful Shell return
/// is never proof; verification requires original-path metadata AND the
/// independently readable $R bytes with the identical NTFS file ID.
/// The format is an OS implementation detail; unfamiliar variants fail closed.
/// </summary>
internal static class RecycleBinEvidence
{
    internal sealed record Finding(bool Verified, string Reason,
        string? IndexPath = null, string? RecycledPath = null);

    private sealed record IndexInfo(string OriginalPath, long Length,
        DateTime DeletedUtc);

    private const int MaxEntries = 2048;
    private const long MaxIndexLength = 128 * 1024;

    private static IndexInfo? ParseIndex(byte[] data)
    {
        if (data.Length < 26) return null;
        var b = data.AsSpan();
        long version = BinaryPrimitives.ReadInt64LittleEndian(b[..8]);
        long length = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(8, 8));
        long fileTime = BinaryPrimitives.ReadInt64LittleEndian(b.Slice(16, 8));
        if (length < 0 || fileTime <= 0) return null;
        DateTime removed;
        try { removed = DateTime.FromFileTimeUtc(fileTime); }
        catch (ArgumentOutOfRangeException) { return null; }
        int start;
        int chars;
        if (version == 1)
        {
            if (data.Length != 544) return null;
            start = 24;
            chars = 260;
        }
        else if (version == 2)
        {
            if (data.Length < 28) return null;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(24, 4));
            if (count == 0 || count > 32768 || count * 2UL > (ulong)(data.Length - 28))
                return null;
            start = 28;
            chars = checked((int)count);
            // Require a complete, exact single index payload. A malformed or
            // extended layout is unknown, rather than optimistically trusted.
            if (start + chars * 2 != data.Length) return null;
        }
        else return null;

        var path = Encoding.Unicode.GetString(data, start, chars * 2);
        int terminator = path.IndexOf('\0');
        if (terminator >= 0)
        {
            if (path.AsSpan(terminator).IndexOfAnyExcept('\0') >= 0) return null;
            path = path[..terminator];
        }
        if (string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path)) return null;
        try
        {
            if (!string.Equals(Path.GetFullPath(path), path,
                    StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        return new IndexInfo(path, length, removed);
    }

    /// <summary>Read a supplied synthetic $I/$R directory, no file changes.</summary>
    internal static Finding VerifyInDirectory(string recycleDirectory,
        string stagedOriginalPath, DuplicateCleanupWorkflow.FileEvidence expected,
        DateTime attemptStartedUtc, DateTime attemptFinishedUtc)
    {
        try
        {
            string directory = Path.GetFullPath(recycleDirectory);
            string original = Path.GetFullPath(stagedOriginalPath);
            if (!Directory.Exists(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 ||
                expected.Length <= 0 ||
                expected.FileIndex == 0 ||
                expected.Sha256.Length != 64 ||
                !expected.Sha256.All(Uri.IsHexDigit) ||
                !Path.IsPathFullyQualified(stagedOriginalPath) ||
                File.Exists(original) || Directory.Exists(original) ||
                attemptFinishedUtc < attemptStartedUtc)
                return new Finding(false, "Missing directory, occupied original, or invalid expected metadata");

            string[] indexes = Directory.EnumerateFiles(
                directory, "$I*", SearchOption.TopDirectoryOnly)
                .Take(MaxEntries + 1).ToArray();
            if (indexes.Length > MaxEntries)
                return new Finding(false, "Recycle Bin listing exceeded safety limit");
            int matches = 0;
            Finding? proof = null;
            foreach (var indexPath in indexes)
            {
                string name = Path.GetFileName(indexPath);
                if (name.Length < 4 || name[0] != '$' ||
                    char.ToUpperInvariant(name[1]) != 'I') continue;
                var attr = File.GetAttributes(indexPath);
                if ((attr & (FileAttributes.ReparsePoint | FileAttributes.Directory |
                    FileAttributes.Offline)) != 0) return new Finding(false, "Index is not regular");
                var info = new FileInfo(indexPath);
                if (info.Length > MaxIndexLength)
                    return new Finding(false, "Oversized recycle index");
                IndexInfo? index = ParseIndex(File.ReadAllBytes(indexPath));
                if (index is null)
                    return new Finding(false, "Unrecognized recycle index format");
                if (!string.Equals(index.OriginalPath, original,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (index.Length != expected.Length ||
                    index.DeletedUtc < attemptStartedUtc.AddMinutes(-2) ||
                    index.DeletedUtc > attemptFinishedUtc.AddMinutes(2))
                    return new Finding(false, "Matching path has wrong size or deletion timestamp");
                matches++;
                if (matches > 1)
                    return new Finding(false, "Multiple matching recycle indexes; cannot identify one file");
                string recycled = Path.Combine(directory, "$R" + name[2..]);
                if (!File.Exists(recycled) ||
                    (File.GetAttributes(recycled) & (FileAttributes.ReparsePoint |
                        FileAttributes.Directory | FileAttributes.Offline)) != 0)
                    return new Finding(false, "Matching recycle data is absent or not a regular file");
                var item = NativeFile.GetIdentity(recycled);
                if (item.VolumeSerial != expected.VolumeSerial ||
                    item.FileIndex != expected.FileIndex || item.LinkCount != 1 ||
                    new FileInfo(recycled).Length != expected.Length ||
                    !string.Equals(Safety.ComputeSha256(recycled, CancellationToken.None,
                            allowDeleteSharing: true), expected.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    return new Finding(false, "Recycle payload ID or bytes do not match");
                // Recheck identity after hashing to reject simple rename races.
                if (!item.SameFile(NativeFile.GetIdentity(recycled)) ||
                    File.Exists(original))
                    return new Finding(false, "Recycle payload changed during verification");
                proof = new Finding(true, "Index path/time/size and $R ID/SHA verified",
                    indexPath, recycled);
            }
            return matches == 1 && proof is not null ? proof :
                new Finding(false, "No independently verified recycle pair");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OverflowException)
        {
            return new Finding(false, "Recycle inspection failed closed: " +
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// Current user's SID-scoped bin only; never traverses other users' bins.
    /// Called solely after an explicit opt-in synthetic Shell request.
    /// </summary>
    internal static Finding VerifyForCurrentUser(string stagedOriginalPath,
        DuplicateCleanupWorkflow.FileEvidence expected,
        DateTime attemptStartedUtc, DateTime attemptFinishedUtc)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid) ||
                !sid.StartsWith("S-1-", StringComparison.Ordinal) ||
                sid.Any(c => !(char.IsLetterOrDigit(c) || c == '-')))
                return new Finding(false, "User SID is unavailable");
            string? volumeRoot = Path.GetPathRoot(Path.GetFullPath(stagedOriginalPath));
            if (string.IsNullOrWhiteSpace(volumeRoot)) return new Finding(false, "Invalid volume");
            string userBin = Path.Combine(volumeRoot, "$Recycle.Bin", sid);
            return VerifyInDirectory(userBin, stagedOriginalPath, expected,
                attemptStartedUtc, attemptFinishedUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception)
        {
            return new Finding(false, "Current-user recycle discovery denied: " +
                ex.GetType().Name);
        }
    }
}
#endif
