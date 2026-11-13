using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// R2.9: independent disposable crash-recovery harness. This program never
// loads the end-user WPF app and never accepts a user-selected file path.
internal static class Program
{
    private const string Prefix = "Chappy-R29-";
    private const string SentinelName = ".r29-generated-sandbox";
    private const string OriginalName = "original.bin";
    private const string CandidateName = "candidate.bin";
    private const string BackupName = "backup.bin";
    private const string JournalName = "transaction.json";
    private const string RestoreName = "restore-stage.bin";
    private const string CheckpointName = "kill-checkpoint.txt";
    private static readonly string[] KillPhases =
        ["after-backup", "after-journal", "after-commit", "after-restore-stage"];

    private sealed record Journal(
        string Nonce, string OriginalSha256, string CandidateSha256,
        long OriginalBytes, long CandidateBytes);

    private readonly record struct FileIdentity(uint Volume, ulong Index, uint Links);

    private static int Main(string[] args)
    {
        try
        {
            RequireCiNtfs();
            if (args.Length == 0) RunParent();
            else if (args.Length == 3 && args[0] == "--worker")
                RunWorker(args[1], args[2]);
            else throw new InvalidOperationException("Unsupported test arguments");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("R29 SYNTHETIC TEST FAILURE: " + ex);
            return 1;
        }
    }

    private static string TempRoot()
    {
        string path = Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidOperationException("RUNNER_TEMP is required");
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(path)) throw new IOException("Runner temp missing");
        for (string? item = path; item is not null; item = Path.GetDirectoryName(item))
        {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runner temp has a reparse-point ancestor");
        }
        return path;
    }

    private static void RequireCiNtfs()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new PlatformNotSupportedException("Only disposable Windows CI may run R2.9");
        string root = Path.GetPathRoot(TempRoot())
            ?? throw new InvalidOperationException("Runner volume missing");
        var fs = new StringBuilder(64);
        if (!Native.GetVolumeInformationW(root, null, 0, out _, out _, out _,
                fs, (uint)fs.Capacity))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!string.Equals(fs.ToString(), "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("NTFS runner volume required");
    }

    private static string SandboxPath(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out Guid id) ||
            !string.Equals(nonce, id.ToString("N"), StringComparison.Ordinal))
            throw new ArgumentException("Invalid generated sandbox identifier");
        string temp = TempRoot();
        string directory = Path.Combine(temp, Prefix + nonce);
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != temp)
            throw new IOException("Sandbox outside runner temp");
        if (!Directory.Exists(directory) ||
            (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Generated sandbox missing or a reparse point");
        string marker = Path.Combine(directory, SentinelName);
        if (!File.Exists(marker) ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(marker) != "CHAPPY-R29-SYNTHETIC:" + nonce)
            throw new IOException("Synthetic sandbox marker invalid");
        return directory;
    }

    private static string At(string dir, string name) => Path.Combine(dir, name);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string HashFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static FileIdentity Identity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!Native.GetFileInformationByHandle(stream.SafeFileHandle, out NativeFileInfo value))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Identity query failed");
        return new FileIdentity(value.VolumeSerialNumber,
            ((ulong)value.FileIndexHigh << 32) | value.FileIndexLow, value.NumberOfLinks);
    }

    private static void RequirePlainFile(string path)
    {
        var attr = File.GetAttributes(path);
        if ((attr & (FileAttributes.Directory | FileAttributes.ReparsePoint |
                     FileAttributes.ReadOnly | FileAttributes.System)) != 0)
            throw new IOException("Unsafe file attributes");
        var id = Identity(path);
        if (id.Links != 1) throw new IOException("Shared file must not be restored");
        using var namedStreams = Native.FindFirstStreamW(path, 0, out StreamData stream, 0);
        if (namedStreams.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Stream enumeration failed");
        do
        {
            if (!string.Equals(stream.Name, "::$DATA", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unexpected named stream on " + Path.GetFileName(path));
        } while (Native.FindNextStreamW(namedStreams, out stream));
        int error = Marshal.GetLastPInvokeError();
        if (error != 38) throw new Win32Exception(error, "Unexpected NTFS stream enumeration error");
    }

    private static string VerifiedHash(string path, string expected, long bytes)
    {
        RequirePlainFile(path);
        FileIdentity identity = Identity(path);
        if (new FileInfo(path).Length != bytes) throw new IOException("Unexpected file size");
        string observed = HashFile(path);
        if (observed != expected || Identity(path) != identity ||
            new FileInfo(path).Length != bytes)
            throw new IOException("File fingerprint changed");
        RequirePlainFile(path);
        return observed;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void SaveDurable(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void KillAt(string dir, string phase)
    {
        SaveDurable(At(dir, CheckpointName), phase);
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    private static void RunWorker(string phase, string nonce)
    {
        if (!KillPhases.Contains(phase, StringComparer.Ordinal))
            throw new ArgumentException("Invalid controlled crash phase");
        string dir = SandboxPath(nonce);
        string original = At(dir, OriginalName), candidate = At(dir, CandidateName);
        string backup = At(dir, BackupName), journalPath = At(dir, JournalName);
        RequirePlainFile(original);
        RequirePlainFile(candidate);
        byte[] originalBytes = File.ReadAllBytes(original);
        byte[] candidateBytes = File.ReadAllBytes(candidate);
        Require(originalBytes.Length == 1024 * 1024 &&
                candidateBytes.Length == 512 * 1024, "Unexpected generated fixture");
        var journal = new Journal(nonce, Hash(originalBytes), Hash(candidateBytes),
            originalBytes.Length, candidateBytes.Length);
        VerifiedHash(original, journal.OriginalSha256, journal.OriginalBytes);
        VerifiedHash(candidate, journal.CandidateSha256, journal.CandidateBytes);
        File.Copy(original, backup);
        Require(Identity(original) != Identity(backup),
            "Synthetic backup was not an independent file");
        VerifiedHash(backup, journal.OriginalSha256, journal.OriginalBytes);
        if (phase == "after-backup") KillAt(dir, phase);

        SaveDurable(journalPath, JsonSerializer.Serialize(journal));
        if (phase == "after-journal") KillAt(dir, phase);
        VerifiedHash(original, journal.OriginalSha256, journal.OriginalBytes);
        VerifiedHash(candidate, journal.CandidateSha256, journal.CandidateBytes);
        VerifiedHash(backup, journal.OriginalSha256, journal.OriginalBytes);
        File.Replace(candidate, original, destinationBackupFileName: null);
        if (phase == "after-commit") KillAt(dir, phase);

        // A second abrupt process termination while preparing the rollback.
        File.Copy(backup, At(dir, RestoreName));
        VerifiedHash(At(dir, RestoreName), journal.OriginalSha256, journal.OriginalBytes);
        KillAt(dir, phase);
    }

    private static bool Recover(string nonce, string expectedOriginal, string expectedCandidate)
    {
        string dir = SandboxPath(nonce);
        string original = At(dir, OriginalName), backup = At(dir, BackupName);
        string journalPath = At(dir, JournalName), stage = At(dir, RestoreName);
        if (!File.Exists(journalPath))
            throw new InvalidOperationException("No durable journal: refuse automatic replay");
        RequirePlainFile(journalPath);
        var jInfo = new FileInfo(journalPath);
        if (jInfo.Length <= 0 || jInfo.Length > 4096)
            throw new IOException("Untrusted journal length");
        Journal? journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(journalPath));
        if (journal is null || journal.Nonce != nonce ||
            journal.OriginalSha256 != expectedOriginal ||
            journal.CandidateSha256 != expectedCandidate ||
            journal.OriginalBytes != 1024 * 1024 ||
            journal.CandidateBytes != 512 * 1024)
            throw new IOException("Journal does not match expected synthetic transaction");

        VerifiedHash(backup, expectedOriginal, journal.OriginalBytes);
        Require(Identity(backup) != Identity(original), "Backup aliases original file");
        RequirePlainFile(original);
        long size = new FileInfo(original).Length;
        string observed = HashFile(original);
        if (observed == expectedOriginal && size == journal.OriginalBytes)
        {
            VerifiedHash(original, expectedOriginal, journal.OriginalBytes);
            return false; // crash before cutover, or second idempotent replay
        }
        if (observed != expectedCandidate || size != journal.CandidateBytes)
            throw new IOException("Unknown changed target: fail closed");
        VerifiedHash(original, expectedCandidate, journal.CandidateBytes);
        // A prior interrupted rollback may have left an already verified stage.
        if (File.Exists(stage) || Directory.Exists(stage))
            VerifiedHash(stage, expectedOriginal, journal.OriginalBytes);
        else
        {
            File.Copy(backup, stage);
            VerifiedHash(stage, expectedOriginal, journal.OriginalBytes);
        }
        VerifiedHash(backup, expectedOriginal, journal.OriginalBytes);
        VerifiedHash(original, expectedCandidate, journal.CandidateBytes);
        File.Replace(stage, original, destinationBackupFileName: null);
        VerifiedHash(original, expectedOriginal, journal.OriginalBytes);
        VerifiedHash(backup, expectedOriginal, journal.OriginalBytes);
        return true;
    }

    private static (string Nonce, string OriginalHash, string CandidateHash, byte[] Original) MakeFixture()
    {
        string nonce = Guid.NewGuid().ToString("N");
        string root = Path.Combine(TempRoot(), Prefix + nonce);
        Require(!File.Exists(root) && !Directory.Exists(root), "GUID sandbox collision");
        Directory.CreateDirectory(root);
        SaveDurable(At(root, SentinelName), "CHAPPY-R29-SYNTHETIC:" + nonce);
        root = SandboxPath(nonce);
        byte[] original = RandomNumberGenerator.GetBytes(1024 * 1024);
        byte[] candidate = RandomNumberGenerator.GetBytes(512 * 1024);
        File.WriteAllBytes(At(root, OriginalName), original);
        File.WriteAllBytes(At(root, CandidateName), candidate);
        Require(Identity(At(root, OriginalName)) != Identity(At(root, CandidateName)),
            "Generated files share file identity");
        return (nonce, Hash(original), Hash(candidate), original);
    }

    private static void ForceRealChildTermination(string nonce, string phase)
    {
        string assembly = typeof(Program).Assembly.Location;
        if (!File.Exists(assembly)) throw new IOException("R2.9 assembly cannot be launched");
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = false, RedirectStandardError = false
        };
        info.ArgumentList.Add(assembly);
        info.ArgumentList.Add("--worker");
        info.ArgumentList.Add(phase);
        info.ArgumentList.Add(nonce);
        using var child = Process.Start(info) ??
            throw new InvalidOperationException("Failed to start synthetic child");
        if (!child.WaitForExit(30_000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw new TimeoutException("Synthetic child failed to terminate");
        }
        Require(child.ExitCode != 0, "Child exited normally instead of being killed");
        string directory = SandboxPath(nonce);
        string checkpoint = At(directory, CheckpointName);
        Require(File.Exists(checkpoint) &&
                File.ReadAllText(checkpoint) == phase,
            "Child did not reach its forced-kill checkpoint");
    }

    private static void MustRefuse(Action action, string reason)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException ||
                                   ex is UnauthorizedAccessException ||
                                   ex is Win32Exception ||
                                   ex is InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException("Recovery accepted unsafe " + reason);
    }

    private static void RunParent()
    {
        var cases = new List<object>();
        int restores = 0, refusals = 0;
        foreach (string phase in KillPhases)
        {
            var fixture = MakeFixture();
            try
            {
                ForceRealChildTermination(fixture.Nonce, phase);
                string dir = SandboxPath(fixture.Nonce);
                string original = At(dir, OriginalName);
                if (phase == "after-backup")
                {
                    MustRefuse(() => Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash), "missing journal");
                    VerifiedHash(original, fixture.OriginalHash, 1024 * 1024);
                    refusals++;
                    cases.Add(new { phase, childKilled = true,
                        journalMissingAutoReplayRejected = true, exactOriginalPreserved = true });
                }
                else
                {
                    bool restored = Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash);
                    Require(restored == (phase != "after-journal"),
                        "Unexpected rollback state for " + phase);
                    VerifiedHash(original, fixture.OriginalHash, 1024 * 1024);
                    Require(!Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash), "Idempotent replay modified original");
                    if (restored) restores++;
                    cases.Add(new { phase, childKilled = true, restored,
                        exactOriginalRestored = true, idempotentReplay = true });
                }
            }
            finally { Cleanup(fixture.Nonce); }
        }

        foreach (string guard in new[] { "backup-tamper", "shared-link", "writer-lock",
                                         "named-stream", "read-only" })
        {
            var fixture = MakeFixture();
            try
            {
                ForceRealChildTermination(fixture.Nonce, "after-commit");
                string dir = SandboxPath(fixture.Nonce);
                string backup = At(dir, BackupName), original = At(dir, OriginalName);
                string alias = At(dir, "backup-alias.bin");
                if (guard == "backup-tamper")
                {
                    var corrupted = (byte[])fixture.Original.Clone();
                    corrupted[0] ^= 0x55;
                    File.WriteAllBytes(backup, corrupted);
                    MustRefuse(() => Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash), guard);
                    File.WriteAllBytes(backup, fixture.Original);
                }
                else if (guard == "shared-link")
                {
                    if (!Native.CreateHardLinkW(alias, backup, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    MustRefuse(() => Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash), guard);
                    File.Delete(alias);
                }
                else if (guard == "writer-lock")
                {
                    using (var writer = new FileStream(original, FileMode.Open,
                               FileAccess.ReadWrite, FileShare.None))
                        MustRefuse(() => Recover(fixture.Nonce, fixture.OriginalHash,
                            fixture.CandidateHash), guard);
                }
                else if (guard == "named-stream")
                {
                    string ads = original + ":R29Injected";
                    File.WriteAllText(ads, "Synthetic test metadata");
                    MustRefuse(() => Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash), guard);
                    File.Delete(ads);
                }
                else if (guard == "read-only")
                {
                    File.SetAttributes(original, File.GetAttributes(original) |
                                               FileAttributes.ReadOnly);
                    MustRefuse(() => Recover(fixture.Nonce, fixture.OriginalHash,
                        fixture.CandidateHash), guard);
                    File.SetAttributes(original, File.GetAttributes(original) &
                                               ~FileAttributes.ReadOnly);
                }
                VerifiedHash(original, fixture.CandidateHash, 512 * 1024);
                Require(Recover(fixture.Nonce, fixture.OriginalHash,
                    fixture.CandidateHash), "Guard repair did not permit exact recovery");
                VerifiedHash(original, fixture.OriginalHash, 1024 * 1024);
                restores++;
                refusals++;
                cases.Add(new { phase = guard, childKilled = true,
                    unsafeRecoveryRefused = true, subsequentExactRollback = true });
            }
            finally { Cleanup(fixture.Nonce); }
        }

        Directory.CreateDirectory("diagnostics");
        string output = Path.Combine("diagnostics", "r29-process-crash.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            schema = "chappy-r29-process-kill-v1",
            scope = "Synthetic generated temp NTFS files only; not OS power loss",
            source = "Independent isolated child process forcibly terminated with Process.Kill",
            cases, forcedKillCases = cases.Count, exactRestorations = restores,
            failClosedRejections = refusals, productionMutationEnabled = false,
            realMediaAffected = false, result = "PASS"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"R29 PROCESS KILL / RECOVERY PASS: {cases.Count} cases, " +
            $"{restores} exact restores, {refusals} fail-closed refusals");
    }

    private static void Cleanup(string nonce)
    {
        string dir = SandboxPath(nonce);
        // A generated fixture may be deliberately made read-only; the guards
        // always reset it before cleanup. Never traverse reparse-point entries.
        foreach (string entry in Directory.GetFileSystemEntries(dir))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected reparse point: fail closed; no recursive delete");
        Directory.Delete(dir, recursive: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInfo
    {
        internal uint Attributes;
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        internal long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        internal string Name;
    }

    private sealed class StreamHandle : SafeHandle
    {
        public StreamHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == new IntPtr(-1) || handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => Native.FindClose(handle);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(string root, StringBuilder? volume,
            uint cap, out uint serial, out uint maxComponent, out uint flags,
            StringBuilder fs, uint fsCap);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out NativeFileInfo info);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLinkW(string newName, string existingName,
            IntPtr attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern StreamHandle FindFirstStreamW(string path, int level,
            out StreamData data, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextStreamW(StreamHandle handle, out StreamData data);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr handle);
    }
}
