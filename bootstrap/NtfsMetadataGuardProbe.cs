using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// R2.11: SECURITY METADATA preflight in generated GitHub Actions NTFS files ONLY.
// This is deliberately isolated from the product; it never updates real media.
internal static class Program
{
    private const string Prefix = "Chappy-R211-";
    private const string Sentinel = ".r211-generated-only";
    private const string Source = "original.bin";
    private const string Stage = "stage.bin";
    private const int FixtureBytes = 256 * 1024;
    private static readonly string[] GuardNames =
    [
        "target-acl-changed", "stage-acl-changed", "target-ads-added",
        "stage-file-id-swapped", "stage-hardlink-added", "target-read-only",
        "target-exclusive-lock"
    ];
    private sealed record Fingerprint(
        string Sha256, long Length, uint Volume, ulong FileIndex, uint Links,
        string OwnerAndDaclSddl, int Attributes, long CreatedTicks, long WrittenTicks);

    private readonly record struct FileIdentity(uint Volume, ulong Index, uint Links);
    private sealed record Fixture(string Nonce, string Path, string OriginalHash,
        Fingerprint Original, Fingerprint Stage);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new ArgumentException("No external paths are accepted");
            RequireWindowsRunner();
            Run();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("R211 NTFS METADATA PREFLIGHT FAILED: " + e);
            return 1;
        }
    }

    private static void Require(bool value, string error)
    {
        if (!value) throw new InvalidOperationException(error);
    }

    private static string RunnerTemp()
    {
        string path = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidOperationException("RUNNER_TEMP required"))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(path)) throw new IOException("Runner temp absent");
        for (string? dir = path; dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runner TEMP contains a reparse-point ancestor");
        }
        return path;
    }

    private static void RequireWindowsRunner()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new PlatformNotSupportedException("R2.11 only runs in disposable Windows CI");
        string root = Path.GetPathRoot(RunnerTemp())
            ?? throw new IOException("Runner NTFS volume missing");
        var name = new StringBuilder(64);
        if (!Native.GetVolumeInformationW(root, null, 0, out _, out _, out _,
                name, (uint)name.Capacity))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!name.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("R2.11 requires NTFS");
    }

    private static string Sandbox(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out Guid parsed) ||
            parsed.ToString("N") != nonce)
            throw new IOException("Invalid synthetic sandbox ID");
        string path = Path.Combine(RunnerTemp(), Prefix + nonce);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) != RunnerTemp() ||
            !Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unsafe synthetic sandbox");
        string marker = Path.Combine(path, Sentinel);
        if (!File.Exists(marker) || File.ReadAllText(marker) != "CHAPPY-R211:" + nonce ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Synthetic sentinel invalid");
        return path;
    }

    private static string FileAt(string dir, string filename) => Path.Combine(dir, filename);

    private static FileIdentity Identity(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!Native.GetFileInformationByHandle(file.SafeFileHandle, out NativeFileInfo info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "NTFS identity unavailable");
        return new FileIdentity(info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    private static void RequireSafePlainFile(string path)
    {
        var attr = File.GetAttributes(path);
        if ((attr & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                     FileAttributes.ReadOnly | FileAttributes.System |
                     FileAttributes.Offline)) != 0)
            throw new IOException("Unsafe source attributes");
        if (Identity(path).Links != 1) throw new IOException("Unexpected hardlink count");
        using var handle = Native.FindFirstStreamW(path, 0, out StreamData data, 0);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "NTFS stream query failed");
        do
        {
            if (!string.Equals(data.Name, "::$DATA", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Named stream found: refuse unsafe replace");
        } while (Native.FindNextStreamW(handle, out data));
        if (Marshal.GetLastPInvokeError() != 38)
            throw new IOException("Named stream enumeration incomplete");
    }

    // Security descriptor is kept in memory. Do not emit user/runner SID or ACL
    // into the machine-readable artifact.
    private static string OwnerDaclSddl(string path)
    {
        const uint ownerAndDacl = 0x1 | 0x4;
        uint status = Native.GetNamedSecurityInfoW(path, 1, ownerAndDacl,
            out _, out _, out _, out _, out IntPtr sd);
        if (status != 0) throw new Win32Exception((int)status, "Security descriptor query failed");
        try
        {
            if (sd == IntPtr.Zero) throw new IOException("Missing security descriptor");
            if (!Native.ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    sd, 1, ownerAndDacl, out IntPtr sddl, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "SDDL conversion failed");
            try
            {
                return Marshal.PtrToStringUni(sddl)
                    ?? throw new IOException("Null SDDL output");
            }
            finally { Native.LocalFree(sddl); }
        }
        finally { Native.LocalFree(sd); }
    }

    private static Fingerprint Capture(string path)
    {
        RequireSafePlainFile(path);
        FileIdentity identity = Identity(path);
        byte[] digest;
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            digest = SHA256.HashData(input);
        var info = new FileInfo(path);
        string sddl = OwnerDaclSddl(path);
        var result = new Fingerprint(
            Convert.ToHexString(digest), info.Length, identity.Volume,
            identity.Index, identity.Links, sddl, (int)info.Attributes,
            info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks);
        RequireSafePlainFile(path);
        Require(Identity(path) == identity, "File changed physical identity during capture");
        return result;
    }

    private static void Validate(string path, Fingerprint expected)
    {
        Fingerprint observed = Capture(path);
        if (observed != expected)
            throw new IOException("Metadata, file identity or SHA-256 changed");
    }

    private static void Preflight(Fixture fixture)
    {
        string dir = Sandbox(fixture.Nonce);
        string source = FileAt(dir, Source), stage = FileAt(dir, Stage);
        Validate(source, fixture.Original);
        Validate(stage, fixture.Stage);
        FileIdentity a = Identity(source), b = Identity(stage);
        Require(a.Volume == b.Volume && a.Index != b.Index,
            "Stage must be a separate physical NTFS file");
        Require(fixture.Original.Sha256 == fixture.Stage.Sha256,
            "Test stage bytes differ from original");
        Validate(source, fixture.Original);
        Validate(stage, fixture.Stage);
    }

    private static Fixture CreateFixture()
    {
        string nonce = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(RunnerTemp(), Prefix + nonce);
        Require(!File.Exists(dir) && !Directory.Exists(dir), "Sandbox name collision");
        Directory.CreateDirectory(dir);
        using (var marker = new FileStream(FileAt(dir, Sentinel), FileMode.CreateNew,
                   FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Encoding.ASCII.GetBytes("CHAPPY-R211:" + nonce);
            marker.Write(bytes);
            marker.Flush(true);
        }
        dir = Sandbox(nonce);
        byte[] data = RandomNumberGenerator.GetBytes(FixtureBytes);
        string original = FileAt(dir, Source), stage = FileAt(dir, Stage);
        File.WriteAllBytes(original, data);
        File.WriteAllBytes(stage, data);
        string hash = Convert.ToHexString(SHA256.HashData(data));
        Fingerprint originalSnapshot = Capture(original), stageSnapshot = Capture(stage);
        Require(originalSnapshot.Sha256 == hash && stageSnapshot.Sha256 == hash,
            "Synthetic fixture hash mismatch");
        var fixture = new Fixture(nonce, dir, hash, originalSnapshot, stageSnapshot);
        Preflight(fixture);
        return fixture;
    }

    private static void RunIcaclsGrant(string path)
    {
        var psi = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // SID is Windows' built-in Authenticated Users identity. Never use
        // the actual runner username. Only generated disposable files touched.
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("/grant");
        psi.ArgumentList.Add("*S-1-5-11:(R)");
        using var process = Process.Start(psi) ?? throw new IOException("icacls did not launch");
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("icacls ACL update timed out");
        }
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
            throw new IOException("icacls synthetic ACL update failed: " + output + error);
    }

    private static void MustReject(Fixture fixture, string name)
    {
        try { Preflight(fixture); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or Win32Exception or InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Unsafe condition not rejected: " + name);
    }

    private static void VerifyOriginalBytes(string path, string hash)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Require(Convert.ToHexString(SHA256.HashData(input)) == hash,
            "Protected original bytes changed unexpectedly");
    }

    private static void Cleanup(string nonce)
    {
        string dir = Sandbox(nonce);
        foreach (string item in Directory.GetFileSystemEntries(dir))
        {
            if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refuse cleanup through reparse point");
        }
        foreach (string file in Directory.GetFiles(dir))
        {
            var attr = File.GetAttributes(file);
            if ((attr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: true);
    }

    private static void Run()
    {
        int allowedClean = 0, refused = 0, securityChanges = 0, streamChanges = 0;
        var cases = new List<object>();
        foreach (string name in GuardNames)
        {
            Fixture fixture = CreateFixture();
            try
            {
                string original = FileAt(fixture.Path, Source);
                string stage = FileAt(fixture.Path, Stage);
                string oldSourceSddl = fixture.Original.OwnerAndDaclSddl;
                string oldStageSddl = fixture.Stage.OwnerAndDaclSddl;
                Preflight(fixture);
                allowedClean++;

                switch (name)
                {
                    case "target-acl-changed":
                        RunIcaclsGrant(original);
                        Require(OwnerDaclSddl(original) != oldSourceSddl,
                            "ACL alteration test did not change owner/DACL descriptor");
                        securityChanges++;
                        MustReject(fixture, name);
                        break;
                    case "stage-acl-changed":
                        RunIcaclsGrant(stage);
                        Require(OwnerDaclSddl(stage) != oldStageSddl,
                            "Stage ACL alteration did not change descriptor");
                        securityChanges++;
                        MustReject(fixture, name);
                        break;
                    case "target-ads-added":
                        string ads = original + ":R211MetadataMarker";
                        File.WriteAllText(ads, "R211 synthetic alternate stream");
                        streamChanges++;
                        MustReject(fixture, name);
                        Require(File.ReadAllText(ads) == "R211 synthetic alternate stream",
                            "Refusal unexpectedly discarded the named stream");
                        break;
                    case "stage-file-id-swapped":
                        string previous = FileAt(fixture.Path, "old-stage.bin");
                        File.Move(stage, previous);
                        File.Copy(previous, stage);
                        Require(Capture(stage).Sha256 == fixture.Stage.Sha256,
                            "Identity-swap input did not keep identical SHA");
                        Require(Identity(stage).Index != fixture.Stage.FileIndex,
                            "Same-hash swap did not change file identity");
                        MustReject(fixture, name);
                        break;
                    case "stage-hardlink-added":
                        string alias = FileAt(fixture.Path, "other-hardlink.bin");
                        if (!Native.CreateHardLinkW(alias, stage, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastPInvokeError());
                        MustReject(fixture, name);
                        break;
                    case "target-read-only":
                        File.SetAttributes(original, File.GetAttributes(original) |
                                                    FileAttributes.ReadOnly);
                        MustReject(fixture, name);
                        break;
                    case "target-exclusive-lock":
                        using (var writer = new FileStream(original, FileMode.Open,
                                   FileAccess.ReadWrite, FileShare.None))
                            MustReject(fixture, name);
                        break;
                    default: throw new InvalidOperationException("Unknown R2.11 guard");
                }

                // Under every unsafe condition the original data must remain
                // byte-for-byte unchanged. No candidate is ever committed.
                VerifyOriginalBytes(original, fixture.OriginalHash);
                refused++;
                cases.Add(new
                {
                    scenario = name,
                    baselineAccepted = true,
                    tamperedPreflightRejected = true,
                    originalDataUnchanged = true
                });
            }
            finally { Cleanup(fixture.Nonce); }
        }

        Require(allowedClean == 7 && refused == 7 && securityChanges == 2 &&
                streamChanges == 1, "R2.11 acceptance count mismatch");
        Directory.CreateDirectory("diagnostics");
        File.WriteAllText(Path.Combine("diagnostics", "r211-ntfs-metadata.json"),
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r211-ntfs-security-metadata-v1",
                scope = "GitHub Actions Windows NTFS generated TEMP fixtures only",
                baselineMetadataPasses = allowedClean,
                unsafeMetadataRefusals = refused,
                descriptorMutationDetected = securityChanges,
                namedStreamMutationDetected = streamChanges,
                snapshotsIncludeOwnerAndDacl = true,
                snapshotsIncludeIdentityAndSha256 = true,
                originalBytesPreservedInAllCases = true,
                normalApplicationMutationsEnabled = false,
                aclOwnerPreservationAfterCommitProved = false,
                originalMediaUsed = false,
                results = cases,
                limitations = "Read-only owner+DACL SDDL snapshots and fail-closed preflight, not actual preservation through application cutover; SACL/EFS/ACL reparse races and true power-loss unresolved.",
                result = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("R211 NTFS SECURITY METADATA GUARD PASS: " +
            allowedClean + " clean preflights, " + refused + " unsafe refusals, " +
            securityChanges + " DACL changes and " + streamChanges + " named stream injection");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInfo
    {
        public uint Attributes;
        public NativeFileTime Created;
        public NativeFileTime Accessed;
        public NativeFileTime Written;
        public uint VolumeSerialNumber;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Length;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    private sealed class StreamHandle : SafeHandle
    {
        public StreamHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => Native.FindClose(handle);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationW(string root, StringBuilder? volume,
            uint len, out uint serial, out uint maxComponent, out uint flags,
            StringBuilder fileSystem, uint capacity);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out NativeFileInfo information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        public static extern StreamHandle FindFirstStreamW(string name, int level,
            out StreamData data, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextStreamW(StreamHandle handle, out StreamData data);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(string alias, string existing,
            IntPtr security);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint GetNamedSecurityInfoW(string path, int type,
            uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl,
            out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
            IntPtr descriptor, uint revision, uint securityInfo,
            out IntPtr sddl, out uint length);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr LocalFree(IntPtr memory);
    }
}
