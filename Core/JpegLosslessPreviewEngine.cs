using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// jpegtran -optimize -copy all PREVIEW ONLY. Does not delete/rename/replace
/// original images, even on cancellation or a validation failure.
/// The independent WIC RGB comparison is required before a candidate is kept.
/// </summary>
public static class JpegLosslessPreviewEngine
{
    public sealed record Result(string Source, string? Candidate, long SourceBytes,
        long CandidateBytes, string Explanation)
    {
        public long EstimatedSavingBytes => Candidate is null ? 0 : SourceBytes - CandidateBytes;
    }

    // G4-02: do not start a user-selected executable based on its NAME.
    // The ordinary Windows application embeds the SHA-256 of the official
    // source-built jpegtran in its own assembly at package-build time.
    // A missing/invalid build pin fails closed (no arbitrary local EXEs).
    // The read-only handle stays open during Process.Start/WaitForExit to
    // deny normal write, rename and replacement races after hashing.
    internal static FileStream OpenTrustedTool(string executable,
        CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("jpegtranはWindows専用です");
        ct.ThrowIfCancellationRequested();
        string tool = Path.GetFullPath(executable);
        if (!Path.GetFileName(tool).Equals("jpegtran.exe",
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(tool) || IsReparse(tool) ||
            !Safety.EligibleAttributes(File.GetAttributes(tool)) ||
            NativeFile.HasNamedDataStreams(tool))
            throw new InvalidOperationException("未確認・特殊属性のjpegtran実行ファイルは拒否");
        FileStream? held = null;
        try
        {
            held = new FileStream(tool, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            if (held.Length is < 1024 or > 128 * 1024 * 1024)
                throw new InvalidDataException("jpegtran実行ファイルのサイズが不正");
            FileIdentity before = NativeFile.GetIdentity(tool);
            if (before.LinkCount != 1)
                throw new InvalidDataException("外部共有ハードリンクのjpegtranは拒否");
            string actual = Convert.ToHexString(SHA256.HashData(held));
            FileIdentity after = NativeFile.GetIdentity(tool);
            if (!before.SameFile(after) || after.LinkCount != 1 ||
                held.Length <= 0 || NativeFile.HasNamedDataStreams(tool))
                throw new InvalidDataException("検証中にjpegtranのID・属性が変化");
            string? embedded = typeof(JpegLosslessPreviewEngine).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(x => x.Key == "Chappy.TrustedJpegtranSha256")?.Value;
            bool trusted = embedded is { Length: 64 } &&
                embedded.All(Uri.IsHexDigit) &&
                actual.Equals(embedded, StringComparison.OrdinalIgnoreCase);
#if CHAPPY_TESTING
            // Only the isolated, test-compiled Core accepts CI's two EXEs
            // from a verified exact upstream Git commit. These environment
            // variables cannot authorize anything in the normal app binary.
            if (!trusted &&
                Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") ==
                    "7b4993786d856cbccabfc0ed2f923ba000e326cc")
            {
                static bool CiMatch(string exe, string digest,
                    string? expectedExe, string? expectedDigest) =>
                    !string.IsNullOrEmpty(expectedExe) &&
                    expectedDigest is { Length: 64 } &&
                    expectedDigest.All(Uri.IsHexDigit) &&
                    exe.Equals(Path.GetFullPath(expectedExe),
                        StringComparison.OrdinalIgnoreCase) &&
                    digest.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase);
                trusted = CiMatch(tool, actual,
                    Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN"),
                    Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_SHA256")) ||
                    CiMatch(tool, actual,
                    Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN"),
                    Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_SHA256"));
            }
#endif
            if (!trusted)
                throw new InvalidOperationException(
                    "このjpegtran.exeのSHA-256は正規配布ビルドに固定された値と一致しません。" +
                    "未知の実行ファイルは起動しません。");
            ct.ThrowIfCancellationRequested();
            return held;
        }
        catch
        {
            held?.Dispose();
            throw;
        }
    }

    public static Result CreatePreview(string sourcePath, string outputDirectory,
        string jpegtranPath, CancellationToken ct = default
#if CHAPPY_TESTING
        , Action<int>? testOnlyNativeProcessId = null
#endif
        )
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows専用です。");
        string source = Path.GetFullPath(sourcePath);
        string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        string tool = Path.GetFullPath(jpegtranPath);
        if (!new[] { ".jpg", ".jpeg" }.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("JPEG形式のみ対応しています。");
        if (!Directory.Exists(output) || !File.Exists(tool) ||
            !string.Equals(Path.GetExtension(tool), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("出力先またはjpegtran.exeが見つかりません。");
        if (IsReparse(source) || IsReparse(output) || IsReparse(tool))
            throw new InvalidOperationException("リンク・ジャンクション経由は対象外です。");
        // Keep the validated tool handle alive through the actual native
        // execution. The basename is NEVER sufficient to grant execution.
        using var pinnedExecutable = OpenTrustedTool(tool, ct);
        if (IsInside(output, Path.GetDirectoryName(source)!))
            throw new InvalidOperationException("候補出力は原本フォルダの外を選んでください。");
        using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        long sourceSize = sourceLock.Length;
        if (sourceSize > 512L * 1024 * 1024)
            return new(source, null, sourceSize, 0, "512MiBを超えるJPEGはR1.8では保留します。");
        var sourceMarkers = JpegMarkerAudit.Inspect(source, ct);
        if ((long)sourceMarkers.Width * sourceMarkers.Height > 48_000_000)
            return new(source, null, sourceSize, 0, "48MPを超えるJPEGはメモリ保護のため保留します。");
        var drive = new DriveInfo(Path.GetPathRoot(output)!);
        if (!drive.IsReady || drive.AvailableFreeSpace < checked(sourceSize + 16L * 1024 * 1024))
            return new(source, null, sourceSize, 0, "出力先に原本サイズ+16MiBの空きがありません。");
        string originalHash = Safety.ComputeSha256(source, ct);
        long originalWriteTicks = File.GetLastWriteTimeUtc(source).Ticks;
        string basename = Path.GetFileNameWithoutExtension(source);
        if (basename.Length > 50) basename = basename[..50];
        string candidate = Path.Combine(output, basename + ".chappy-jpeg-preview-" +
            Guid.NewGuid().ToString("N") + ".jpg");
        bool keep = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            var psi = new ProcessStartInfo
            {
                FileName = tool, UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = output,
            };
            psi.ArgumentList.Add("-copy");
            psi.ArgumentList.Add("all");
            psi.ArgumentList.Add("-optimize");
            psi.ArgumentList.Add("-outfile");
            psi.ArgumentList.Add(candidate);
            psi.ArgumentList.Add(source);
            using (var process = Process.Start(psi) ?? throw new IOException("jpegtran.exeを起動できません."))
            {
                try
                {
#if CHAPPY_TESTING
                    // The test-only post-Start PID hook must stay INSIDE the
                    // cancellation cleanup guard: a test can cancel as soon
                    // as a real child exists, before WaitForExitAsync begins.
                    // Production binaries do not compile this callback.
                    testOnlyNativeProcessId?.Invoke(process.Id);
#endif
                    ct.ThrowIfCancellationRequested();
                    process.WaitForExitAsync(ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    throw;
                }
                if (process.ExitCode != 0)
                    throw new InvalidDataException("jpegtran.exeが異常終了しました: " + process.ExitCode);
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(candidate) || IsReparse(candidate))
                throw new InvalidDataException("jpegtran候補が見つからないかリンクです。");
            long compressed = new FileInfo(candidate).Length;
            if (compressed >= sourceSize)
                return new(source, null, sourceSize, compressed, "容量が減らないため候補は採用しません。");
            var optimizedMarkers = JpegMarkerAudit.Inspect(candidate, ct);
            if (sourceMarkers != optimizedMarkers)
                throw new InvalidDataException("メタデータ・量子化表・画像ヘッダが一致しません。");
            if (!EquivalentDecodedPixels(source, candidate, ct))
                throw new InvalidDataException("復号したRGB画素が完全一致しません。");
            if (File.GetLastWriteTimeUtc(source).Ticks != originalWriteTicks ||
                !string.Equals(Safety.ComputeSha256(source, ct), originalHash, StringComparison.Ordinal))
                throw new InvalidDataException("圧縮候補生成中に原本が変化しました。");
            keep = true;
            return new(source, candidate, sourceSize, compressed,
                "検証済み候補。元ファイル未変更・実際の容量回復はまだありません。");
        }
        finally
        {
            if (!keep && File.Exists(candidate))
            {
                try { File.Delete(candidate); } catch { /* preserve evidence if delete fails */ }
            }
        }
    }

    /// <summary>Exact WIC RGB output comparison in short scanline blocks.</summary>
    public static bool EquivalentDecodedPixels(string left, string right, CancellationToken ct = default)
    {
        using var a = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var b = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fa = BitmapDecoder.Create(a, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var fb = BitmapDecoder.Create(b, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (fa.Frames.Count != 1 || fb.Frames.Count != 1) return false;
        var aa = new FormatConvertedBitmap(fa.Frames[0], PixelFormats.Bgr24, null, 0);
        var bb = new FormatConvertedBitmap(fb.Frames[0], PixelFormats.Bgr24, null, 0);
        if (aa.PixelWidth != bb.PixelWidth || aa.PixelHeight != bb.PixelHeight) return false;
        if ((long)aa.PixelWidth * aa.PixelHeight > 48_000_000) return false;
        int stride = checked(aa.PixelWidth * 3);
        int rows = Math.Max(1, Math.Min(32, (8 * 1024 * 1024) / stride));
        byte[] leftPixels = new byte[checked(stride * rows)];
        byte[] rightPixels = new byte[leftPixels.Length];
        for (int y = 0; y < aa.PixelHeight; y += rows)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(rows, aa.PixelHeight - y);
            var rect = new Int32Rect(0, y, aa.PixelWidth, count);
            aa.CopyPixels(rect, leftPixels, stride, 0);
            bb.CopyPixels(rect, rightPixels, stride, 0);
            if (!leftPixels.AsSpan(0, stride * count).SequenceEqual(rightPixels.AsSpan(0, stride * count)))
                return false;
        }
        return true;
    }

    private static bool IsInside(string path, string parent)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsReparse(string path)
    {
        for (string? p = path; !string.IsNullOrWhiteSpace(p); p = Path.GetDirectoryName(p))
        {
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
            string? parent = Path.GetDirectoryName(p);
            if (parent is null || string.Equals(parent, p, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }
}
