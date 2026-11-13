using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R2.0 read-only JPEG log diagnosis and explicit, backed-up tail repair.
/// Never touches originals, preview media or the planned jpegtran executable.
/// A malformed complete record or corruption preceding the final unterminated
/// record is NOT repairable here and must be examined manually.
/// </summary>
public static class JpegBatchJournalTools
{
    public sealed record TailResult(string State, int TailBytes, string Message);
    public sealed record RepairResult(string BackupPath, int RemovedTailBytes, bool AddedNewline);

    public static TailResult InspectTail(string inputRoot, string outputRoot)
    {
        using var state = Open(inputRoot, outputRoot);
        return state.Tail;
    }

    public static RepairResult RepairTail(string inputRoot, string outputRoot)
    {
        using var state = Open(inputRoot, outputRoot);
        if (state.Tail.State is not ("PartialTail" or "MissingNewline"))
            throw new InvalidOperationException("修復対象の末尾がありません。正常記録や中間破損は変更しません。");
        string original = state.JournalPath;
        if (!File.Exists(original)) throw new InvalidDataException("修復するJPGログがありません。");
        string backup = original + ".before-r20-" + Guid.NewGuid().ToString("N") + ".bak";
        string temp = original + ".repair-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // An exact, separate copy is retained for post-crash diagnostics.
            File.Copy(original, backup, overwrite: false);
            if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(state.Bytes))
                throw new InvalidDataException("バックアップ内容が修復前ログと一致しません。");
            if (!File.ReadAllBytes(original).AsSpan().SequenceEqual(state.Bytes))
                throw new InvalidDataException("ログが診断後に変更されました。");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                file.Write(state.Bytes.AsSpan(0, state.RetainedBytes));
                if (state.Tail.State == "MissingNewline") file.WriteByte((byte)'\n');
                file.Flush(flushToDisk: true);
            }
            byte[] repaired = File.ReadAllBytes(temp);
            var plan = JpegBatchPreviewEngine.LoadPlan(state.PlanPath);
            JpegBatchPreviewEngine.ReplayJournalBytes(repaired, plan);
            JpegBatchPreviewEngine.ValidatePlan(plan, state.Input, state.Output, plan.ToolPath);
            if (!File.ReadAllBytes(original).AsSpan().SequenceEqual(state.Bytes))
                throw new InvalidDataException("置換直前にログが変更されました。");
            File.Move(temp, original, overwrite: true);
            return new RepairResult(backup,
                state.Tail.State == "PartialTail" ? state.Tail.TailBytes : 0,
                state.Tail.State == "MissingNewline");
        }
        finally
        {
            if (File.Exists(temp)) { try { File.Delete(temp); } catch { } }
        }
    }

    private static OpenState Open(string inputRoot, string outputRoot)
    {
        string input = JpegBatchPreviewEngine.CanonicalFolder(inputRoot);
        string output = JpegBatchPreviewEngine.CanonicalFolder(outputRoot);
        JpegBatchPreviewEngine.ValidateFolders(input, output);
        JpegBatchPreviewEngine.ValidateWorkingPaths(output,
            Path.Combine(output, JpegBatchPreviewEngine.PreviewName));
        string planPath = Path.Combine(output, JpegBatchPreviewEngine.PlanName);
        string journalPath = Path.Combine(output, JpegBatchPreviewEngine.JournalName);
        if (!File.Exists(planPath))
            throw new FileNotFoundException("JPG処理計画がありません。", planPath);
        var exclusive = new FileStream(Path.Combine(output, JpegBatchPreviewEngine.LockName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var plan = JpegBatchPreviewEngine.LoadPlan(planPath);
            JpegBatchPreviewEngine.ValidatePlan(plan, input, output, plan.ToolPath);
            if (File.Exists(journalPath) && new FileInfo(journalPath).Length > JpegBatchPreviewEngine.MaxJournalBytes)
                throw new InvalidDataException("JPGログが128MiBを超えています。");
            byte[] bytes = File.Exists(journalPath) ? File.ReadAllBytes(journalPath) : Array.Empty<byte>();
            if (bytes.Length > JpegBatchPreviewEngine.MaxJournalBytes)
                throw new InvalidDataException("JPGログが128MiBを超えています。");
            var (tail, kept) = Examine(bytes, plan, input, output);
            return new OpenState(input, output, planPath, journalPath, bytes, kept, tail, exclusive);
        }
        catch { exclusive.Dispose(); throw; }
    }

    private static (TailResult, int) Examine(byte[] bytes, JpegBatchPreviewEngine.Plan plan,
        string input, string output)
    {
        if (bytes.Length == 0)
            return (new TailResult("Empty", 0, "JPGログは空です。"), 0);
        if (bytes[^1] == (byte)'\n')
        {
            JpegBatchPreviewEngine.ReplayJournalBytes(bytes, plan);
            JpegBatchPreviewEngine.ValidatePlan(plan, input, output, plan.ToolPath);
            return (new TailResult("Healthy", 0, "すべての完了レコードが正常です。"), bytes.Length);
        }

        int lastNewline = Array.LastIndexOf(bytes, (byte)'\n');
        int kept = lastNewline + 1;
        JpegBatchPreviewEngine.ReplayJournalBytes(bytes.AsSpan(0, kept), plan);
        JpegBatchPreviewEngine.ValidatePlan(plan, input, output, plan.ToolPath);
        var suffix = bytes.AsSpan(kept);
        if (suffix.Length > 256 * 1024)
            throw new InvalidDataException("JPGログ末尾の不完全行が長すぎます。手動確認が必要です。");
        // A syntactically valid final record must also pass the SAME semantic
        // validation as a normal resume. Do not discard a mismatched identity.
        try
        {
            var parsed = JsonSerializer.Deserialize<JpegBatchPreviewEngine.Entry>(suffix);
            if (parsed is null) throw new InvalidDataException("JPGログ末尾がnullです。手動確認が必要です。");
            byte[] withLf = new byte[bytes.Length + 1];
            bytes.CopyTo(withLf, 0);
            withLf[^1] = (byte)'\n';
            JpegBatchPreviewEngine.ReplayJournalBytes(withLf, plan);
            JpegBatchPreviewEngine.ValidatePlan(plan, input, output, plan.ToolPath);
            return (new TailResult("MissingNewline", 0,
                "最後のレコードは正常ですが改行がありません。完全バックアップ後に改行追加できます。"), bytes.Length);
        }
        catch (JsonException)
        {
            return (new TailResult("PartialTail", suffix.Length,
                "最後の未完了レコードのみ修復候補です。完全バックアップを残して末尾を除外できます。"), kept);
        }
    }

    private sealed class OpenState : IDisposable
    {
        public string Input { get; }
        public string Output { get; }
        public string PlanPath { get; }
        public string JournalPath { get; }
        public byte[] Bytes { get; }
        public int RetainedBytes { get; }
        public TailResult Tail { get; }
        private readonly FileStream _lease;
        public OpenState(string input, string output, string plan, string journal,
            byte[] bytes, int kept, TailResult tail, FileStream lease)
        {
            Input = input; Output = output; PlanPath = plan; JournalPath = journal;
            Bytes = bytes; RetainedBytes = kept; Tail = tail; _lease = lease;
        }
        public void Dispose() => _lease.Dispose();
    }
}
