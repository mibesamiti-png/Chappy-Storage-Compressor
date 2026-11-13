using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("Windows required");

string sandbox = Path.Combine(Path.GetTempPath(), "Chappy-NTFS-Probe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
try
{
    // This executable never accepts an input path. Only its newly created TEMP sandbox is touched.
    string? root = Path.GetPathRoot(sandbox);
    if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("No volume root");
    var fsName = new StringBuilder(64);
    if (!NativeMethods.GetVolumeInformationW(root, null, 0, out _, out _, out _, fsName, (uint)fsName.Capacity))
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Volume query failed");
    if (!string.Equals(fsName.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("TEMP drive is not NTFS; the test cannot be credited");

    string source = Path.Combine(sandbox, "source.bin");
    string hardlink = Path.Combine(sandbox, "linked.bin");
    byte[] original = Encoding.ASCII.GetBytes("synthetic original bytes");
    byte[] modified = Encoding.ASCII.GetBytes("different synthetic bytes");
    File.WriteAllBytes(source, original);

    if (!NativeMethods.CreateHardLinkW(hardlink, source, IntPtr.Zero))
        throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic hardlink failed");
    if (!File.ReadAllBytes(hardlink).AsSpan().SequenceEqual(original))
        throw new InvalidOperationException("Synthetic hardlink content mismatch");

    // This proves why production deduplication must not treat hardlinks as independent backups.
    File.WriteAllBytes(hardlink, modified);
    if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(modified))
        throw new InvalidOperationException("Hardlink alias did not share writes");
    File.Delete(hardlink);
    if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(modified))
        throw new InvalidOperationException("Deleting one name damaged the remaining name");
    Console.WriteLine("PASS: Hardlinks share bytes; deleting one name preserves the other");

    string replacement = Path.Combine(sandbox, "replacement.bin");
    string backup = Path.Combine(sandbox, "original.backup");
    File.WriteAllBytes(source, original);
    File.WriteAllBytes(replacement, modified);
    byte[] expectedHash = SHA256.HashData(original);
    File.Replace(replacement, source, backup);
    if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(modified))
        throw new InvalidOperationException("Synthetic replacement did not commit");
    if (!SHA256.HashData(File.ReadAllBytes(backup)).AsSpan().SequenceEqual(expectedHash))
        throw new InvalidOperationException("Backup is not byte identical to synthetic original");
    File.Copy(backup, source, overwrite: true);
    if (!SHA256.HashData(File.ReadAllBytes(source)).AsSpan().SequenceEqual(expectedHash))
        throw new InvalidOperationException("Synthetic restoration did not restore original bytes");
    Console.WriteLine("PASS: Synthetic File.Replace backup and byte-exact restoration");
    Console.WriteLine("NTFS PROBE PASS - synthetic-only; not an application validation");
}
finally
{
    // Leave nothing on success; a failure is not permission to touch any external path.
    if (Directory.Exists(sandbox))
        Directory.Delete(sandbox, recursive: true);
}

internal static class NativeMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationW(
        string lpRootPathName, StringBuilder? lpVolumeNameBuffer, uint nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags, StringBuilder lpFileSystemNameBuffer, uint nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateHardLinkW(
        string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
