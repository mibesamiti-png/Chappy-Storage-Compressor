using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

// R2.10. Isolated synthetic-only hardlink-detach experiment.
// Never loaded by the WPF product. Input paths cannot be supplied by a user.
internal static class Program
{
    private const int Bytes = 4 * 1024 * 1024;
    private const string Prefix = "Chappy-R210-";
    private const string Marker = ".generated-only";
    private const string Master = "master.bin";
    private const string Alias = "alias.bin";
    private const string Stage = "independent-stage.bin";
    private const string JournalFile = "detach-journal.json";
    private const string Checkpoint = "kill-checkpoint.txt";
    private static readonly string[] KillPhases =
        ["after-stage", "after-journal", "after-cutover", "after-verify"];

    private sealed record Journal(
        string Nonce, string Sha256, long Length,
        uint Volume, ulong FileIndex, long CreationUtcTicks,
        long WriteUtcTicks, int Attributes);

    private readonly record struct Identity(uint Volume, ulong Index, uint Links);
    private sealed record Fixture(string Nonce, byte[] Bytes, string Sha);

    private static int Main(string[] args)
    {
        try
        {
            RequireCiNtfs();
            if (args.Length == 0) RunParent();
            else if (args.Length == 3 && args[0] == "--worker")
                RunWorker(args[1], args[2]);
            else throw new ArgumentException("Only internal synthetic worker mode is supported");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("R210 SYNTHETIC DETACH FAILURE: " + ex);
            return 1;
        }
    }

    private static string RunnerRoot()
    {
        string runner = Path.GetFullPath(
            Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidOperationException("RUNNER_TEMP missing"))
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(runner)) throw new IOException("RUNNER_TEMP missing");
        for (string? parent = runner; parent is not null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runner TEMP contains a reparse-point ancestor");
        return runner;
    }

    private static void RequireCiNtfs()
    {
        if (!OperatingSystem.IsWindows() ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new PlatformNotSupportedException("Only Windows GitHub Actions may run R2.10");
        string volume = Path.GetPathRoot(RunnerRoot())
            ?? throw new IOException("No TEMP volume");
        var name = new StringBuilder(64);
        if (!Native.GetVolumeInformationW(volume, null, 0, out _, out _, out _,
                name, (uint)name.Capacity))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!name.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Synthetic detach requires NTFS");
    }

    private static string PathFor(string nonce)
    {
        if (!Guid.TryParseExact(nonce, "N", out var parsed) ||
            parsed.ToString("N") != nonce)
            throw new IOException("Invalid synthetic nonce");
        string root = RunnerRoot();
        string dir = Path.Combine(root, Prefix + nonce);
        if (Path.GetDirectoryName(Path.GetFullPath(dir)) != root ||
            !Directory.Exists(dir) ||
            (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Untrusted synthetic directory");
        string marker = Path.Combine(dir, Marker);
        if (!File.Exists(marker) ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            File.ReadAllText(marker) != "R210-SYNTHETIC:" + nonce)
            throw new IOException("Missing generated fixture sentinel");
        return dir;
    }

    private static string At(string dir, string filename) => Path.Combine(dir, filename);

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string ShaFile(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static Identity GetIdentity(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!Native.GetFileInformationByHandle(stream.SafeFileHandle, out FileDetails result))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "File ID query failed");
        return new Identity(result.VolumeSerialNumber,
            ((ulong)result.FileIndexHigh << 32) | result.FileIndexLow, result.NumberOfLinks);
    }

    private static long Allocated(string path)
    {
        uint low = Native.GetCompressedFileSizeW(path, out uint high);
        int error = Marshal.GetLastPInvokeError();
        if (low == uint.MaxValue && error != 0)
            throw new Win32Exception(error, "NTFS allocated size query failed");
        return checked((long)(((ulong)high << 32) | low));
    }

    private static void RequirePlain(string path, uint expectedLinks)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory |
                           FileAttributes.ReadOnly | FileAttributes.System |
                           FileAttributes.Offline)) != 0)
            throw new IOException("Unsafe file attributes: " + Path.GetFileName(path));
        if (GetIdentity(path).Links != expectedLinks)
            throw new IOException("Unexpected hardlink count: " + Path.GetFileName(path));

        using var handle = Native.FindFirstStreamW(path, 0, out StreamData stream, 0);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not enumerate NTFS streams");
        do
        {
            if (!stream.Name.Equals("::$DATA", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Named NTFS stream present: " + Path.GetFileName(path));
        } while (Native.FindNextStreamW(handle, out stream));
        if (Marshal.GetLastPInvokeError() != 38)
            throw new IOException("NTFS stream enumeration did not finish normally");
    }

    private static void VerifyFile(string path, string hash, uint links = 1)
    {
        RequirePlain(path, links);
        Identity before = GetIdentity(path);
        if (new FileInfo(path).Length != Bytes || ShaFile(path) != hash)
            throw new IOException("Unexpected file bytes");
        if (GetIdentity(path) != before)
            throw new IOException("File identity changed during read");
        RequirePlain(path, links);
    }

    private static void Durable(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static Journal ExpectedJournal(Fixture fixture, string dir)
    {
        string master = At(dir, Master), alias = At(dir, Alias);
        RequirePlain(master, 2);
        RequirePlain(alias, 2);
        Identity old = GetIdentity(master);
        Assert(old == GetIdentity(alias), "Original alias does not share the master ID");
        VerifyFile(master, fixture.Sha, 2);
        VerifyFile(alias, fixture.Sha, 2);
        return new Journal(fixture.Nonce, fixture.Sha, Bytes, old.Volume, old.Index,
            File.GetCreationTimeUtc(alias).Ticks,
            File.GetLastWriteTimeUtc(alias).Ticks,
            (int)File.GetAttributes(alias));
    }

    private static Journal ReadJournal(string dir, string nonce, string expectedHash)
    {
        string path = At(dir, JournalFile);
        RequirePlain(path, 1);
        if (new FileInfo(path).Length is <= 0 or > 4096)
            throw new IOException("Untrusted synthetic journal size");
        Journal? journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path));
        if (journal is null || journal.Nonce != nonce ||
            journal.Sha256 != expectedHash || journal.Length != Bytes)
            throw new IOException("Untrusted journal identity/hash");
        return journal;
    }

    private static Identity ExpectedIdentity(Journal j) => new(j.Volume, j.FileIndex, 2);

    private static void ValidateShared(string dir, Journal journal)
    {
        string master = At(dir, Master), alias = At(dir, Alias);
        VerifyFile(master, journal.Sha256, 2);
        VerifyFile(alias, journal.Sha256, 2);
        Assert(GetIdentity(master) == ExpectedIdentity(journal) &&
               GetIdentity(alias) == ExpectedIdentity(journal),
               "Hardlinked source identity changed");
    }

    private static void PrepareStage(string dir, Journal journal)
    {
        ValidateShared(dir, journal);
        string stage = At(dir, Stage);
        if (File.Exists(stage) || Directory.Exists(stage))
            throw new IOException("Preexisting stage name is unsafe");
        File.Copy(At(dir, Alias), stage);
        File.SetCreationTimeUtc(stage, new DateTime(journal.CreationUtcTicks, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(stage, new DateTime(journal.WriteUtcTicks, DateTimeKind.Utc));
        VerifyFile(stage, journal.Sha256);
        Assert(GetIdentity(stage) != GetIdentity(At(dir, Alias)),
            "Stage must use independent file ID");
        ValidateShared(dir, journal);
    }

    private static void ValidateDetached(string dir, Journal j)
    {
        string master = At(dir, Master), alias = At(dir, Alias);
        VerifyFile(master, j.Sha256);
        VerifyFile(alias, j.Sha256);
        Identity m = GetIdentity(master), a = GetIdentity(alias);
        Assert(m.Volume == j.Volume && m.Index == j.FileIndex,
            "Master file identity changed during detach");
        Assert(a != m && a.Index != j.FileIndex,
            "Alias was not actually detached into an independent NTFS file");
        Assert(File.GetCreationTimeUtc(alias).Ticks == j.CreationUtcTicks,
            "Alias creation timestamp was not preserved");
        Assert(File.GetLastWriteTimeUtc(alias).Ticks == j.WriteUtcTicks,
            "Alias last-write timestamp was not preserved");
        Assert((int)File.GetAttributes(alias) == j.Attributes,
            "Alias attributes were not preserved");
    }

    private static bool Replay(string dir, string nonce, string hash)
    {
        Journal j = ReadJournal(dir, nonce, hash);
        Identity masterId = GetIdentity(At(dir, Master));
        Identity aliasId = GetIdentity(At(dir, Alias));
        if (masterId == ExpectedIdentity(j) && aliasId == ExpectedIdentity(j))
        {
            ValidateShared(dir, j);
            VerifyFile(At(dir, Stage), j.Sha256);
            ValidateShared(dir, j);
            File.Replace(At(dir, Stage), At(dir, Alias), null);
            ValidateDetached(dir, j);
            return true;
        }
        // A previous process may have exited just after replacing the alias.
        // Never overwrite it on replay: verify the two independent bytes/IDs.
        ValidateDetached(dir, j);
        return false;
    }

    private static void Kill(string dir, string phase)
    {
        Durable(At(dir, Checkpoint), phase);
        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }

    private static void RunWorker(string phase, string nonce)
    {
        if (!KillPhases.Contains(phase, StringComparer.Ordinal))
            throw new ArgumentException("Unknown kill phase");
        string dir = PathFor(nonce);
        byte[] bytes = File.ReadAllBytes(At(dir, Master));
        var fixture = new Fixture(nonce, bytes, Sha(bytes));
        Journal journal = ExpectedJournal(fixture, dir);
        PrepareStage(dir, journal);
        if (phase == "after-stage") Kill(dir, phase);
        Durable(At(dir, JournalFile), JsonSerializer.Serialize(journal));
        if (phase == "after-journal") Kill(dir, phase);
        Assert(Replay(dir, nonce, fixture.Sha), "Worker failed to create independent file");
        if (phase == "after-cutover") Kill(dir, phase);
        ValidateDetached(dir, journal);
        Kill(dir, phase);
    }

    private static Fixture MakeFixture()
    {
        string nonce = Guid.NewGuid().ToString("N");
        string dir = Path.Combine(RunnerRoot(), Prefix + nonce);
        Assert(!File.Exists(dir) && !Directory.Exists(dir), "GUID collision");
        Directory.CreateDirectory(dir);
        Durable(At(dir, Marker), "R210-SYNTHETIC:" + nonce);
        dir = PathFor(nonce);
        byte[] bytes = RandomNumberGenerator.GetBytes(Bytes);
        File.WriteAllBytes(At(dir, Master), bytes);
        if (!Native.CreateHardLinkW(At(dir, Alias), At(dir, Master), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Synthetic hardlink creation failed");
        Assert(GetIdentity(At(dir, Master)) == GetIdentity(At(dir, Alias)),
            "Generated pair did not share file ID");
        return new Fixture(nonce, bytes, Sha(bytes));
    }

    private static void RunKilledChild(Fixture fixture, string phase)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add(typeof(Program).Assembly.Location);
        psi.ArgumentList.Add("--worker");
        psi.ArgumentList.Add(phase);
        psi.ArgumentList.Add(fixture.Nonce);
        using var child = Process.Start(psi) ?? throw new IOException("Could not launch test child");
        if (!child.WaitForExit(30000))
        {
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            throw new TimeoutException("Child never exited");
        }
        string err = child.StandardError.ReadToEnd(), output = child.StandardOutput.ReadToEnd();
        string checkpoint = At(PathFor(fixture.Nonce), Checkpoint);
        if (child.ExitCode == 0 || !File.Exists(checkpoint) ||
            File.ReadAllText(checkpoint) != phase)
            throw new InvalidOperationException(
                $"Child did not kill itself at {phase}; exit={child.ExitCode}; out={output}; err={err}");
    }

    private static void MustRefuse(Action operation, string label)
    {
        try { operation(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or Win32Exception or JsonException)
        {
            return;
        }
        throw new InvalidOperationException("Unsafe detach unexpectedly accepted: " + label);
    }

    private static void ProveEditIndependence(string dir, Fixture fixture)
    {
        string alias = At(dir, Alias), master = At(dir, Master);
        VerifyFile(master, fixture.Sha);
        VerifyFile(alias, fixture.Sha);
        using (var output = new FileStream(alias, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            output.Position = 0;
            output.WriteByte((byte)(fixture.Bytes[0] ^ 0xA5));
            output.Flush(true);
        }
        Assert(ShaFile(master) == fixture.Sha,
            "Editing detached alias unexpectedly modified master");
        Assert(ShaFile(alias) != fixture.Sha,
            "Detached alias did not support independent editing");
    }

    private static long? RunOrdinaryDetach()
    {
        Fixture fixture = MakeFixture();
        try
        {
            string dir = PathFor(fixture.Nonce);
            var volume = new DriveInfo(Path.GetPathRoot(dir)!);
            long beforeFree = volume.AvailableFreeSpace;
            long sharedAllocated = Allocated(At(dir, Master));
            Journal j = ExpectedJournal(fixture, dir);
            PrepareStage(dir, j);
            Durable(At(dir, JournalFile), JsonSerializer.Serialize(j));
            Assert(Replay(dir, fixture.Nonce, fixture.Sha), "Ordinary detach did not commit");
            Assert(!Replay(dir, fixture.Nonce, fixture.Sha), "Second replay changed a completed detach");
            ValidateDetached(dir, j);
            long independentAllocated = Allocated(At(dir, Master)) + Allocated(At(dir, Alias));
            Assert(independentAllocated > sharedAllocated,
                "Undo failed to consume additional NTFS physical allocation");
            long afterFree = volume.AvailableFreeSpace;
            ProveEditIndependence(dir, fixture);
            return afterFree - beforeFree;
        }
        finally { Cleanup(fixture.Nonce); }
    }

    private static void RunParent()
    {
        long? freeSpaceDelta = RunOrdinaryDetach();
        var results = new List<object>();
        int completed = 1, refused = 0, kills = 0, independentEditChecks = 1;
        foreach (string phase in KillPhases)
        {
            Fixture fixture = MakeFixture();
            try
            {
                RunKilledChild(fixture, phase);
                kills++;
                string dir = PathFor(fixture.Nonce);
                if (phase == "after-stage")
                {
                    MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha),
                        "missing pre-journal recovery record");
                    Assert(GetIdentity(At(dir, Master)) == GetIdentity(At(dir, Alias)),
                        "Prejournal interruption lost a shared file name");
                    VerifyFile(At(dir, Master), fixture.Sha, 2);
                    refused++;
                    results.Add(new { phase, killed = true, journalMissing = true,
                        sharedNamesIntact = true });
                }
                else
                {
                    bool committed = Replay(dir, fixture.Nonce, fixture.Sha);
                    Assert(committed == (phase == "after-journal"),
                        "Replay outcome disagrees with kill phase");
                    Assert(!Replay(dir, fixture.Nonce, fixture.Sha), "Replay not idempotent");
                    ProveEditIndependence(dir, fixture);
                    independentEditChecks++;
                    completed++;
                    results.Add(new { phase, killed = true, detached = true,
                        verifiedExactBytes = true, editIndependence = true,
                        replayIdempotent = true });
                }
            }
            finally { Cleanup(fixture.Nonce); }
        }

        foreach (string guard in new[] {
            "stage-tamper", "extra-stage-hardlink", "writer-lock",
            "named-stream", "read-only", "shared-content-writer" })
        {
            Fixture fixture = MakeFixture();
            try
            {
                string dir = PathFor(fixture.Nonce);
                Journal j = ExpectedJournal(fixture, dir);
                PrepareStage(dir, j);
                Durable(At(dir, JournalFile), JsonSerializer.Serialize(j));
                string stage = At(dir, Stage), alias = At(dir, Alias);
                if (guard == "stage-tamper")
                {
                    byte[] altered = (byte[])fixture.Bytes.Clone();
                    altered[0] ^= 1;
                    File.WriteAllBytes(stage, altered);
                    MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha), guard);
                }
                else if (guard == "extra-stage-hardlink")
                {
                    string extra = At(dir, "extra-stage-name.bin");
                    if (!Native.CreateHardLinkW(extra, stage, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastPInvokeError());
                    MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha), guard);
                }
                else if (guard == "writer-lock")
                {
                    using (var writer = new FileStream(alias, FileMode.Open, FileAccess.ReadWrite,
                               FileShare.None))
                        MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha), guard);
                }
                else if (guard == "named-stream")
                {
                    File.WriteAllText(alias + ":R210Synthetic", "not personal media");
                    MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha), guard);
                }
                else if (guard == "read-only")
                {
                    File.SetAttributes(alias, File.GetAttributes(alias) | FileAttributes.ReadOnly);
                    MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha), guard);
                    File.SetAttributes(alias, File.GetAttributes(alias) & ~FileAttributes.ReadOnly);
                }
                else
                {
                    using (var writer = new FileStream(alias, FileMode.Open, FileAccess.Write,
                               FileShare.None))
                    {
                        writer.WriteByte((byte)(fixture.Bytes[0] ^ 0xFF));
                        writer.Flush(true);
                    }
                    // The writer is gone: this must fail on altered bytes,
                    // not merely because of an exclusive open handle.
                    MustRefuse(() => Replay(dir, fixture.Nonce, fixture.Sha), guard);
                }
                Assert(GetIdentity(At(dir, Master)) == GetIdentity(alias),
                    "Unsafe guard unexpectedly detached a shared name");
                refused++;
                results.Add(new { phase = guard, preventedUnsafeDetach = true });
            }
            finally { Cleanup(fixture.Nonce); }
        }

        Assert(kills == 4 && completed == 4 && refused == 7 &&
               independentEditChecks == 4 && results.Count == 10,
               "R2.10 expected evidence count mismatch");
        Directory.CreateDirectory("diagnostics");
        string output = At("diagnostics", "r210-detach.json");
        File.WriteAllText(output, JsonSerializer.Serialize(new {
            schema = "chappy-r210-ntfs-hardlink-detach-v1",
            context = "GitHub Windows NTFS generated disposable files only",
            originalFileBytes = Bytes,
            forcedProcessTerminationCheckpoints = kills,
            completeIndependentRestoreCases = completed,
            independentEditingValidatedCases = independentEditChecks,
            unsafeMutationRefusals = refused,
            snapshots = results,
            observedFreeSpaceDeltaForOrdinaryDetachBytes = freeSpaceDelta,
            note = "Hardlink detachment consumes physical disk space. Free-space snapshot includes test journal/NTFS overhead and is noisy. This does not enable end-user undo or prove ACL/ADS preservation, crash/power-loss consistency or release safety.",
            productionHardlinkMutationEnabled = false,
            productionOriginalReplacementEnabled = false,
            verdict = "PASS"
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"R210 SYNTHETIC HARDLINK DETACH PASS: {kills} real child kills, " +
            $"{completed} independent recoveries, {refused} refusals, " +
            $"{independentEditChecks} independent edit checks");
    }

    private static void Cleanup(string nonce)
    {
        string dir = PathFor(nonce);
        // The directory is created solely by the test with an unpredictable
        // nonce. Check for junctions before recursively deleting any fixture.
        foreach (string path in Directory.GetFileSystemEntries(dir))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected reparse point in generated sandbox");
        foreach (string path in Directory.GetFiles(dir))
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        Directory.Delete(dir, recursive: true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDetails
    {
        public uint Attributes;
        public NativeTime Created;
        public NativeTime Accessed;
        public NativeTime Modified;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
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
        public StreamHandle() : base(IntPtr.Zero, true) { }
        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
        protected override bool ReleaseHandle() => Native.FindClose(handle);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetVolumeInformationW(
            string root, StringBuilder? volumeName, uint capacity,
            out uint serial, out uint maxComponent, out uint flags,
            StringBuilder filesystem, uint fsCapacity);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out FileDetails details);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateHardLinkW(
            string newName, string existingName, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        public static extern uint GetCompressedFileSizeW(string path, out uint high);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        public static extern StreamHandle FindFirstStreamW(
            string path, int infoLevel, out StreamData data, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindNextStreamW(StreamHandle handle, out StreamData stream);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FindClose(IntPtr handle);
    }
}
