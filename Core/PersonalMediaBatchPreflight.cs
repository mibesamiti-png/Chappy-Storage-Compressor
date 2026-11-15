using System.Security.Cryptography;
using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.14: read-only inventory for a unified PNG + JPG preview run.
/// Does not parse/decode candidate files or modify user inputs.
/// The individual format engines independently revalidate and journal files.
/// </summary>
public static class PersonalMediaBatchPreflight
{
    public sealed record Summary(
        int PngFiles, int JpegFiles, long PngSourceBytes, long JpegSourceBytes,
        int SkippedSpecialFiles, int ScanErrors, long OutputAvailableBytes)
    {
        // Metadata-only inventory fingerprint binds displayed consent to the
        // set of eligible relative paths, sizes and UTC modification ticks.
        // The engines still hash/validate actual source bytes independently.
        public string InventorySha256 { get; init; } = "";
        public long TotalSourceBytes => checked(PngSourceBytes + JpegSourceBytes);
        public int TotalFiles => checked(PngFiles + JpegFiles);
        // Output needs free space for separate preview candidates. This is an
        // indicative warning, not a required reservation or a savings claim.
        public bool OutputMayNeedMoreSpace =>
            TotalSourceBytes > OutputAvailableBytes;
        public long ActualFreedBytes => 0;
    }

    public static Summary Inspect(string inputRoot, string outputRoot,
        CancellationToken ct = default)
    {
        string input = Safety.ValidateRoot(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        JpegBatchPreviewEngine.ValidateFolders(input, output);
        JpegBatchPreviewEngine.ValidateWorkingPaths(output,
            Path.Combine(output, JpegBatchPreviewEngine.PreviewName));

        var drive = new DriveInfo(Path.GetPathRoot(output)!);
        if (!drive.IsReady) throw new IOException("候補保存先ドライブにアクセスできません。");
        long free = drive.AvailableFreeSpace;

        int png = 0, jpeg = 0, special = 0, errors = 0;
        long pngBytes = 0, jpegBytes = 0;
        var eligibleSnapshot = new List<string>();
        var pending = new Stack<string>();
        pending.Push(input);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var a = File.GetAttributes(child);
                        if ((a & (FileAttributes.ReparsePoint | FileAttributes.System |
                                  FileAttributes.Offline)) != 0) { special++; continue; }
                        pending.Push(child);
                    }
                    catch (IOException) { errors++; }
                    catch (UnauthorizedAccessException) { errors++; }
                }
            }
            catch (IOException) { errors++; }
            catch (UnauthorizedAccessException) { errors++; }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    string extension = Path.GetExtension(file);
                    bool isPng = extension.Equals(".png", StringComparison.OrdinalIgnoreCase);
                    bool isJpeg = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                  extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
                    if (!isPng && !isJpeg) continue;

                    try
                    {
                        var info = new FileInfo(file);
                        info.Refresh();
                        if (!info.Exists || info.Length <= 0 ||
                            !Safety.EligibleAttributes(info.Attributes))
                        { special++; continue; }

                        // Include path, size, UTC mtime and current attributes
                        // in the consent snapshot (no image bytes are read).
                        // Sort later to make directory enumeration order irrelevant.
                        eligibleSnapshot.Add(Path.GetRelativePath(input, file) + "\t" +
                            info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" +
                            info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\t" +
                            ((int)info.Attributes).ToString(System.Globalization.CultureInfo.InvariantCulture));
                        // Do not claim that a named-stream or other uncommon
                        // media file is eligible: the format engine will make
                        // its stricter independent decision at execution time.
                        // Preflight is deliberately a rough count only.
                        if (isPng)
                        {
                            png++;
                            pngBytes = checked(pngBytes + info.Length);
                            if (png > 100_000)
                                throw new InvalidOperationException(
                                    "PNGは100,000件以下になるようにフォルダを分けてください。");
                        }
                        else
                        {
                            jpeg++;
                            jpegBytes = checked(jpegBytes + info.Length);
                            if (jpeg > 100_000)
                                throw new InvalidOperationException(
                                    "JPG/JPEGは100,000件以下になるようにフォルダを分けてください。");
                        }
                    }
                    catch (IOException) { errors++; }
                    catch (UnauthorizedAccessException) { errors++; }
                }
            }
            catch (IOException) { errors++; }
            catch (UnauthorizedAccessException) { errors++; }
        }
        eligibleSnapshot.Sort(StringComparer.OrdinalIgnoreCase);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string row in eligibleSnapshot)
            digest.AppendData(Encoding.UTF8.GetBytes(row + "\n"));
        return new Summary(png, jpeg, pngBytes, jpegBytes, special, errors, free)
        {
            InventorySha256 = Convert.ToHexString(digest.GetHashAndReset())
        };
    }

    // The live free-space sample is informative, not part of the user's
    // approved source inventory. It can legitimately change between reads.
    // Source entries, sizes, paths, times and exclusion/error counts cannot.
    public static Summary VerifyAfterConfirmation(string inputRoot, string outputRoot,
        Summary displayed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(displayed);
        ct.ThrowIfCancellationRequested();
        Summary current = Inspect(inputRoot, outputRoot, ct);
        if (current.PngFiles != displayed.PngFiles ||
            current.JpegFiles != displayed.JpegFiles ||
            current.PngSourceBytes != displayed.PngSourceBytes ||
            current.JpegSourceBytes != displayed.JpegSourceBytes ||
            current.SkippedSpecialFiles != displayed.SkippedSpecialFiles ||
            current.ScanErrors != displayed.ScanErrors ||
            !StringComparer.Ordinal.Equals(current.InventorySha256, displayed.InventorySha256))
            throw new InvalidDataException(
                "確認画面の表示後に入力画像の一覧・サイズ・更新日時・調査状態が変わりました。もう一度確認してください。");
        ct.ThrowIfCancellationRequested();
        return current;
    }
}
