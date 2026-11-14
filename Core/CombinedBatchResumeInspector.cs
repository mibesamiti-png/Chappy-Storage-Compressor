using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.17 part 4: READ-ONLY inspection before the combined PNG/JPEG preview run.
/// It neither writes lock files nor repairs journals nor trusts an unfamiliar
/// output folder. The actual engines still repeat all their own validations.
/// </summary>
public static class CombinedBatchResumeInspector
{
    private const string PngPlan = ".chappy-png-batch-r16.json";
    private const string PngJournal = ".chappy-png-batch-r16.progress.jsonl";
    private const string PngLock = ".chappy-png-batch-r16.lock";
    private const string PngPreviews = "chappy-png-previews";
    private const string JpegPlan = ".chappy-jpeg-r19.plan.json";
    private const string JpegJournal = ".chappy-jpeg-r19.progress.jsonl";
    private const string JpegLock = ".chappy-jpeg-r19.lock";
    private const string JpegPreviews = "chappy-jpeg-r19-previews";
    private const long MaxPlanBytes = 128L * 1024 * 1024;
    private const long MaxJournalBytes = 128L * 1024 * 1024;
    private const int MaxPreviewEntries = 100_000;

    private static readonly HashSet<string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        PngPlan, PngJournal, PngLock, JpegPlan, JpegJournal, JpegLock
    };
    private static readonly HashSet<string> KnownDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        PngPreviews, JpegPreviews
    };

    public sealed record Summary(bool HasPngPlan, bool HasJpegPlan,
        int PngPlanEntries, int JpegPlanEntries)
    {
        public bool IsResume => HasPngPlan || HasJpegPlan;
        public string Description => IsResume
            ? $"既存記録を再開（PNG: {(HasPngPlan ? $"{PngPlanEntries:N0}件" : "新規")}" +
              $" / JPG: {(HasJpegPlan ? $"{JpegPlanEntries:N0}件" : "新規")}）"
            : "新規作成（空の候補保存先）";
    }

    public static Summary Inspect(string inputRoot, string outputRoot,
        string jpegtranPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string input = Safety.ValidateRoot(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        string tool = Path.GetFullPath(jpegtranPath);
        JpegBatchPreviewEngine.ValidateFolders(input, output);
        JpegBatchPreviewEngine.ValidateWorkingPaths(output, Path.Combine(output, JpegPreviews));
        // No tool invocation. The real engine independently verifies the chosen
        // jpegtran file at execution time.
        JpegBatchPreviewEngine.CheckNoReparse(tool);

        bool hasPng = File.Exists(Path.Combine(output, PngPlan));
        bool hasJpeg = File.Exists(Path.Combine(output, JpegPlan));

        // Reject unknown or user-created output content instead of silently
        // reusing a directory. All known members must have their correct type.
        int members = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(output))
        {
            ct.ThrowIfCancellationRequested();
            if (++members > 100_010)
                throw new InvalidDataException("保存先の直下に項目が多すぎます。別フォルダを選んでください。");
            string name = Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            if ((directory && !KnownDirectories.Contains(name)) ||
                (!directory && !KnownFiles.Contains(name)))
                throw new InvalidDataException("候補保存先に未確認のファイル/フォルダがあります: " + name);
            RejectUnsafePath(path, !directory);
        }

        bool pngLog = File.Exists(Path.Combine(output, PngJournal));
        bool jpegLog = File.Exists(Path.Combine(output, JpegJournal));
        bool pngDir = Directory.Exists(Path.Combine(output, PngPreviews));
        bool jpegDir = Directory.Exists(Path.Combine(output, JpegPreviews));
        if (!hasPng && (pngLog || pngDir || File.Exists(Path.Combine(output, PngLock))))
            throw new InvalidDataException("PNGの計画なしで作業ファイルが残っています。手動で確認してください。");
        if (!hasJpeg && (jpegLog || jpegDir || File.Exists(Path.Combine(output, JpegLock))))
            throw new InvalidDataException("JPGの計画なしで作業ファイルが残っています。手動で確認してください。");

        // Check descendants too: the program never removes foreign preview
        // files, but a non-generated name or a reparse point must never pass as
        // a safe, previously-owned output folder.
        if (pngDir) ValidatePreviewDirectory(Path.Combine(output, PngPreviews),
            ".chappy-preview-", ".png", ct);
        if (jpegDir) ValidatePreviewDirectory(Path.Combine(output, JpegPreviews),
            ".chappy-jpeg-preview-", ".jpg", ct);

        int pngCount = 0, jpegCount = 0;
        if (hasPng)
        {
            var png = JsonSerializer.Deserialize<PngBatchPreviewEngine.Plan>(
                ReadLimited(Path.Combine(output, PngPlan), MaxPlanBytes))
                ?? throw new InvalidDataException("PNGの作業計画がnullです。");
            PngBatchPreviewEngine.ValidatePlan(png, input, output,
                Path.Combine(output, PngPreviews));
            if (pngLog)
            {
                ApplyPngJournal(ReadLimited(Path.Combine(output, PngJournal), MaxJournalBytes),
                    png, input, output, ct);
                PngBatchPreviewEngine.ValidatePlan(png, input, output,
                    Path.Combine(output, PngPreviews));
            }
            pngCount = png.Entries.Count;
        }

        if (hasJpeg)
        {
            var jpeg = JsonSerializer.Deserialize<JpegBatchPreviewEngine.Plan>(
                ReadLimited(Path.Combine(output, JpegPlan), MaxPlanBytes))
                ?? throw new InvalidDataException("JPGの作業計画がnullです。");
            JpegBatchPreviewEngine.ValidatePlan(jpeg, input, output, tool);
            if (jpegLog)
            {
                JpegBatchPreviewEngine.ReplayJournalBytes(
                    ReadLimited(Path.Combine(output, JpegJournal), MaxJournalBytes), jpeg);
                JpegBatchPreviewEngine.ValidatePlan(jpeg, input, output, tool);
            }
            jpegCount = jpeg.Entries.Count;
        }
        ct.ThrowIfCancellationRequested();
        // Never create the output folder, lock, state file, or journal here.
        // A later engine Run must re-validate after the user's confirmation.
        return new Summary(hasPng, hasJpeg, pngCount, jpegCount);
    }

    private static byte[] ReadLimited(string path, long limit)
    {
        var file = new FileInfo(path);
        if (file.Length > limit)
            throw new InvalidDataException("作業記録が128MiBを超えているため再開しません。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.Length > limit)
            throw new InvalidDataException("作業記録が128MiBを超えているため再開しません。");
        byte[] buffer = new byte[checked((int)stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void ApplyPngJournal(byte[] bytes, PngBatchPreviewEngine.Plan plan,
        string input, string output, CancellationToken ct)
    {
        if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
            throw new InvalidDataException("PNG進捗ログの最終行が不完全です。");
        var dictionary = plan.Entries.ToDictionary(e => e.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        int start = 0;
        while (start < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            int offset = bytes.AsSpan(start).IndexOf((byte)'\n');
            if (offset < 0 || offset > 256 * 1024)
                throw new InvalidDataException("PNG進捗ログの1行が不正です。");
            var row = bytes.AsSpan(start, offset);
            start += offset + 1;
            if (row.Length == 0) continue;
            PngBatchPreviewEngine.Entry e;
            try { e = JsonSerializer.Deserialize<PngBatchPreviewEngine.Entry>(row)
                ?? throw new InvalidDataException("PNG進捗ログがnullです。"); }
            catch (JsonException ex)
            { throw new InvalidDataException("PNG進捗ログが破損しています。", ex); }
            if (string.IsNullOrWhiteSpace(e.RelativePath) ||
                !dictionary.TryGetValue(e.RelativePath, out var baseline) ||
                baseline.SourceBytes != e.SourceBytes ||
                baseline.ModifiedUtcTicks != e.ModifiedUtcTicks ||
                e.Status is not ("Pending" or "Completed" or "Skipped" or "Failed" or "Deferred") ||
                e.PreviewBytes < 0)
                throw new InvalidDataException("PNG進捗ログが作業計画と一致しません。");
            // Validate EACH historical record, even if later records look good.
            if (e.PreviewPath is not null && !Inside(e.PreviewPath,
                    Path.Combine(output, PngPreviews)))
                throw new InvalidDataException("PNGログの候補が出力階層の外です。");
            if (e.PreviewSha256 is not null &&
                (e.PreviewSha256.Length != 64 || !e.PreviewSha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("PNGログのハッシュが不正です。");
            if (e.Status == "Completed" && (e.PreviewPath is null ||
                e.PreviewSha256 is null || e.PreviewBytes >= e.SourceBytes))
                throw new InvalidDataException("PNGログの完了レコードが不正です。");
            baseline.Status = e.Status;
            baseline.PreviewPath = e.PreviewPath;
            baseline.PreviewBytes = e.PreviewBytes;
            baseline.PreviewSha256 = e.PreviewSha256;
            baseline.Message = e.Message;
        }
    }

    private static bool Inside(string path, string parent)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectUnsafePath(string path, bool isFile)
    {
        var attr = File.GetAttributes(path);
        if ((attr & (FileAttributes.ReparsePoint | FileAttributes.System |
                     FileAttributes.Offline | FileAttributes.Encrypted |
                     FileAttributes.SparseFile | FileAttributes.ReadOnly)) != 0)
            throw new InvalidDataException("特殊属性の作業ファイル/フォルダは拒否します: " +
                Path.GetFileName(path));
        JpegBatchPreviewEngine.CheckNoReparse(path);
        if (isFile && NativeFile.GetIdentity(path).LinkCount != 1)
            throw new InvalidDataException("作業ファイルが既存ハードリンクです。再開できません。");
    }

    private static void ValidatePreviewDirectory(string dir, string tag,
        string extension, CancellationToken ct)
    {
        int count = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(dir))
        {
            ct.ThrowIfCancellationRequested();
            if (++count > MaxPreviewEntries)
                throw new InvalidDataException("候補フォルダ内の項目が多すぎます。");
            if (Directory.Exists(path))
                throw new InvalidDataException("候補フォルダ内に未確認の下位フォルダがあります。");
            RejectUnsafePath(path, isFile: true);
            string name = Path.GetFileName(path);
            int marker = name.LastIndexOf(tag, StringComparison.OrdinalIgnoreCase);
            string suffix = marker < 0 ? "" : name[(marker + tag.Length)..];
            if (marker <= 0 ||
                !suffix.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(suffix[..^extension.Length], "N", out _))
                throw new InvalidDataException("候補フォルダ内に未確認のファイルがあります: " + name);
        }
    }
}
