using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace ChappyStorageCompressor.Core;

public static class NativeFile
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileInformationByHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateHardLinkW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string linkName, string existingFileName, IntPtr reserved);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetFileSecurityW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurity(string fileName, uint securityInformation, byte[]? securityDescriptor,
        uint length, out uint lengthNeeded);

    // NTFS named streams can contain data not covered by ordinary SHA-256 / byte comparisons.
    // Fail closed if stream enumeration is unsupported, rather than silently dropping data.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstStreamW")]
    private static extern IntPtr FindFirstStream(string fileName, int infoLevel, out Win32FindStreamData data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextStreamW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStream(IntPtr handle, out Win32FindStreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);

    public static bool HasNamedDataStreams(string path)
    {
        var handle = FindFirstStream(Extended(path), 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            int code = Marshal.GetLastWin32Error();
            if (code == 38) return false; // ERROR_HANDLE_EOF: no streams to enumerate
            throw new Win32Exception(code, "NTFSストリーム情報を取得できません: " + path);
        }
        try
        {
            do
            {
                // The ordinary unnamed stream is called ::$DATA.
                if (!string.Equals(data.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (FindNextStream(handle, out data)) continue;
                int code = Marshal.GetLastWin32Error();
                if (code == 38) break;
                throw new Win32Exception(code, "NTFSストリーム列挙に失敗: " + path);
            } while (true);
        }
        finally { FindClose(handle); }
        return false;
    }

    private static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    public static FileIdentity GetIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new FileIdentity(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    public static void NewHardLink(string newLink, string existing)
    {
        if (!CreateHardLink(Extended(newLink), Extended(existing), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ハードリンク作成に失敗: " + newLink);
    }

    public static byte[] ReadSecurity(string path)
    {
        const uint ownerGroupDacl = 0x00000001 | 0x00000002 | 0x00000004;
        _ = GetFileSecurity(Extended(path), ownerGroupDacl, null, 0, out uint required);
        if (required == 0 || required > 1024 * 1024)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "アクセス権情報を読み取れません");
        var bytes = new byte[required];
        if (!GetFileSecurity(Extended(path), ownerGroupDacl, bytes, required, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "アクセス権情報の取得に失敗");
        return bytes;
    }

    // Part33 native pinned-handle operations, now shared with the real Core.
    // Deliberately an NTFS *file-ID* primitive, not an independent merge engine.
    // Callers own journaling, root confinement, alias-count and Undo decisions.
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const uint FileShareReadDelete = 0x00000001 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFilePinned(
        string name, uint desiredAccess, uint shareMode, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPinnedInfo(SafeFileHandle handle,
        int infoClass, IntPtr data, uint bytes);

    internal static FileIdentity IdentityFromHandle(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Pinned NTFS file identity unavailable");
        return new FileIdentity(info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            info.NumberOfLinks);
    }

    internal sealed class PinnedFile : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly FileIdentity _original;
        private readonly string _hash;
        private string _currentPath;

        private PinnedFile(SafeFileHandle handle, string path,
            FileIdentity id, string hash)
        {
            _handle = handle;
            _currentPath = path;
            _original = id;
            _hash = hash;
        }

        internal FileIdentity OpeningIdentity => _original;
        internal string OpeningHash => _hash;
        internal FileIdentity CurrentHandleIdentity => IdentityFromHandle(_handle);

        internal static PinnedFile Open(string path, FileIdentity expected,
            string hash, bool requireIndependent = true)
        {
            string full = Path.GetFullPath(path);
            if (!File.Exists(full) || !Safety.EligibleAttributes(
                    File.GetAttributes(full)) ||
                HasNamedDataStreams(full))
                throw new InvalidDataException("Pinned NTFS file must be regular without ADS");
            SafeFileHandle handle = CreateFilePinned(Extended(full),
                DeleteAccess | ReadAttributes, FileShareReadDelete, IntPtr.Zero,
                OpenExisting, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Cannot pin original NTFS identity");
            }
            try
            {
                var pinned = new PinnedFile(handle, full, expected, hash);
                if (!pinned.CurrentHandleIdentity.SameFile(expected) ||
                    (requireIndependent && pinned.CurrentHandleIdentity.LinkCount != 1) ||
                    !pinned.PathStillMatches(full))
                    throw new InvalidDataException("Original NTFS identity or SHA changed while pinning");
                return pinned;
            }
            catch { handle.Dispose(); throw; }
        }

        // Opening with DELETE access requires concurrent read streams to
        // share delete, while denying external write handles.
        internal bool PathStillMatches(string expectedPath)
        {
            try
            {
                string full = Path.GetFullPath(expectedPath);
                if (!full.Equals(_currentPath, StringComparison.OrdinalIgnoreCase) ||
                    !CurrentHandleIdentity.SameFile(_original) ||
                    !GetIdentity(full).SameFile(_original) ||
                    !string.Equals(Safety.ComputeSha256(full,
                        CancellationToken.None, allowDeleteSharing: true),
                        _hash, StringComparison.OrdinalIgnoreCase) ||
                    !GetIdentity(full).SameFile(_original))
                    return false;
                return true;
            }
            catch { return false; }
        }

        internal void RenameNoReplace(string destination,
            Action? afterLastPathCheck = null)
        {
            string target = Path.GetFullPath(destination);
            if (File.Exists(target) || Directory.Exists(target) ||
                !PathStillMatches(_currentPath))
                throw new InvalidDataException("Pinned rename preflight rejected source or destination");
            // Only the generated Part33 race test passes this callback.
            afterLastPathCheck?.Invoke();
            if (!CurrentHandleIdentity.SameFile(_original))
                throw new InvalidDataException("Pinned NTFS identity changed before rename");

            // FILE_RENAME_INFO class 3, ReplaceIfExists=FALSE.
            // The kernel acts on the already-open ID, never on a new lookup
            // of the original pathname. An occupied target is NOT replaced.
            byte[] name = System.Text.Encoding.Unicode.GetBytes(target);
            int lengthOffset = IntPtr.Size == 8 ? 16 : 8;
            int nameOffset = lengthOffset + 4;
            byte[] buffer = new byte[nameOffset + name.Length + 2];
            BitConverter.GetBytes(name.Length).CopyTo(buffer, lengthOffset);
            name.CopyTo(buffer, nameOffset);
            IntPtr p = Marshal.AllocHGlobal(buffer.Length);
            try
            {
                Marshal.Copy(buffer, 0, p, buffer.Length);
                if (!SetPinnedInfo(_handle, FileRenameInfo, p, (uint)buffer.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Pinned NTFS no-replace rename rejected");
            }
            finally { Marshal.FreeHGlobal(p); }
            _currentPath = target;
            if (!PathStillMatches(target))
                throw new InvalidDataException(
                    "Pinned rename postcheck failed; preserve journal evidence");
        }

        // A disposition acts on the file ID associated with this handle.
        // requireIndependent protects the last data-bearing backup deletion;
        // for rollback a caller may remove only its separately authenticated
        // shared alias link without deleting the shared master content.
        internal void DeleteCurrent(bool requireIndependent)
        {
            if (!PathStillMatches(_currentPath) ||
                (requireIndependent && CurrentHandleIdentity.LinkCount != 1))
                throw new InvalidDataException(
                    "Pinned delete refused changed path, identity, hash or link count");
            IntPtr p = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(p, 1);
                if (!SetPinnedInfo(_handle, FileDispositionInfo, p, 4))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Pinned NTFS delete disposition rejected");
            }
            finally { Marshal.FreeHGlobal(p); }
            _handle.Dispose();
        }

        public void Dispose() => _handle.Dispose();
    }

    public static bool SameFile(string a, string b)
    {
        var first = GetIdentity(a);
        var second = GetIdentity(b);
        return first.SameFile(second);
    }
}
