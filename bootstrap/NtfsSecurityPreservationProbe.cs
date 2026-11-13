using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// R2.12: test-only security metadata preservation during staged replacement.
// Runs only on CI-generated disposable NTFS fixtures. Not part of the WPF app.
internal static class Program
{
    private const string Prefix = "Chappy-R212-";
    private const string Marker = ".r212-synthetic-only";
    private const string Target = "original.bin";
    private const string Stage = "stage.bin";
    private const string Backup = "independent-original.bak";
    private const string RollbackStage = "rollback-stage.bin";
    private const string AdsName = ":R212-Injected";
    private const uint OwnerAndDacl = 0x0001 | 0x0004; // OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION

    private sealed record Identity(uint Volume, ulong Index, uint Links);
    private sealed record Snapshot(
        string Hash, long Length, Identity Id, string OwnerDaclSddl,
        long CreationTicks, long WriteTicks, FileAttributes Attributes);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new ArgumentException("R2.12 accepts no external paths");
            ValidateCiEnvironment();
            Run();
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("R212 SYNTHETIC OWNER/DACL PRESERVATION FAILED: " + e);
            return 1;
        }
    }

    private static string RunnerTemp()
    {
        string path = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new IOException("Missing RUNNER_TEMP"))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(path)) throw new IOException("Missing runner temp directory");
        for (string? parent = path; parent != null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runner temp includes a reparse ancestor");
        return path;
    }

    private static void ValidateCiEnvironment()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new PlatformNotSupportedException("Only Windows GitHub Actions NTFS is permitted");
        string volumeRoot = Path.GetPathRoot(RunnerTemp()) ?? throw new IOException("No NTFS volume");
        var fs = new StringBuilder(64);
        if (!Native.GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out _,
            fs, (uint)fs.Capacity))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (fs.ToString() != "NTFS") throw new IOException("Test requires NTFS");
    }

    private static string Sandbox(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out var parsed) ||
            parsed.ToString("N") != nonce)
            throw new IOException("Invalid sandbox nonce");
        string dir = Path.Combine(RunnerTemp(), Prefix + nonce);
        if (Path.GetDirectoryName(Path.GetFullPath(dir)) != RunnerTemp() ||
            !Directory.Exists(dir) ||
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Untrusted sandbox");
        string marker = Path.Combine(dir, Marker);
        if (!File.Exists(marker) ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(marker) != "CHAPPY-R212:" + nonce)
            throw new IOException("Missing synthetic marker");
        return dir;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static string At(string directory, string file) => Path.Combine(directory, file);

    private static Identity GetIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!Native.GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Identity query failed");
        return new Identity(info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.NumberOfLinks);
    }

    private static string Sddl(string path)
    {
        uint status = Native.GetNamedSecurityInfoW(path, 1, OwnerAndDacl,
            out _, out _, out _, out _, out IntPtr descriptor);
        if (status != 0) throw new Win32Exception((int)status, "Reading owner/DACL failed");
        try
        {
            if (!Native.ConvertSecurityDescriptorToStringSecurityDescriptorW(
                descriptor, 1, OwnerAndDacl, out IntPtr text, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Converting descriptor failed");
            try { return Marshal.PtrToStringUni(text) ?? throw new IOException("Null owner/DACL"); }
            finally { Native.LocalFree(text); }
        }
        finally { Native.LocalFree(descriptor); }
    }

    private static void CopyOwnerDacl(string from, string to)
    {
        // GetNamedSecurityInfo returns owner and DACL pointers owned by the
        // descriptor. Keep the descriptor alive until SetNamedSecurityInfo returns.
        uint status = Native.GetNamedSecurityInfoW(from, 1, OwnerAndDacl,
            out IntPtr owner, out _, out IntPtr dacl, out _, out IntPtr descriptor);
        if (status != 0) throw new Win32Exception((int)status, "Security source read failed");
        try
        {
            status = Native.SetNamedSecurityInfoW(to, 1, OwnerAndDacl,
                owner, IntPtr.Zero, dacl, IntPtr.Zero);
            if (status != 0) throw new Win32Exception((int)status,
                "Could not apply owner+DACL to independent stage");
        }
        finally { Native.LocalFree(descriptor); }
    }

    private static void RequireNoNamedStreams(string path)
    {
        using var stream = Native.FindFirstStreamW(path, 0, out StreamData data, 0);
        if (stream.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Enumerating streams failed");
        do
        {
            if (!data.Name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected NTFS alternate data stream");
        } while (Native.FindNextStreamW(stream, out data));
        if (Marshal.GetLastPInvokeError() != 38)
            throw new IOException("Incomplete NTFS stream enumeration");
    }

    private static Snapshot Capture(string path)
    {
        var attr = File.GetAttributes(path);
        if ((attr & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                     FileAttributes.ReadOnly | FileAttributes.System |
                     FileAttributes.Offline)) != 0)
            throw new IOException("Unsafe fixture file attributes");
        RequireNoNamedStreams(path);
        Identity id = GetIdentity(path);
        if (id.Links != 1) throw new IOException("Expected independent file ID");
        byte[] digest;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            digest = SHA256.HashData(stream);
        var info = new FileInfo(path);
        var snap = new Snapshot(Convert.ToHexString(digest), info.Length, id,
            Sddl(path), info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, info.Attributes);
        if (GetIdentity(path) != id) throw new IOException("Physical file swapped during snapshot");
        RequireNoNamedStreams(path);
        return snap;
    }

    private static void SameSnapshot(string path, Snapshot expected)
    {
        if (Capture(path) != expected)
            throw new IOException("Security descriptor, hash, identity or metadata changed");
    }

    private static void ChangeSyntheticDacl(string path, string sid, string access)
    {
        var info = new ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        info.ArgumentList.Add(path);
        info.ArgumentList.Add("/grant");
        info.ArgumentList.Add("*"+sid+":"+access);
        using var process = Process.Start(info) ?? throw new IOException("Could not run icacls");
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Synthetic icacls timed out");
        }
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0)
            throw new IOException("Synthetic icacls failed: " + output + error);
    }

    private static void EnsureCleanPair(string target, string stage,
        Snapshot expectedTarget, Snapshot expectedStage)
    {
        SameSnapshot(target, expectedTarget);
        SameSnapshot(stage, expectedStage);
        Require(expectedTarget.Id.Index != expectedStage.Id.Index &&
                expectedTarget.Id.Volume == expectedStage.Id.Volume,
                "Stage unexpectedly aliases the target");
        Require(expectedTarget.OwnerDaclSddl == expectedStage.OwnerDaclSddl,
                "Stage owner/DACL differs from target before cutover");
        SameSnapshot(target, expectedTarget);
        SameSnapshot(stage, expectedStage);
    }

    private static void MustRefuse(Action action, string scenario)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
            or UnauthorizedAccessException or Win32Exception)
        {
            return;
        }
        throw new InvalidOperationException("Failed to reject unsafe " + scenario);
    }

    private static string MakeSandbox()
    {
        string nonce = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(RunnerTemp(), Prefix + nonce);
        Require(!File.Exists(dir) && !Directory.Exists(dir), "Unexpected GUID collision");
        Directory.CreateDirectory(dir);
        using (var marker = new FileStream(At(dir, Marker), FileMode.CreateNew,
            FileAccess.Write, FileShare.None))
        {
            byte[] data = Encoding.ASCII.GetBytes("CHAPPY-R212:" + nonce);
            marker.Write(data);
            marker.Flush(true);
        }
        return Sandbox(nonce);
    }

    private static void DeleteSandbox(string directory)
    {
        string nonce = Path.GetFileName(directory)[Prefix.Length..];
        string dir = Sandbox(nonce);
        foreach (string path in Directory.GetFileSystemEntries(dir))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing cleanup through reparse point");
        Directory.Delete(dir, recursive: true);
    }

    private static (string Target, string Stage, string OriginalHash, string CandidateHash)
        CreatePair(string dir)
    {
        string target = At(dir, Target), stage = At(dir, Stage);
        byte[] original = RandomNumberGenerator.GetBytes(512 * 1024);
        byte[] candidate = RandomNumberGenerator.GetBytes(384 * 1024);
        File.WriteAllBytes(target, original);
        File.WriteAllBytes(stage, candidate);

        string targetBefore = Sddl(target), stageBefore = Sddl(stage);
        ChangeSyntheticDacl(target, "S-1-5-11", "(R)");
        Require(Sddl(target) != targetBefore, "Custom fixture DACL did not change");
        Require(Sddl(stage) == stageBefore,
            "Changing original unexpectedly changed the independent stage");

        // Without copying owner + DACL the stage is intentionally untrusted.
        Require(Sddl(stage) != Sddl(target), "Independent staging did not differ in ACL");
        CopyOwnerDacl(target, stage);
        Require(Sddl(target) == Sddl(stage), "Explicit owner/DACL copy did not match");
        File.SetCreationTimeUtc(stage, File.GetCreationTimeUtc(target));
        File.SetLastWriteTimeUtc(stage, File.GetLastWriteTimeUtc(target));

        return (target, stage, Convert.ToHexString(SHA256.HashData(original)),
            Convert.ToHexString(SHA256.HashData(candidate)));
    }

    private static void Run()
    {
        int preservedAfterCommit = 0, preservedAfterRollback = 0;
        int refusals = 0, aclMutationDetection = 0, adsMutationDetection = 0;
        long beforeBytes = 0, candidateBytes = 0;

        // Verified successful synthetic replacement + byte-exact recovery.
        {
            string dir = MakeSandbox();
            try
            {
                var (target, stage, originalHash, candidateHash) = CreatePair(dir);
                Snapshot targetBefore = Capture(target), stageBefore = Capture(stage);
                EnsureCleanPair(target, stage, targetBefore, stageBefore);
                beforeBytes = targetBefore.Length;
                candidateBytes = stageBefore.Length;
                Require(targetBefore.Hash == originalHash &&
                        stageBefore.Hash == candidateHash, "Unexpected test fixture");
                string backup = At(dir, Backup);
                File.Replace(stage, target, backup);
                Snapshot targetAfter = Capture(target);
                Snapshot backupAfter = Capture(backup);
                Require(targetAfter.Hash == candidateHash, "Staged synthetic candidate changed");
                Require(backupAfter.Hash == originalHash, "Independent backup bytes changed");
                Require(targetAfter.Id != backupAfter.Id &&
                        targetAfter.Id.Index != targetBefore.Id.Index,
                        "Replace did not establish independent NTFS file identity");
                Require(targetAfter.OwnerDaclSddl == targetBefore.OwnerDaclSddl,
                        "Owner/DACL changed during synthetic replacement");
                Require(targetAfter.CreationTicks == targetBefore.CreationTicks,
                        "Creation timestamp changed during synthetic replacement");
                Require(targetAfter.Attributes == targetBefore.Attributes,
                        "Ordinary file attributes changed during synthetic replacement");
                preservedAfterCommit++;

                string restore = At(dir, RollbackStage);
                File.Copy(backup, restore);
                CopyOwnerDacl(target, restore);
                File.SetCreationTimeUtc(restore,
                    new DateTime(targetBefore.CreationTicks, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(restore,
                    new DateTime(targetBefore.WriteTicks, DateTimeKind.Utc));
                Snapshot restoreBefore = Capture(restore);
                Require(restoreBefore.Hash == originalHash &&
                        restoreBefore.OwnerDaclSddl == targetBefore.OwnerDaclSddl,
                        "Independent restore stage is invalid");
                SameSnapshot(target, targetAfter);
                SameSnapshot(restore, restoreBefore);
                File.Replace(restore, target, null);
                Snapshot recovered = Capture(target);
                Require(recovered.Hash == originalHash &&
                        recovered.Length == targetBefore.Length &&
                        recovered.OwnerDaclSddl == targetBefore.OwnerDaclSddl &&
                        recovered.CreationTicks == targetBefore.CreationTicks,
                        "Rollback failed to recover exact data and security metadata");
                Require(Capture(backup).Hash == originalHash &&
                        GetIdentity(backup) != GetIdentity(target),
                        "Recovery backup lost its independent identity");
                preservedAfterRollback++;
            }
            finally { DeleteSandbox(dir); }
        }

        // Exactly one post-fingerprint modification per disposable fixture.
        foreach (string negative in new[] {
            "stage-dacl-tampered", "original-dacl-tampered",
            "stage-named-stream", "same-sha-new-stage-file-id", "new-hardlink-alias" })
        {
            string dir = MakeSandbox();
            try
            {
                var (target, stage, originalHash, _) = CreatePair(dir);
                Snapshot beforeTarget = Capture(target), beforeStage = Capture(stage);
                EnsureCleanPair(target, stage, beforeTarget, beforeStage);
                switch (negative)
                {
                    case "stage-dacl-tampered":
                        ChangeSyntheticDacl(stage, "S-1-5-32-545", "(RX)");
                        Require(Sddl(stage) != beforeStage.OwnerDaclSddl,
                            "Stage DACL tamper was not introduced");
                        aclMutationDetection++;
                        break;
                    case "original-dacl-tampered":
                        ChangeSyntheticDacl(target, "S-1-5-32-545", "(RX)");
                        Require(Sddl(target) != beforeTarget.OwnerDaclSddl,
                            "Target DACL tamper was not introduced");
                        aclMutationDetection++;
                        break;
                    case "stage-named-stream":
                        File.WriteAllText(stage + AdsName, "R2.12 fake named stream");
                        Require(File.ReadAllText(stage + AdsName) ==
                            "R2.12 fake named stream", "Named stream not created");
                        adsMutationDetection++;
                        break;
                    case "same-sha-new-stage-file-id":
                        string old = At(dir, "old-stage.bin");
                        File.Move(stage, old);
                        File.Copy(old, stage);
                        Require(Capture(stage).Hash == beforeStage.Hash &&
                                GetIdentity(stage).Index != beforeStage.Id.Index,
                                "Same-hash stage swap did not occur");
                        break;
                    case "new-hardlink-alias":
                        if (!Native.CreateHardLinkW(At(dir, "stage-alias.bin"), stage,
                            IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastPInvokeError());
                        break;
                    default: throw new InvalidOperationException("Unknown scenario");
                }

                MustRefuse(() => EnsureCleanPair(target, stage,
                    beforeTarget, beforeStage), negative);
                using (var input = new FileStream(target, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    Require(Convert.ToHexString(SHA256.HashData(input)) == originalHash,
                        "Unsafe preflight modified original data");
                refusals++;
            }
            finally { DeleteSandbox(dir); }
        }

        Require(preservedAfterCommit == 1 && preservedAfterRollback == 1 &&
                refusals == 5 && aclMutationDetection == 2 &&
                adsMutationDetection == 1, "Unexpected R2.12 evidence count");
        Directory.CreateDirectory("diagnostics");
        File.WriteAllText(Path.Combine("diagnostics", "r212-security-preservation.json"),
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r212-synthetic-security-preservation-v1",
                scope = "Windows GitHub Actions NTFS disposable synthetic files only",
                customDaclReproducedInStage = true,
                exactOwnerDaclAfterFileReplace = preservedAfterCommit == 1,
                exactOwnerDaclAfterRollback = preservedAfterRollback == 1,
                exactOriginalSha256AfterRollback = true,
                originalBytes = beforeBytes,
                candidateBytes,
                negativePreflightRefusals = refusals,
                negativeDaclAlterationsDetected = aclMutationDetection,
                negativeAdsChangesDetected = adsMutationDetection,
                powerLossResilienceProven = false,
                arbitraryAclAdsSaclPreservationProven = false,
                productionMutationEnabled = false,
                realUserMediaTouched = false,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(
            $"R212 NTFS OWNER/DACL PRESERVATION PASS: {preservedAfterCommit} replacement, " +
            $"{preservedAfterRollback} recovery, {refusals} preflight refusals");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDetails
    {
        public uint Attributes;
        public NativeFileTime Created;
        public NativeFileTime Accessed;
        public NativeFileTime Modified;
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
        public long Size;
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
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationW(
            string root, StringBuilder? label, uint labelCapacity, out uint serial,
            out uint maxComponent, out uint flags, StringBuilder fs, uint capacity);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out FileDetails details);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        public static extern StreamHandle FindFirstStreamW(string path, int level,
            out StreamData data, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextStreamW(StreamHandle stream, out StreamData data);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr pointer);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(string name, string existing,
            IntPtr security);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        public static extern uint GetNamedSecurityInfoW(
            string path, int objectType, uint flags,
            out IntPtr owner, out IntPtr group, out IntPtr dacl,
            out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        public static extern uint SetNamedSecurityInfoW(
            string path, int objectType, uint flags,
            IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
            IntPtr descriptor, uint revision, uint flags, out IntPtr text, out uint length);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr LocalFree(IntPtr pointer);
    }
}
