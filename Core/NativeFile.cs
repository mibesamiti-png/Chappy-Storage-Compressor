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

    public static bool SameFile(string a, string b)
    {
        var first = GetIdentity(a);
        var second = GetIdentity(b);
        return first.SameFile(second);
    }
}
