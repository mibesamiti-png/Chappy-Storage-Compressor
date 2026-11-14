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
        return new Summary(png, jpeg, pngBytes, jpegBytes, special, errors, free);
    }
}
