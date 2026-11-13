using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// R2.8 measurements intentionally do not reference the WPF app or mutate user files.
// The program has no path arguments, runs only in GitHub's Windows runner, and
// creates its own random isolated directory inside RUNNER_TEMP.
internal static class Program
{
    private const int DuplicateBytes = 16 * 1024 * 1024;
    private static readonly byte[] PngSignature = {137, 80, 78, 71, 13, 10, 26, 10};

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new InvalidOperationException("This probe accepts no paths or arguments");
            Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("R28 SYNTHETIC MEASUREMENT FAILED: " + ex);
            return 1;
        }
    }

    private static void Run()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new PlatformNotSupportedException("R2.8 experiment is CI Windows-only");

        string runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidOperationException("RUNNER_TEMP required");
        runnerTemp = Path.GetFullPath(runnerTemp);
        if (!Directory.Exists(runnerTemp) ||
            (File.GetAttributes(runnerTemp) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("RUNNER_TEMP is absent or a reparse point");

        string root = Path.GetPathRoot(runnerTemp)
            ?? throw new InvalidOperationException("No runner temp volume");
        var fsName = new StringBuilder(64);
        if (!Native.GetVolumeInformationW(root, null, 0, out _, out _, out _,
            fsName, (uint)fsName.Capacity))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Volume query failed");
        if (fsName.ToString() != "NTFS")
            throw new InvalidOperationException("R2.8 requires a disposable NTFS runner volume");
        if (!Native.GetDiskFreeSpaceW(root, out uint sectors, out uint bytesPerSector,
            out _, out _))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cluster query failed");

        ulong clusterBytes = (ulong)sectors * bytesPerSector;
        var volume = new DriveInfo(root);
        string sandbox = Path.Combine(runnerTemp, "Chappy-R28-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(sandbox) || File.Exists(sandbox))
            throw new IOException("Synthetic sandbox unexpectedly exists");
        Directory.CreateDirectory(sandbox);

        try
        {
            string primary = Path.Combine(sandbox, "synthetic-a.bin");
            string duplicate = Path.Combine(sandbox, "synthetic-b.bin");
            var payload = new byte[DuplicateBytes];
            RandomNumberGenerator.Fill(payload);
            File.WriteAllBytes(primary, payload);
            File.Copy(primary, duplicate);
            byte[] expectedHash = SHA256.HashData(payload);
            Assert(HashEquals(primary, expectedHash) && HashEquals(duplicate, expectedHash),
                "Generated duplicates are not identical");
            var originalIds = (ReadIdentity(primary), ReadIdentity(duplicate));
            Assert(originalIds.Item1.Id != originalIds.Item2.Id,
                "Generated pair unexpectedly shares file identity");
            Assert(originalIds.Item1.Links == 1 && originalIds.Item2.Links == 1,
                "Synthetic inputs have unexpected hardlinks");

            long primaryAllocated = AllocatedSize(primary);
            long secondAllocated = AllocatedSize(duplicate);
            long freeBeforeDedupe = volume.AvailableFreeSpace;
            // Only these new, generated sandbox files are ever deleted or linked.
            File.Delete(duplicate);
            if (!Native.CreateHardLinkW(duplicate, primary, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                    "Creating synthetic duplicate hardlink failed");
            long freeAfterDedupe = volume.AvailableFreeSpace;
            var linkedIdA = ReadIdentity(primary);
            var linkedIdB = ReadIdentity(duplicate);
            Assert(linkedIdA.Id == linkedIdB.Id && linkedIdA.Links >= 2 &&
                   linkedIdB.Links >= 2, "Expected two names to share NTFS file identity");
            Assert(HashEquals(primary, expectedHash) && HashEquals(duplicate, expectedHash),
                "Hardlink consolidation changed synthetic bytes");

            // The physical estimate counts each FILE ID exactly once.
            long uniqueAfter = AllocatedSize(primary);
            long physicalSaved = primaryAllocated + secondAllocated - uniqueAfter;
            Assert(physicalSaved > 0, "Synthetic dedupe did not reduce unique allocated bytes");

            // A generated valid RGBA PNG with a deliberately uncompressed IDAT,
            // compared with an independently recompressed, byte-exact scanline stream.
            string png = Path.Combine(sandbox, "synthetic-original.png");
            string candidate = Path.Combine(sandbox, "synthetic-optimized.png");
            string backup = Path.Combine(sandbox, "synthetic-independent.backup");
            string restoreStage = Path.Combine(sandbox, "synthetic-restore.tmp");

            byte[] scanlines = SyntheticScanlines(512, 512);
            File.WriteAllBytes(png, MakePng(scanlines, 512, 512, CompressionLevel.NoCompression));
            File.WriteAllBytes(candidate, MakePng(scanlines, 512, 512, CompressionLevel.SmallestSize));

            byte[] pngOriginalHash = SHA256.HashData(File.ReadAllBytes(png));
            var originalInfo = ReadPng(png);
            var candidateInfo = ReadPng(candidate);
            Assert(originalInfo.Scanlines.AsSpan().SequenceEqual(candidateInfo.Scanlines),
                "PNG decoded filtered scanlines changed");
            Assert(originalInfo.Metadata.AsSpan().SequenceEqual(candidateInfo.Metadata),
                "PNG non-IDAT chunks including metadata changed");
            Assert(originalInfo.Scanlines.AsSpan().SequenceEqual(scanlines),
                "PNG generator produced unexpected scanlines");
            long pngOriginalLength = new FileInfo(png).Length;
            long pngCandidateLength = new FileInfo(candidate).Length;
            Assert(pngCandidateLength < pngOriginalLength,
                "Synthetic PNG lossless candidate did not get smaller");
            long pngOriginalAllocated = AllocatedSize(png);
            long candidateAllocated = AllocatedSize(candidate);
            long freeWithCandidate = volume.AvailableFreeSpace;

            File.Copy(png, backup);
            Assert(HashEquals(backup, pngOriginalHash), "Independent PNG backup mismatch");
            Assert(ReadIdentity(backup).Id != ReadIdentity(png).Id,
                "Backup is a hardlink, not an independent copy");

            // Fault injection #1: interruption after backup, before commit.
            // Only the generated original exists. No replacement is allowed.
            try
            {
                throw new SyntheticInterruptionException("after backup, before commit");
            }
            catch (SyntheticInterruptionException)
            {
                Assert(HashEquals(png, pngOriginalHash) &&
                       HashEquals(backup, pngOriginalHash),
                       "Precommit interruption changed the original or backup");
            }

            long freeWithBackup = volume.AvailableFreeSpace;
            File.Replace(candidate, png, destinationBackupFileName: null);
            long freeAfterPngCommit = volume.AvailableFreeSpace;
            Assert(ReadPng(png).Scanlines.AsSpan().SequenceEqual(scanlines),
                "Lossless PNG synthetic commit changed pixels");
            Assert(ReadPng(png).Metadata.AsSpan().SequenceEqual(originalInfo.Metadata),
                "PNG commit changed non-IDAT metadata");

            // Fault injection #2: treat commit as interrupted; recover solely
            // from the independent backup. Never delete backup to claim savings.
            try
            {
                throw new SyntheticInterruptionException("after commit, before finalization");
            }
            catch (SyntheticInterruptionException)
            {
                File.Copy(backup, restoreStage);
                File.Replace(restoreStage, png, destinationBackupFileName: null);
                Assert(HashEquals(png, pngOriginalHash),
                    "Rollback did not restore the exact original PNG bytes");
                Assert(HashEquals(backup, pngOriginalHash),
                    "Rollback damaged the independent backup");
            }

            long freeAfterPngRollback = volume.AvailableFreeSpace;
            long allocatedAfterRollback = AllocatedSize(png);
            Assert(allocatedAfterRollback > 0, "Restored file lost allocated storage");
            Assert(ReadIdentity(backup).Id != ReadIdentity(png).Id,
                "Rollback unexpectedly linked original to backup");

            var result = new
            {
                schema = "chappy-r28-ntfs-synthetic-v1",
                environment = "GitHub Actions Windows NTFS synthetic sandbox",
                notes = "Only generated disposable data. Drive free-space deltas are noisy observational snapshots. Hardlinks do not preserve independent writes. A retained on-volume backup consumes capacity. No real user file recovery is implied.",
                ntfsClusterBytes = clusterBytes,
                duplicates = new
                {
                    originalFileBytes = DuplicateBytes,
                    independentAllocatedBeforeBytes = primaryAllocated + secondAllocated,
                    uniqueAllocatedAfterBytes = uniqueAfter,
                    uniqueFileIdAllocatedSavingsBytes = physicalSaved,
                    freeSpaceBeforeBytes = freeBeforeDedupe,
                    freeSpaceAfterBytes = freeAfterDedupe,
                    observedFreeSpaceDeltaBytes = freeAfterDedupe - freeBeforeDedupe,
                    bothNamesHaveIdenticalSha256 = true,
                    bothNamesShareNtfsIdentity = true
                },
                png = new
                {
                    originalBytes = pngOriginalLength,
                    candidateBytes = pngCandidateLength,
                    losslessCandidateLogicalSavingsBytes = pngOriginalLength - pngCandidateLength,
                    originalAllocatedBytes = pngOriginalAllocated,
                    candidateAllocatedBytes = candidateAllocated,
                    freeSpaceWithCandidateBytes = freeWithCandidate,
                    freeSpaceWithBackupBytes = freeWithBackup,
                    freeSpaceAfterSyntheticCommitBytes = freeAfterPngCommit,
                    freeSpaceAfterRollbackBytes = freeAfterPngRollback,
                    backupIndependent = true,
                    metadataAndScanlinesMatch = true,
                    faultBeforeCommitPreservedOriginal = true,
                    faultAfterCommitRolledBackExactly = true,
                    realWorldCapacityReclaimedBytes = (long?)null
                },
                productionMutationEnabled = false,
                syntheticTestsPassed = true
            };

            Directory.CreateDirectory("diagnostics");
            string reportPath = Path.Combine("diagnostics", "r28-ntfs-space.json");
            File.WriteAllText(reportPath, JsonSerializer.Serialize(result,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R28 NTFS SYNTHETIC MEASUREMENT PASS");
            Console.WriteLine("Duplicate unique-ID allocation saved: " +
                              physicalSaved + " bytes; noisy free-space delta: " +
                              (freeAfterDedupe - freeBeforeDedupe) + " bytes");
            Console.WriteLine("PNG candidate logical reduction: " +
                              (pngOriginalLength - pngCandidateLength) +
                              " bytes; exact rollback PASS");
            Console.WriteLine("Machine-readable report: " + reportPath);
        }
        finally
        {
            // The GUID directory did not exist before this test. All contents
            // were generated by this process; never accept an external path.
            Directory.Delete(sandbox, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool HashEquals(string path, ReadOnlySpan<byte> expected) =>
        SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(expected);

    private static long AllocatedSize(string path)
    {
        uint low = Native.GetCompressedFileSizeW(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == 0xffffffffu && error != 0)
            throw new Win32Exception(error, "Allocation query failed: " + Path.GetFileName(path));
        return checked((long)(((ulong)high << 32) | low));
    }

    private static (NtfsId Id, uint Links) ReadIdentity(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!Native.GetFileInformationByHandle(file.SafeFileHandle, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "File identity failed");
        return (new NtfsId(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow),
                info.NumberOfLinks);
    }

    private static byte[] SyntheticScanlines(int width, int height)
    {
        int stride = width * 4 + 1;
        var data = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride; // filter 0 (None) is already initialized.
            for (int x = 0; x < width; x++)
            {
                int at = row + 1 + x * 4;
                data[at] = (byte)(x & 7);
                data[at + 1] = (byte)(y & 7);
                data[at + 2] = (byte)((x + y) & 7);
                data[at + 3] = 255;
            }
        }
        return data;
    }

    private static byte[] MakePng(byte[] scanlines, int width, int height,
        CompressionLevel compression)
    {
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, compression, leaveOpen: true))
            z.Write(scanlines);

        using var output = new MemoryStream();
        output.Write(PngSignature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // 8-bit
        ihdr[9] = 6;  // RGBA
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "tEXt", Encoding.ASCII.GetBytes(
            "Comment\0Chappy-R28-test-only: no user media"));
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] payload)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        stream.Write(len);
        byte[] tag = Encoding.ASCII.GetBytes(type);
        stream.Write(tag);
        stream.Write(payload);
        var input = new byte[tag.Length + payload.Length];
        tag.CopyTo(input, 0);
        payload.CopyTo(input, tag.Length);
        BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(input));
        stream.Write(len);
    }

    private static (byte[] Scanlines, byte[] Metadata) ReadPng(string path)
    {
        using var input = File.OpenRead(path);
        var magic = new byte[8];
        input.ReadExactly(magic);
        Assert(magic.AsSpan().SequenceEqual(PngSignature), "Bad PNG signature");
        using var metadata = new MemoryStream();
        using var idat = new MemoryStream();
        bool ended = false;
        while (input.Position < input.Length)
        {
            var hdr = new byte[8];
            input.ReadExactly(hdr);
            int size = BinaryPrimitives.ReadInt32BigEndian(hdr.AsSpan(0, 4));
            if (size < 0 || size > 4_000_000) throw new InvalidDataException("Invalid test PNG chunk");
            var body = new byte[size];
            input.ReadExactly(body);
            var crc = new byte[4];
            input.ReadExactly(crc);
            var crcData = new byte[4 + size];
            hdr.AsSpan(4, 4).CopyTo(crcData);
            body.CopyTo(crcData, 4);
            Assert(BinaryPrimitives.ReadUInt32BigEndian(crc) == Crc32(crcData),
                "PNG chunk CRC mismatch");
            string tag = Encoding.ASCII.GetString(hdr, 4, 4);
            if (tag == "IDAT")
                idat.Write(body);
            else
            {
                metadata.Write(hdr);
                metadata.Write(body);
                metadata.Write(crc);
            }
            if (tag == "IEND") { ended = true; break; }
        }
        Assert(ended && input.Position == input.Length, "Missing or trailing PNG chunks");
        idat.Position = 0;
        using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        return (raw.ToArray(), metadata.ToArray());
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xffffffff;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = ((crc & 1) != 0) ? ((crc >> 1) ^ 0xedb88320) : (crc >> 1);
        }
        return ~crc;
    }

    private sealed class SyntheticInterruptionException(string reason) : Exception(reason);
    private readonly record struct NtfsId(uint Volume, uint High, uint Low);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInfo
    {
        internal uint Attributes;
        internal FILETIME CreationTime;
        internal FILETIME LastAccessTime;
        internal FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(
            string root, StringBuilder? volume, uint capacity, out uint serial,
            out uint maxComponent, out uint flags, StringBuilder fsName, uint fsCapacity);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceW(
            string root, out uint sectorsPerCluster, out uint bytesPerSector,
            out uint freeClusters, out uint totalClusters);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLinkW(
            string newName, string existingName, IntPtr attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        internal static extern uint GetCompressedFileSizeW(string fileName, out uint high);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out NativeFileInfo information);
    }
}
