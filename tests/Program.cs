using System.Security.Cryptography;
using ChappyStorageCompressor.Core;

static class Program
{
    private static int _checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

    private static void ExpectThrows(Action action, string message)
    {
        bool threw = false;
        try { action(); } catch { threw = true; }
        Check(threw, message);
    }

    private static PendingOperation Operation(string id, string original, string backup, string master, string hash) =>
        new() { OperationId = id, OriginalPath = original, BackupPath = backup,
            MasterPath = master, Sha256 = hash, RecordedUtc = DateTime.UtcNow };

    // R2.2: storage preflight formula tests; no user file modification.
    private static void TestR22Readiness()
    {
        const long reserve = ReplacementReadiness.MinimumReserveBytes;
        var good = ReplacementReadiness.EvaluateCapacity(100, 70,
            reserve + 70, reserve + 100, true);
        Check(good.MeetsSpaceRequirements, "R2.2: 容量境界ちょうどで事前条件合格");
        Check(!ReplacementReadiness.EvaluateCapacity(100, 100,
            reserve + 300, reserve + 300, true).MeetsSpaceRequirements,
            "R2.2: 圧縮できていない候補は除外");
        Check(!ReplacementReadiness.EvaluateCapacity(100, 101,
            reserve + 300, reserve + 300, true).MeetsSpaceRequirements,
            "R2.2: 候補の方が大きいと除外");
        Check(!ReplacementReadiness.EvaluateCapacity(100, 70,
            reserve + 69, reserve + 100, true).MeetsSpaceRequirements,
            "R2.2: 元ドライブの残量不足を検出");
        Check(!ReplacementReadiness.EvaluateCapacity(100, 70,
            reserve + 70, reserve + 99, true).MeetsSpaceRequirements,
            "R2.2: バックアップ先の残量不足を検出");
        Check(!ReplacementReadiness.EvaluateCapacity(100, 70,
            reserve + 70, reserve + 100, false).MeetsSpaceRequirements,
            "R2.2: 同一ボリュームへのバックアップを拒否");
        Check(!ReplacementReadiness.EvaluateCapacity(-1, 70,
            reserve + 70, reserve + 100, true).MeetsSpaceRequirements,
            "R2.2: 不正な元サイズを拒否");
        Check(!ReplacementReadiness.EvaluateCapacity(100, 70,
            -1, reserve + 100, true).MeetsSpaceRequirements,
            "R2.2: 負数の残容量を拒否");
        Check(!ReplacementReadiness.EvaluateCapacity(long.MaxValue, long.MaxValue - 1,
            long.MaxValue, long.MaxValue, true).MeetsSpaceRequirements,
            "R2.2: 巨大値でオーバーフローさせず拒否");
    }

    private static void TestPowerLoss(string testRoot, string checkpoint, byte[] expected)
    {
        string root = Path.Combine(testRoot, "fault-" + checkpoint);
        Directory.CreateDirectory(root);
        Journal.TestFolderPath = Path.Combine(root, "isolated-journal");
        try
        {
            string original = Path.Combine(root, "original.png");
            string duplicate = Path.Combine(root, "duplicate.png");
            File.WriteAllBytes(original, expected);
            File.WriteAllBytes(duplicate, expected);
            File.SetCreationTimeUtc(duplicate, File.GetCreationTimeUtc(original));
            File.SetLastWriteTimeUtc(duplicate, File.GetLastWriteTimeUtc(original));
            var scanned = Scanner.Run(root, true, null, CancellationToken.None);
            Check(scanned.Groups.Count == 1 && scanned.Groups[0].BlockReason(false) == null,
                checkpoint + ": 故障前の完全一致スキャン");
            var group = scanned.Groups[0];
            string target = group.Files[1].Path;
            string source = group.Files[0].Path;
            string master = Path.Combine(Safety.StorePath(root), group.Hash + ".png");
            bool triggered = false;
            FaultInjection.OnCheckpoint = name =>
            {
                if (name == checkpoint) throw new SimulatedPowerLossException(checkpoint);
            };
            try
            {
                DedupeEngine.Run(root, scanned.Groups, false, null, CancellationToken.None);
            }
            catch (SimulatedPowerLossException) { triggered = true; }
            finally { FaultInjection.OnCheckpoint = null; }
            Check(triggered, checkpoint + ": 指定操作直後に擬似強制終了");
            Check(Journal.Pending().Count == 1, checkpoint + ": 復旧記録を保持");
            var journal = Journal.Pending()[0];
            Check(File.Exists(master) && File.Exists(source), checkpoint + ": 代表データは残存");
            Check(Safety.ComputeSha256(source, CancellationToken.None) == group.Hash,
                checkpoint + ": 代表ファイルの内容は無変更");
            var inspect = RecoveryService.InspectAll();
            Check(inspect.Count == 1, checkpoint + ": 復旧ウィザードに1件表示");
            var wanted = checkpoint switch
            {
                "journal-recorded" or "backup-deleted" => RecoveryAction.ClearVerifiedJournal,
                "backup-renamed" => RecoveryAction.RestoreIndependentBackup,
                "new-link-created" or "pre-backup-delete" => RecoveryAction.UndoNewHardLink,
                _ => throw new InvalidOperationException("未知のチェックポイント")
            };
            Check(inspect[0].Action == wanted, checkpoint + ": 停止地点を安全に分類");
            string result = RecoveryService.Apply(journal.OperationId, wanted);
            Check(!string.IsNullOrWhiteSpace(result), checkpoint + ": 復旧操作の結果表示");
            Check(File.Exists(target), checkpoint + ": 元の名前にファイルがある");
            Check(!File.Exists(journal.BackupPath), checkpoint + ": 退避ファイルの孤立なし");
            Check(Safety.ComputeSha256(target, CancellationToken.None) == group.Hash,
                checkpoint + ": 復旧後のバイト列一致");
            if (wanted == RecoveryAction.UndoNewHardLink || wanted == RecoveryAction.RestoreIndependentBackup)
                Check(!NativeFile.SameFile(target, master),
                    checkpoint + ": 独立ファイルに復元されている");
            Check(Journal.Pending().Count == 0 && Journal.UnresolvedRecordFiles().Count == 0,
                checkpoint + ": 安全復旧後に記録が解決");
        }
        finally { Journal.TestFolderPath = null; }
    }

    private static void TestOrdinaryError(string testRoot, string checkpoint, byte[] expected)
    {
        string root = Path.Combine(testRoot, "exception-" + checkpoint);
        Directory.CreateDirectory(root);
        Journal.TestFolderPath = Path.Combine(root, "isolated-journal");
        try
        {
            string a = Path.Combine(root, "a.jpg");
            string b = Path.Combine(root, "b.jpg");
            File.WriteAllBytes(a, expected);
            File.WriteAllBytes(b, expected);
            File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
            File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
            var scanned = Scanner.Run(root, true, null, CancellationToken.None);
            Check(scanned.Groups.Count == 1, checkpoint + ": 通常例外の事前スキャン");
            string target = scanned.Groups[0].Files[1].Path;
            string source = scanned.Groups[0].Files[0].Path;
            FaultInjection.OnCheckpoint = name =>
            {
                if (name == checkpoint) throw new IOException("synthetic disk error at " + checkpoint);
            };
            ExpectThrows(() => DedupeEngine.Run(root, scanned.Groups, false, null,
                CancellationToken.None), checkpoint + ": 通常のI/O例外を発生");
            FaultInjection.OnCheckpoint = null;
            Check(File.Exists(target) && File.Exists(source), checkpoint + ": 例外後も元パスが存在");
            Check(Safety.ComputeSha256(target, CancellationToken.None) == scanned.Groups[0].Hash,
                checkpoint + ": 例外後も元内容が不変");
            Check(!NativeFile.SameFile(target, source), checkpoint + ": 例外のロールバックで独立性維持");
            var pending = Journal.Pending();
            Check(pending.Count == 1, checkpoint + ": 完了前の記録を保持");
            Journal.RecoverSafely();
            Check(Journal.Pending().Count == 0, checkpoint + ": 再検証後に記録を解消");
        }
        finally
        {
            FaultInjection.OnCheckpoint = null;
            Journal.TestFolderPath = null;
        }
    }

    private static void TestRecoveryRefusesAmbiguous(string testRoot, byte[] bytes)
    {
        string root = Path.Combine(testRoot, "recovery-ambiguous");
        Directory.CreateDirectory(root);
        Journal.TestFolderPath = Path.Combine(root, "isolated-journal");
        string original = Path.Combine(root, "photo.jpg");
        string source = Path.Combine(root, "source.jpg");
        string store = Safety.StorePath(root);
        Directory.CreateDirectory(store);
        File.WriteAllBytes(original, bytes);
        File.WriteAllBytes(source, bytes);
        string hash = Safety.ComputeSha256(source, CancellationToken.None);
        string master = Path.Combine(store, hash + ".jpg");
        NativeFile.NewHardLink(master, source);
        string id = Guid.NewGuid().ToString("N");
        var rec = Operation(id, original, original + ".chappy-backup-" + id, master, hash);
        Journal.Add(rec);
        // Both paths exist, but the original is NOT a new link to master.
        File.WriteAllBytes(rec.BackupPath, bytes);
        var plan = RecoveryService.Inspect(rec);
        Check(plan.Action == RecoveryAction.ManualReview,
            "両方存在・元パスが独立した状態を安全判定で操作禁止");
        ExpectThrows(() => RecoveryService.Apply(id, RecoveryAction.UndoNewHardLink),
            "曖昧な両ファイル存在時は強制操作さえ拒否");
        Check(File.Exists(original) && File.Exists(rec.BackupPath),
            "復旧拒否によってどちらの独立データも消えない");
        File.Delete(rec.BackupPath);
        Journal.Completed(id);

        // After a read-only preview, modifying the backup must invalidate the action.
        string id2 = Guid.NewGuid().ToString("N");
        var rec2 = Operation(id2, original, original + ".chappy-backup-" + id2, master, hash);
        Journal.Add(rec2);
        File.Move(original, rec2.BackupPath);
        Check(RecoveryService.Inspect(rec2).Action == RecoveryAction.RestoreIndependentBackup,
            "独立バックアップの安全な復元候補を表示");
        File.AppendAllText(rec2.BackupPath, "changed after preview");
        Check(RecoveryService.Inspect(rec2).Action == RecoveryAction.ManualReview,
            "プレビュー後の内容変更を検出して復旧保留");
        ExpectThrows(() => RecoveryService.Apply(id2, RecoveryAction.RestoreIndependentBackup),
            "復元操作の直前にも変更を拒否");
        Check(File.Exists(rec2.BackupPath) && !File.Exists(original),
            "変更されたバックアップは削除も移動もしない");
        File.Delete(rec2.BackupPath); // Only test-generated files.
        Journal.Completed(id2);
        Journal.TestFolderPath = null;
    }

    private static void TestRecoveryInterruptedAfterUnlink(string testRoot, byte[] bytes)
    {
        string root = Path.Combine(testRoot, "recovery-unlink-crash");
        Directory.CreateDirectory(root);
        Journal.TestFolderPath = Path.Combine(root, "isolated-journal");
        try
        {
            string source = Path.Combine(root, "source.png");
            string original = Path.Combine(root, "duplicate.png");
            File.WriteAllBytes(source, bytes);
            File.WriteAllBytes(original, bytes);
            string hash = Safety.ComputeSha256(original, CancellationToken.None);
            string store = Safety.StorePath(root);
            Directory.CreateDirectory(store);
            string master = Path.Combine(store, hash + ".png");
            NativeFile.NewHardLink(master, source);
            string id = Guid.NewGuid().ToString("N");
            var rec = Operation(id, original, original + ".chappy-backup-" + id, master, hash);
            Journal.Add(rec);
            File.Move(original, rec.BackupPath);
            NativeFile.NewHardLink(original, master);
            Check(RecoveryService.Inspect(rec).Action == RecoveryAction.UndoNewHardLink,
                "復旧中のリンク解除前には安全な独立化候補");
            FaultInjection.OnCheckpoint = point =>
            {
                if (point == "recovery-link-removed")
                    throw new SimulatedPowerLossException(point);
            };
            bool crashed = false;
            try { RecoveryService.Apply(id, RecoveryAction.UndoNewHardLink); }
            catch (SimulatedPowerLossException) { crashed = true; }
            finally { FaultInjection.OnCheckpoint = null; }
            Check(crashed, "共有リンク解除直後の模擬強制終了");
            Check(!File.Exists(original) && File.Exists(rec.BackupPath),
                "復旧途中の強制終了でも独立バックアップを保持");
            Check(RecoveryService.Inspect(rec).Action == RecoveryAction.RestoreIndependentBackup,
                "強制終了後はバックアップ単独からの復元に切替");
            RecoveryService.Apply(id, RecoveryAction.RestoreIndependentBackup);
            Check(File.Exists(original) && !File.Exists(rec.BackupPath),
                "復旧の再実行で元パスを回復");
            Check(!NativeFile.SameFile(original, master),
                "復旧の再実行後は独立実体");
            Check(Journal.Pending().Count == 0, "復旧途中の二重停止後も未解決記録ゼロ");
        }
        finally
        {
            FaultInjection.OnCheckpoint = null;
            Journal.TestFolderPath = null;
        }
    }

    private static void TestR14Guards(string root)
    {
        Check(CapacityGuard.Enough(4 * 1024 * 1024, CapacityGuard.TargetReserve),
            "容量下限と一致する場合は通過");
        Check(!CapacityGuard.Enough(0, CapacityGuard.TargetReserve),
            "空き容量ゼロなら統合停止判定");
        Check(!CapacityGuard.Enough(2 * 1024 * 1024 - 1, CapacityGuard.JournalReserve),
            "復旧記録用の空き容量不足を拒否");

        // A concurrent operation in another thread must fail without waiting.
        using (var lease = MutationLease.Acquire())
        {
            bool rejected = Task.Run(() =>
            {
                try { using var second = MutationLease.Acquire(); return false; }
                catch (InvalidOperationException) { return true; }
            }).GetAwaiter().GetResult();
            Check(rejected, "他スレッドの統合・復旧を排他ロックで拒否");
        }
        using (var lease = MutationLease.Acquire())
            Check(true, "ロック解除後に新しい処理を開始できる");

        string image = Path.Combine(root, "r14-valid-png.png");
        byte[] data = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC");
        File.WriteAllBytes(image, data);
        string before = Safety.ComputeSha256(image, CancellationToken.None);
        var inspected = PngInspector.Inspect(image);
        Check(inspected.Width == 1 && inspected.Height == 1 &&
            inspected.IdatBytes > 0 && inspected.ChunkCount == 3 &&
            inspected.CandidateForFutureLosslessOptimization,
            "PNG画像のチャンクCRC・サイズ・候補判定");
        Check(Safety.ComputeSha256(image, CancellationToken.None) == before,
            "PNG診断は元ファイルを一切変更しない");
        data[45] ^= 1; // Corrupt IDAT without updating its CRC.
        string corrupted = Path.Combine(root, "r14-corrupt-png.png");
        File.WriteAllBytes(corrupted, data);
        ExpectThrows(() => PngInspector.Inspect(corrupted),
            "CRCが不一致のPNGを拒否");
        string truncated = Path.Combine(root, "r14-truncated-png.png");
        File.WriteAllBytes(truncated, data[..(data.Length - 8)]);
        ExpectThrows(() => PngInspector.Inspect(truncated),
            "途中で切れたPNGを拒否");
    }

    // Lossless PNG preview test: start with a *valid*, deliberately inefficient
    // zlib/IDAT stream, with a metadata chunk whose bytes must be preserved.
    private static void TestR15PngPreview(string root)
    {
        string folder = Path.Combine(root, "r15-png");
        string previews = Path.Combine(root, "r15-previews");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(previews);
        string original = Path.Combine(folder, "test-lossless.png");
        const int width = 128, height = 128;
        byte[] ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA, filter=0, interlace=0
        byte[] scanlines = new byte[(width * 4 + 1) * height]; // all transparent black
        byte[] zipped;
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed,
                System.IO.Compression.CompressionLevel.NoCompression, true))
                zlib.Write(scanlines);
            zipped = compressed.ToArray();
        }
        using (var file = File.Create(original))
        {
            file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            WritePngChunk(file, "IHDR", ihdr);
            WritePngChunk(file, "tEXt", System.Text.Encoding.ASCII.GetBytes("Comment\0preserve this metadata"));
            WritePngChunk(file, "IDAT", zipped);
            WritePngChunk(file, "IEND", Array.Empty<byte>());
        }
        string originalHash = Safety.ComputeSha256(original, CancellationToken.None);
        var output = PngLosslessOptimizer.CreatePreview(original, previews);
        Check(output.PreviewPath != null && File.Exists(output.PreviewPath),
            "R1.5: 非効率PNGから無劣化候補を生成");
        Check(output.SavedBytes > 0 && output.PreviewBytes < output.OriginalBytes,
            "R1.5: 候補サイズが確実に小さくなる");
        Check(Safety.ComputeSha256(original, CancellationToken.None) == originalHash,
            "R1.5: 元PNGはハッシュ単位で未変更");
        var candidate = PngInspector.Inspect(output.PreviewPath!);
        Check(candidate.Width == width && candidate.Height == height && candidate.HasAncillaryChunk,
            "R1.5: 候補のチャンクとメタデータが検証に合格");
        Check(File.ReadAllBytes(output.PreviewPath!).AsSpan().IndexOf(
            System.Text.Encoding.ASCII.GetBytes("preserve this metadata")) >= 0,
            "R1.5: tEXtメタデータのバイト列を完全保存");
        string efficient = Path.Combine(folder, "efficient.png");
        File.Copy(output.PreviewPath!, efficient);
        string efficientHash = Safety.ComputeSha256(efficient, CancellationToken.None);
        var again = PngLosslessOptimizer.CreatePreview(efficient, previews);
        Check(again.PreviewPath == null && !Directory.EnumerateFiles(previews, "*chappy-preview*")
            .Any(x => x != output.PreviewPath),
            "R1.5: 削減できない候補は破棄する");
        Check(Safety.ComputeSha256(efficient, CancellationToken.None) == efficientHash,
            "R1.5: 改善なしでも元ファイルは未変更");
        string damaged = Path.Combine(folder, "damaged.png");
        byte[] corrupt = File.ReadAllBytes(original);
        corrupt[50] ^= 0x80; // metadata changed without CRC update
        File.WriteAllBytes(damaged, corrupt);
        ExpectThrows(() => PngLosslessOptimizer.CreatePreview(damaged, previews),
            "R1.5: 破損PNGは候補生成前に拒否");
    }

    // R1.6: deterministic PNG fixtures for multiple IDAT chunks, Adam7 and bit-depths.
    private static string CreateR16Png(string folder, string name,
        int width, int height, int color, int depth, bool adam7, bool multiIdat)
    {
        string path = Path.Combine(folder, name);
        byte[] ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = (byte)depth; ihdr[9] = (byte)color; ihdr[12] = adam7 ? (byte)1 : (byte)0;
        int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new ArgumentException() };
        using var raw = new MemoryStream();
        void AddPass(int pw, int ph)
        {
            if (pw <= 0 || ph <= 0) return;
            int pitch = checked((pw * channels * depth + 7) / 8);
            for (int row = 0; row < ph; row++)
            {
                raw.WriteByte(0); // PNG filter: None
                raw.Write(new byte[pitch]);
            }
        }
        if (adam7)
        {
            int[] xs = { 0, 4, 0, 2, 0, 1, 0 }, ys = { 0, 0, 4, 0, 2, 0, 1 };
            int[] dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
            for (int i = 0; i < 7; i++)
            {
                int pw = width <= xs[i] ? 0 : (width - xs[i] + dx[i] - 1) / dx[i];
                int ph = height <= ys[i] ? 0 : (height - ys[i] + dy[i] - 1) / dy[i];
                AddPass(pw, ph);
            }
        }
        else AddPass(width, height);
        byte[] zipped;
        using (var zippedBuffer = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(zippedBuffer,
                System.IO.Compression.CompressionLevel.NoCompression, leaveOpen: true))
                zlib.Write(raw.ToArray());
            zipped = zippedBuffer.ToArray();
        }
        using (var png = File.Create(path))
        {
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            WritePngChunk(png, "IHDR", ihdr);
            if (color == 3)
            {
                WritePngChunk(png, "PLTE", new byte[] { 0, 0, 0, 255, 255, 255 });
                WritePngChunk(png, "tRNS", new byte[] { 0, 255 });
            }
            WritePngChunk(png, "tEXt", System.Text.Encoding.ASCII.GetBytes("Author\0R16 fixture"));
            if (multiIdat)
            {
                int one = zipped.Length / 3;
                WritePngChunk(png, "IDAT", zipped[..one]);
                WritePngChunk(png, "IDAT", zipped[one..(one * 2)]);
                WritePngChunk(png, "IDAT", zipped[(one * 2)..]);
            }
            else WritePngChunk(png, "IDAT", zipped);
            WritePngChunk(png, "IEND", Array.Empty<byte>());
        }
        return path;
    }

    private sealed class InlineR16Progress : IProgress<PngBatchPreviewEngine.Progress>
    {
        private readonly Action<PngBatchPreviewEngine.Progress> _callback;
        public InlineR16Progress(Action<PngBatchPreviewEngine.Progress> callback) => _callback = callback;
        public void Report(PngBatchPreviewEngine.Progress value) => _callback(value);
    }

    private static void TestR16PngAndBatch(string root)
    {
        string variants = Path.Combine(root, "r16-variants");
        string output = Path.Combine(root, "r16-variants-output");
        Directory.CreateDirectory(variants);
        Directory.CreateDirectory(output);
        var cases = new (string Name, int Color, int Depth, bool Interlace, bool Multi)[]
        {
            ("rgba-multi.png", 6, 8, false, true),
            ("rgb-adam7.png", 2, 8, true, true),
            ("gray16.png", 0, 16, false, false),
            ("palette4.png", 3, 4, false, true),
            ("gray1-adam7.png", 0, 1, true, false)
        };
        foreach (var testCase in cases)
        {
            string source = CreateR16Png(variants, testCase.Name, 97, 89,
                testCase.Color, testCase.Depth, testCase.Interlace, testCase.Multi);
            string hash = Safety.ComputeSha256(source, CancellationToken.None);
            var inspection = PngInspector.Inspect(source);
            Check(inspection.Width == 97 && inspection.Height == 89 &&
                inspection.CandidateForFutureLosslessOptimization,
                "R1.6: 画像チャンクの形式チェック: " + testCase.Name);
            var preview = PngLosslessOptimizer.CreatePreview(source, output);
            Check(preview.PreviewPath != null && preview.SavedBytes > 0,
                "R1.6: 可逆候補生成: " + testCase.Name);
            Check(Safety.ComputeSha256(source, CancellationToken.None) == hash,
                "R1.6: 元ファイルの全バイト維持: " + testCase.Name);
        }

        string input = Path.Combine(root, "r16-batch-input");
        string nested = Path.Combine(input, "nested");
        string previews = Path.Combine(root, "r16-batch-output");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(previews);
        var a = CreateR16Png(input, "a.png", 64, 64, 6, 8, false, true);
        var b = CreateR16Png(nested, "b.png", 128, 64, 2, 8, true, false);
        var corrupt = CreateR16Png(nested, "c.png", 256, 128, 6, 8, false, false);
        byte[] damaged = File.ReadAllBytes(corrupt);
        damaged[50] ^= 1;
        File.WriteAllBytes(corrupt, damaged);
        string ah = Safety.ComputeSha256(a, CancellationToken.None);
        string bh = Safety.ComputeSha256(b, CancellationToken.None);
        string ch = Safety.ComputeSha256(corrupt, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        int reports = 0;
        bool stopped = false;
        try
        {
            PngBatchPreviewEngine.Run(input, previews,
                new InlineR16Progress(_ => { if (++reports == 1) stop.Cancel(); }), stop.Token);
        }
        catch (OperationCanceledException) { stopped = true; }
        Check(stopped && reports == 1, "R1.6: 最初の処理結果を記録後に中断可能");
        string manifest = Path.Combine(previews, ".chappy-png-batch-r16.json");
        Check(File.Exists(manifest), "R1.6: 出力側に再開記録が存在");
        var afterResume = PngBatchPreviewEngine.Run(input, previews);
        Check(afterResume.Total == 3 && afterResume.Done == 3 &&
            afterResume.Optimized == 2 && afterResume.Failed == 1 &&
            afterResume.SavedBytes > 0,
            "R1.6: 中断後に再開し、破損PNGを除外して有効な候補のみ保存");
        Check(Safety.ComputeSha256(a, CancellationToken.None) == ah &&
            Safety.ComputeSha256(b, CancellationToken.None) == bh &&
            Safety.ComputeSha256(corrupt, CancellationToken.None) == ch,
            "R1.6: 一括実行後も入力原本は全バイト不変");
        int candidateCount = Directory.GetFiles(Path.Combine(previews, "chappy-png-previews"), "*.png").Length;
        var afterRunAgain = PngBatchPreviewEngine.Run(input, previews);
        Check(afterRunAgain.Optimized == 2 &&
            Directory.GetFiles(Path.Combine(previews, "chappy-png-previews"), "*.png").Length == candidateCount,
            "R1.6: 再実行しても既存候補の複製を生成しない");
        ExpectThrows(() => PngBatchPreviewEngine.Run(input, nested),
            "R1.6: 入力と出力の階層が重なる場合は拒否");
        string candidateFile = Directory.GetFiles(Path.Combine(previews, "chappy-png-previews"), "*.png")[0];
        byte[] originalCandidate = File.ReadAllBytes(candidateFile);
        File.WriteAllBytes(candidateFile, originalCandidate.Concat(new byte[] { 0x01 }).ToArray());
        ExpectThrows(() => PngBatchPreviewEngine.Run(input, previews),
            "R1.6: 再開前の候補改変をSHA-256/サイズ検証で拒否");
        File.WriteAllBytes(candidateFile, originalCandidate);
        string progressJournal = Path.Combine(previews, ".chappy-png-batch-r16.progress.jsonl");
        string savedProgress = File.ReadAllText(progressJournal);
        File.AppendAllText(progressJournal, "truncated");
        ExpectThrows(() => PngBatchPreviewEngine.Run(input, previews),
            "R1.6: 中途半端な追記ログを安全停止");
        File.WriteAllText(progressJournal, savedProgress);
        string state = File.ReadAllText(manifest);
        File.WriteAllText(manifest, "{corrupted json");
        ExpectThrows(() => PngBatchPreviewEngine.Run(input, previews),
            "R1.6: 破損した再開記録を上書きせず拒否");
        File.WriteAllText(manifest, state);
    }

    private static void TestR17JournalAndJpeg(string root)
    {
        string input = Path.Combine(root, "r17-media-input");
        string output = Path.Combine(root, "r17-preview-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        string source = CreateR16Png(input, "r17.png", 128, 128, 6, 8, false, true);
        string originalHash = Safety.ComputeSha256(source, CancellationToken.None);
        var batch = PngBatchPreviewEngine.Run(input, output);
        Check(batch.Total == 1, "R1.7: 1件のPNGを一括スキャン");
        string journal = Path.Combine(output, ".chappy-png-batch-r16.progress.jsonl");
        string initial = File.ReadAllText(journal);
        Check(PngBatchJournalTools.InspectTail(input, output).State == "Healthy",
            "R1.7: 正常なJSONL末尾を判定");
        var audit = PngBatchJournalTools.ReadAudit(input, output);
        Check(audit.Total == 1 && audit.Tail.State == "Healthy",
            "R1.7: 進捗を読み取り専用で監査");
        string csv = Path.Combine(output, "r17-results.csv");
        PngBatchJournalTools.ExportCsv(audit, input, csv);
        Check(File.ReadAllText(csv).Contains("EstimatedSavingBytes") &&
            File.ReadAllText(csv).Contains("r17.png"), "R1.7: 結果一覧CSVを書き出す");
        ExpectThrows(() => PngBatchJournalTools.ExportCsv(audit, input,
            Path.Combine(input, "should-not-be-created.csv")),
            "R1.7: 原本フォルダ内へのCSV書き込みを拒否");

        File.AppendAllText(journal, "{\"RelativePath\":\"r17.png\",\"Status\":");
        Check(PngBatchJournalTools.InspectTail(input, output).State == "PartialTail",
            "R1.7: 末尾の不完全JSONだけを識別");
        ExpectThrows(() => PngBatchJournalTools.ReadAudit(input, output),
            "R1.7: 不完全なログの結果表示を拒否");
        var repaired = PngBatchJournalTools.RepairTail(input, output);
        Check(repaired.DiscardedTailBytes > 0 && File.Exists(repaired.BackupPath),
            "R1.7: 末尾修復と元ログ完全バックアップ");
        Check(File.ReadAllText(repaired.BackupPath).Contains("\"Status\":"),
            "R1.7: 元ログ末尾の証拠をバックアップに保存");
        Check(File.ReadAllText(journal) == initial &&
            PngBatchJournalTools.InspectTail(input, output).State == "Healthy",
            "R1.7: 検証済みの正常レコードをすべて維持");
        Check(Safety.ComputeSha256(source, CancellationToken.None) == originalHash,
            "R1.7: 末尾修復後も元画像バイト列が完全一致");
        Check(PngBatchPreviewEngine.Run(input, output).Total == 1,
            "R1.7: 修復済みのログから既存キューを再開");

        byte[] log = File.ReadAllBytes(journal);
        Check(log.Length > 1 && log[^1] == (byte)'\n', "R1.7: 末尾にLFあり");
        File.WriteAllBytes(journal, log[..^1]);
        Check(PngBatchJournalTools.InspectTail(input, output).State == "MissingNewline",
            "R1.7: 正常なJSONの末尾改行不足を識別");
        var newline = PngBatchJournalTools.RepairTail(input, output);
        Check(newline.AddedNewline && newline.DiscardedTailBytes == 0 &&
            PngBatchJournalTools.InspectTail(input, output).State == "Healthy",
            "R1.7: 正常なJSONを失わず改行を追記");
        ExpectThrows(() => PngBatchJournalTools.RepairTail(input, output),
            "R1.7: 正常なログの修復を拒否");

        byte[] beforeCorrupt = File.ReadAllBytes(journal);
        File.WriteAllText(journal, "{not-json}\n" + System.Text.Encoding.UTF8.GetString(beforeCorrupt));
        ExpectThrows(() => PngBatchJournalTools.RepairTail(input, output),
            "R1.7: 中間破損を自動修復せず保護");
        Check(File.ReadAllText(journal).StartsWith("{not-json}"),
            "R1.7: 中間破損ログの証拠を変更しない");
        File.WriteAllBytes(journal, beforeCorrupt); // Test-created journal only.

        // Synthetic *header* vector: the inspector does not claim JPEG decode.
        string jpg = Path.Combine(input, "r17.jpg");
        File.WriteAllBytes(jpg, new byte[] {
            0xFF,0xD8,
            0xFF,0xE1, 0x00,0x08, 0x45,0x78,0x69,0x66,0x00,0x00,
            0xFF,0xC0, 0x00,0x0B, 0x08, 0x00,0x20, 0x00,0x40, 0x01, 0x01,0x11,0x00,
            0xFF,0xDA, 0x00,0x02, 0x12,0x34, 0xFF,0xD9
        });
        string jpgHash = Safety.ComputeSha256(jpg, CancellationToken.None);
        var header = JpegInspector.Inspect(jpg);
        Check(header.Width == 64 && header.Height == 32 && header.HasExif &&
            header.HasEndMarker && !header.Progressive,
            "R1.7: JPEGヘッダ・EXIF・サイズを読み取り専用診断");
        Check(Safety.ComputeSha256(jpg, CancellationToken.None) == jpgHash,
            "R1.7: JPG診断はバイト列無変更");
        File.WriteAllBytes(jpg, new byte[] {0xFF, 0xD8, 0x00});
        ExpectThrows(() => JpegInspector.Inspect(jpg),
            "R1.7: JPEGの壊れたヘッダを拒否");
    }

    private static void WritePngChunk(Stream output, string kind, byte[] payload)
    {
        byte[] type = System.Text.Encoding.ASCII.GetBytes(kind);
        if (type.Length != 4) throw new ArgumentException("PNG chunk type must be 4 bytes.");
        Span<byte> word = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(word, (uint)payload.Length);
        output.Write(word);
        output.Write(type);
        output.Write(payload);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type.Concat(payload))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ?
                0xEDB88320U ^ (crc >> 1) : crc >> 1;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFF);
        output.Write(word);
    }

    private static void TestR18JpegAndOrphans(string root)
    {
        string input = Path.Combine(root, "r18-png-input");
        string output = Path.Combine(root, "r18-png-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        string png = CreateR16Png(input, "keep.png", 220, 200, 6, 8, false, false);
        string pngHash = Safety.ComputeSha256(png, CancellationToken.None);
        var batch = PngBatchPreviewEngine.Run(input, output);
        Check(batch.Optimized == 1, "R1.8: PNG候補を生成し監査準備");
        var initial = PngOrphanAudit.Inspect(input, output);
        Check(initial.Tracked == 1 && initial.Untracked.Count == 0,
            "R1.8: 記録で追跡済みのPNG候補を未参照と誤判定しない");
        string previewFolder = Path.Combine(output, "chappy-png-previews");
        string realPreview = Directory.EnumerateFiles(previewFolder, "*.png").Single();
        string leftOver = Path.Combine(previewFolder,
            "keep.chappy-preview-" + Guid.NewGuid().ToString("N") + ".png");
        string manual = Path.Combine(previewFolder, "private-user-file.png");
        File.Copy(realPreview, leftOver);
        File.WriteAllText(manual, "not an image; user-owned example");
        var audit = PngOrphanAudit.Inspect(input, output);
        Check(audit.Untracked.Count == 2 && audit.Untracked.Any(x => x.Path == leftOver && x.LooksGenerated),
            "R1.8: 候補風の未参照ファイルを検出");
        Check(audit.Untracked.Any(x => x.Path == manual && !x.LooksGenerated),
            "R1.8: 利用者ファイルは候補と区別する");
        string csv = Path.Combine(output, "untracked-r18.csv");
        PngOrphanAudit.ExportCsv(audit, input, csv);
        Check(File.Exists(csv) && File.ReadAllText(csv).Contains("private-user-file.png"),
            "R1.8: 未参照候補CSV出力");
        ExpectThrows(() => PngOrphanAudit.ExportCsv(audit, input, Path.Combine(input, "bad.csv")),
            "R1.8: 原本フォルダへの監査CSV保存を拒否");
        Check(File.Exists(leftOver) && File.Exists(manual) &&
            Safety.ComputeSha256(png, CancellationToken.None) == pngHash,
            "R1.8: 監査・CSVは画像も未参照ファイルも削除しない");

        // Retain old candidate references when the latest record has no preview.
        var plan = System.Text.Json.JsonSerializer.Deserialize<PngBatchPreviewEngine.Plan>(
            File.ReadAllText(Path.Combine(output, ".chappy-png-batch-r16.json")))!;
        var item = plan.Entries.Single();
        item.Status = "Pending";
        item.PreviewPath = null;
        item.PreviewBytes = 0;
        item.PreviewSha256 = null;
        File.AppendAllText(Path.Combine(output, ".chappy-png-batch-r16.progress.jsonl"),
            System.Text.Json.JsonSerializer.Serialize(item) + "\n");
        var historical = PngOrphanAudit.Inspect(input, output);
        Check(historical.Untracked.Count == 2 && historical.Tracked == 1,
            "R1.8: 過去ログで参照された候補は孤立扱いしない");

        string jpegDir = Path.Combine(root, "r18-jpeg");
        Directory.CreateDirectory(jpegDir);
        string a = Path.Combine(jpegDir, "a.jpg"), b = Path.Combine(jpegDir, "b.jpg");
        string damaged = Path.Combine(jpegDir, "trailing.jpg");
        File.WriteAllBytes(a, Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBD/wAARCAAEAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDl6KKK/SD4I//Z"));
        File.WriteAllBytes(b, Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQCgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBD/wAARCAAEAAQDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDmqKKK/VT8/P/Z"));
        var snapshot = JpegMarkerAudit.Inspect(a);
        Check(snapshot.Width == 4 && snapshot.Height == 4 && snapshot.Precision == 8 &&
            snapshot.Baseline && snapshot.ScanCount == 1,
            "R1.8: 8bitベースラインJPEGマーカー解析");
        Check(JpegLosslessPreviewEngine.EquivalentDecodedPixels(a, a),
            "R1.8: WICによる同一画像の画素完全一致");
        Check(!JpegLosslessPreviewEngine.EquivalentDecodedPixels(a, b),
            "R1.8: WICで異なる画素を拒否");
        File.WriteAllBytes(damaged, File.ReadAllBytes(a).Concat(new byte[] { 0xEE }).ToArray());
        ExpectThrows(() => JpegMarkerAudit.Inspect(damaged),
            "R1.8: JPEG末尾の不明データを安全拒否");
        string added = Path.Combine(jpegDir, "metadata.jpg");
        byte[] bytes = File.ReadAllBytes(a);
        byte[] app1 = { 0xFF, 0xE1, 0, 8, 69, 120, 105, 102, 0, 0 };
        File.WriteAllBytes(added, bytes[..2].Concat(app1).Concat(bytes[2..]).ToArray());
        var changed = JpegMarkerAudit.Inspect(added);
        Check(snapshot.ProtectedMarkerSha256 != changed.ProtectedMarkerSha256,
            "R1.8: EXIFなどのAPP追加をマーカー保護検査で検出");
    }

    private static void TestR19JpegQueue(string root)
    {
        string input = Path.Combine(root, "r19-jpg-input");
        string output = Path.Combine(root, "r19-jpg-output");
        string nested = Path.Combine(input, "nested");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(nested);
        string tool = Path.Combine(root, "jpegtran.exe");
        // Deliberately not a real executable. Invalid JPEG fixtures must be
        // rejected by the marker inspector before the external process runs.
        File.WriteAllText(tool, "test-only-not-an-exe");
        string image1 = Path.Combine(input, "large.jpg");
        string image2 = Path.Combine(nested, "small.jpeg");
        File.WriteAllBytes(image1, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9, 0x00 });
        File.WriteAllBytes(image2, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        string hash1 = Safety.ComputeSha256(image1, CancellationToken.None);
        string hash2 = Safety.ComputeSha256(image2, CancellationToken.None);

        var plan = JpegBatchPreviewEngine.BuildPlan(input, output, tool, CancellationToken.None);
        Check(plan.Entries.Count == 2 && plan.Entries[0].SourceBytes <= plan.Entries[1].SourceBytes,
            "R1.9: 子フォルダ再帰スキャンと小さい順の処理計画");
        var invalid = JpegBatchPreviewEngine.BuildPlan(input, output, tool, CancellationToken.None);
        invalid.Entries[0].RelativePath = "..\\escape.jpg";
        ExpectThrows(() => JpegBatchPreviewEngine.ValidatePlan(invalid, input, output, tool),
            "R1.9: 進捗計画からのディレクトリ脱出を拒否");
        ExpectThrows(() => JpegBatchPreviewEngine.BuildPlan(input, nested, tool, CancellationToken.None),
            "R1.9: 原本と候補が入れ子になる保存先を拒否");

        var first = JpegBatchPreviewEngine.Run(input, output, tool);
        Check(first.Total == 2 && first.Failed == 2 && first.Completed == 0 &&
            first.EstimatedSavingBytes == 0,
            "R1.9: 不正JPEGは候補を採用せず記録して続行");
        Check(Safety.ComputeSha256(image1, CancellationToken.None) == hash1 &&
            Safety.ComputeSha256(image2, CancellationToken.None) == hash2,
            "R1.9: 無効なJPEGがあっても原本バイト列は無変更");
        var resumed = JpegBatchPreviewEngine.Run(input, output, tool);
        Check(resumed.Total == 2 && resumed.Failed == 2,
            "R1.9: ファイルごとの追記ログを読み直して再試行できる");
        var audit = JpegBatchPreviewEngine.Inspect(input, output, tool);
        Check(audit.Rows.Count == 2 && audit.VerifiedSavingBytes == 0,
            "R1.9: プレビューのないファイルは削減見込に計上しない");

        string report = Path.Combine(root, "r19-report.csv");
        var crafted = new JpegBatchPreviewEngine.Audit(
            new[] { new JpegBatchPreviewEngine.ReportRow("=HYPERLINK(1)", "Failed", 1, 0, 0, "", "") }, 0, 0);
        JpegBatchPreviewEngine.ExportCsv(crafted, input, output, report);
        Check(File.ReadAllText(report).Contains("'=HYPERLINK(1)"),
            "R1.9: CSVへの数式注入を無害化");
        ExpectThrows(() => JpegBatchPreviewEngine.ExportCsv(audit, input, output,
            Path.Combine(input, "unsafe.csv")), "R1.9: 原本階層へのCSV出力を拒否");
        ExpectThrows(() => JpegBatchPreviewEngine.ExportCsv(audit, input, output, report),
            "R1.9: 既存CSVの上書きを拒否");

        string journal = Path.Combine(output, ".chappy-jpeg-r19.progress.jsonl");
        string intact = File.ReadAllText(journal);
        File.AppendAllText(journal, "{\"RelativePath\":");
        ExpectThrows(() => JpegBatchPreviewEngine.Run(input, output, tool),
            "R1.9: 途中で切れた追記ログを勝手に修復しない");
        Check(File.ReadAllText(journal).EndsWith("{\"RelativePath\":"),
            "R1.9: 破損した証拠ログを保持");
        File.WriteAllText(journal, intact); // test-only fixture reset
        File.AppendAllText(image2, "changed");
        var changed = JpegBatchPreviewEngine.Run(input, output, tool);
        Check(changed.NeedsReview == 1 &&
            Safety.ComputeSha256(image1, CancellationToken.None) == hash1,
            "R1.9: 作業計画後の変更を要確認として隔離");
        Check(JpegBatchPreviewEngine.Inspect(input, output, tool).NeedsReview == 1,
            "R1.9: 変更済み原本の状態を監査で表示");
    }

    private static void TestR20CombinedAndJpegRepair(string root)
    {
        string input = Path.Combine(root, "r20-source");
        string output = Path.Combine(root, "r20-candidates");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        string png = CreateR16Png(input, "r20-image.png", 128, 128, 6, 8, false, true);
        string jpg = Path.Combine(input, "r20-invalid.jpg");
        File.WriteAllBytes(jpg, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        string tool = Path.Combine(root, "jpegtran.exe");
        string pngHash = Safety.ComputeSha256(png, CancellationToken.None);
        string jpgHash = Safety.ComputeSha256(jpg, CancellationToken.None);
        Check(PngBatchPreviewEngine.Run(input, output).Total == 1,
            "R2.0: PNGプレビュー計画を作成");
        Check(JpegBatchPreviewEngine.Run(input, output, tool).Total == 1,
            "R2.0: JPGプレビュー計画を作成");
        var combined = CombinedCapacityReport.Inspect(input, output);
        Check(combined.PngFiles == 1 && combined.JpegFiles == 1 && combined.Rows.Count == 2,
            "R2.0: JPGとPNGの候補を横断監査");
        Check(combined.CandidateCount >= 1 && combined.EstimatedSavingBytes > 0 &&
            combined.ActualFreedBytes == 0,
            "R2.0: 容量見込と未回復の容量を区別");
        Check(combined.EstimatedSavingBytes == combined.Rows.Sum(x => x.EstimatedSavingBytes),
            "R2.0: JPGとPNGの候補を行単位で1度だけ集計");
        string csv = Path.Combine(root, "r20-report.csv");
        CombinedCapacityReport.ExportCsv(combined, input, output, csv);
        Check(File.ReadAllText(csv).Contains("ActualFreedBytes") &&
            File.ReadAllText(csv).Contains("r20-image.png"),
            "R2.0: 横断CSVに実際の回復量0を明示");
        ExpectThrows(() => CombinedCapacityReport.ExportCsv(combined, input, output, csv),
            "R2.0: 既存の横断CSV上書きを禁止");
        ExpectThrows(() => CombinedCapacityReport.ExportCsv(combined, input, output,
            Path.Combine(input, "unsafe.csv")),
            "R2.0: 原本側への横断CSV書き込みを拒否");
        var injected = new CombinedCapacityReport.Summary(0, 1, 0, 0, 0, 0,
            new[] { new CombinedCapacityReport.Row("JPG", "=HYPERLINK(1)", "Failed", 1, 0, 0, "note") });
        string injectionCsv = Path.Combine(root, "r20-injection.csv");
        CombinedCapacityReport.ExportCsv(injected, input, output, injectionCsv);
        Check(File.ReadAllText(injectionCsv).Contains("'=HYPERLINK(1)"),
            "R2.0: CSV数式として解釈される名前を無害化");

        string journal = Path.Combine(output, ".chappy-jpeg-r19.progress.jsonl");
        byte[] healthy = File.ReadAllBytes(journal);
        Check(JpegBatchJournalTools.InspectTail(input, output).State == "Healthy",
            "R2.0: 正常なJPGログは修復不要");
        File.AppendAllText(journal, "{\"RelativePath\":");
        Check(JpegBatchJournalTools.InspectTail(input, output).State == "PartialTail",
            "R2.0: JPGの未完了末尾JSONを識別");
        ExpectThrows(() => JpegBatchPreviewEngine.Inspect(input, output, tool),
            "R2.0: 修復前の不完全ログで集計停止");
        var repaired = JpegBatchJournalTools.RepairTail(input, output);
        Check(repaired.RemovedTailBytes > 0 && File.Exists(repaired.BackupPath) &&
            File.ReadAllBytes(journal).AsSpan().SequenceEqual(healthy),
            "R2.0: JPG末尾修復は正常レコードを保持し元ログを完全バックアップ");
        Check(JpegBatchJournalTools.InspectTail(input, output).State == "Healthy",
            "R2.0: 修復後ログを再診断");
        File.WriteAllBytes(journal, healthy[..^1]);
        Check(JpegBatchJournalTools.InspectTail(input, output).State == "MissingNewline",
            "R2.0: 末尾の改行欠落を不完全JSONと区別");
        var newline = JpegBatchJournalTools.RepairTail(input, output);
        Check(newline.AddedNewline && newline.RemovedTailBytes == 0 &&
            File.ReadAllBytes(journal).AsSpan().SequenceEqual(healthy),
            "R2.0: 最終JSONを削除せず末尾改行だけ補修");
        ExpectThrows(() => JpegBatchJournalTools.RepairTail(input, output),
            "R2.0: 健全なJPGログの修復を禁止");

        // Valid JSON with mismatched file identity must NEVER be discarded as a torn tail.
        var foreign = new JpegBatchPreviewEngine.Entry {
            RelativePath = "untracked.jpg", SourceBytes = 1, ModifiedUtcTicks = 1, Status = "Failed" };
        File.AppendAllText(journal, System.Text.Json.JsonSerializer.Serialize(foreign));
        ExpectThrows(() => JpegBatchJournalTools.RepairTail(input, output),
            "R2.0: 正常JSONでも計画外の記録は自動破棄しない");
        Check(File.ReadAllText(journal).Contains("untracked.jpg"),
            "R2.0: 不整合の証拠ログを保持");
        File.WriteAllBytes(journal, healthy);
        File.WriteAllText(journal, "{not-json}\n" + System.Text.Encoding.UTF8.GetString(healthy));
        ExpectThrows(() => JpegBatchJournalTools.RepairTail(input, output),
            "R2.0: 中間破損したログは自動修復を拒否");
        Check(File.ReadAllText(journal).StartsWith("{not-json}"),
            "R2.0: 中間破損の証拠を保持");
        File.WriteAllBytes(journal, healthy);
        // Invalid complete record followed by a valid record must not be masked.
        var invalidMiddle = new JpegBatchPreviewEngine.Entry {
            RelativePath = "r20-invalid.jpg", SourceBytes = new FileInfo(jpg).Length,
            ModifiedUtcTicks = File.GetLastWriteTimeUtc(jpg).Ticks, Status = "WrongState" };
        File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(invalidMiddle) +
            "\n" + System.Text.Encoding.UTF8.GetString(healthy));
        ExpectThrows(() => JpegBatchJournalTools.InspectTail(input, output),
            "R2.0: 後続の正常記録で中間の不正状態を隠せない");
        File.WriteAllBytes(journal, healthy);
        Check(Safety.ComputeSha256(png, CancellationToken.None) == pngHash &&
            Safety.ComputeSha256(jpg, CancellationToken.None) == jpgHash,
            "R2.0: レポート・末尾修復を行っても原本画像は完全不変");
        Check(CombinedCapacityReport.Inspect(input, output).ActualFreedBytes == 0,
            "R2.0: 非破壊処理から実回復量を捏造しない");
    }

    private static void TestR21NonOverlappingEstimate(string root)
    {
        string input = Path.Combine(root, "r21-dedupe-input");
        Directory.CreateDirectory(input);
        string first = Path.Combine(input, "one.jpg");
        string second = Path.Combine(input, "two.jpg");
        string standalone = Path.Combine(input, "unique.png");
        byte[] jpegBytes = RandomNumberGenerator.GetBytes(100);
        File.WriteAllBytes(first, jpegBytes);
        File.WriteAllBytes(second, jpegBytes);
        File.WriteAllBytes(standalone, RandomNumberGenerator.GetBytes(80));
        File.SetCreationTimeUtc(second, File.GetCreationTimeUtc(first));
        File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(first));
        string originalHash = Safety.ComputeSha256(first, CancellationToken.None);
        var scan = Scanner.Run(input, true, null, CancellationToken.None);
        Check(scan.Groups.Count == 1 && scan.Groups[0].BlockReason(false) == null,
            "R2.1: 2つの独立した完全一致ファイルを検出");
        var rows = new[] {
            new CombinedCapacityReport.Row("JPG", "one.jpg", "Completed", 100, 90, 10, "ok"),
            new CombinedCapacityReport.Row("JPG", "two.jpg", "Completed", 100, 90, 10, "ok"),
            new CombinedCapacityReport.Row("PNG", "unique.png", "CandidatePresent", 80, 75, 5, "ok"),
            new CombinedCapacityReport.Row("JPG", "../escape.jpg", "Completed", 100, 0, 100, "bad")
        };
        var preview = new CombinedCapacityReport.Summary(1, 2, 3, 0, 25, 0, rows);
        var calculated = CombinedSavingsPlanner.Analyze(preview, scan, false);
        Check(calculated.PreviewOnlyBytes == 25 && calculated.DedupeOnlyBytes == 100,
            "R2.1: 画像プレビューと独立実体の重複を別々に集計");
        Check(calculated.DoubleCountAvoidedBytes == 10 &&
              calculated.CombinedHypotheticalBytes == 115 &&
              calculated.VerifiedDuplicateGroups == 1 && calculated.ActualFreedBytes == 0,
            "R2.1: 2重計上10Bを除外し実回復0を保持");
        File.AppendAllText(second, "changed");
        var stale = CombinedSavingsPlanner.Analyze(preview, scan, false);
        Check(stale.DedupeOnlyBytes == 0 && stale.VerifiedDuplicateGroups == 0 &&
              stale.SkippedDuplicateGroups == 1 && stale.DoubleCountAvoidedBytes == 0,
            "R2.1: スキャン後の原本変更では重複見込を採用しない");
        Check(Safety.ComputeSha256(first, CancellationToken.None) == originalHash,
            "R2.1: 分析による原本のSHA-256不変");
    }

    // R2.3: isolated synthetic transaction test. Never touches actual media.
    private static void TestR23SyntheticReplacement(string root)
    {
        var faults = new[] { "r23-after-external-copy", "r23-after-stage-copy",
            "r23-after-journal", "r23-after-replace",
            "r23-after-quarantine", "r23-after-restore" };
        foreach (string checkpoint in faults)
        {
            string dir = Path.Combine(root, "r23-" + checkpoint);
            SyntheticReplacementExperiment.PrepareTestDirectory(dir);
            byte[] original = RandomNumberGenerator.GetBytes(300_000);
            byte[] replacement = RandomNumberGenerator.GetBytes(100_000);
            string file = Path.Combine(dir, "original.bin");
            string candidate = Path.Combine(dir, "candidate.bin");
            File.WriteAllBytes(file, original);
            File.WriteAllBytes(candidate, replacement);
            string originalHash = Safety.ComputeSha256(file, CancellationToken.None);
            bool triggered = false;
            FaultInjection.OnCheckpoint = name =>
            {
                if (name == checkpoint) throw new SimulatedPowerLossException(name);
            };
            try
            {
                try
                {
                    SyntheticReplacementExperiment.Begin(dir);
                    if (checkpoint == "r23-after-quarantine" || checkpoint == "r23-after-restore")
                        SyntheticReplacementExperiment.RollBack(dir);
                }
                catch (SimulatedPowerLossException) { triggered = true; }
            }
            finally { FaultInjection.OnCheckpoint = null; }
            Check(triggered, "R2.3: simulated stop " + checkpoint);
            if (checkpoint is "r23-after-external-copy" or "r23-after-stage-copy")
            {
                Check(!SyntheticReplacementExperiment.Pending(dir),
                    "R2.3: pre-journal stop cannot mutate original");
            }
            else
            {
                Check(SyntheticReplacementExperiment.Pending(dir),
                    "R2.3: interrupted cutover retains a journal");
                SyntheticReplacementExperiment.RollBack(dir);
                Check(!SyntheticReplacementExperiment.Pending(dir),
                    "R2.3: rollback resolves synthetic journal");
            }
            Check(Safety.ComputeSha256(file, CancellationToken.None) == originalHash,
                "R2.3: original bytes restored " + checkpoint);
            Check(File.ReadAllBytes(candidate).AsSpan().SequenceEqual(replacement),
                "R2.3: candidate bytes preserved " + checkpoint);
        }
        // Normal cutover is still test-only and MUST be rolled back, never committed.
        string normalDir = Path.Combine(root, "r23-normal");
        SyntheticReplacementExperiment.PrepareTestDirectory(normalDir);
        string normalOriginal = Path.Combine(normalDir, "original.bin");
        string normalCandidate = Path.Combine(normalDir, "candidate.bin");
        File.WriteAllBytes(normalOriginal, RandomNumberGenerator.GetBytes(220_000));
        File.WriteAllBytes(normalCandidate, RandomNumberGenerator.GetBytes(70_000));
        var originalIdentity = NativeFile.GetIdentity(normalOriginal);
        string oldHash = Safety.ComputeSha256(normalOriginal, CancellationToken.None);
        string candidateHash = Safety.ComputeSha256(normalCandidate, CancellationToken.None);
        SyntheticReplacementExperiment.Begin(normalDir);
        Check(Safety.ComputeSha256(normalOriginal, CancellationToken.None) == candidateHash &&
              SyntheticReplacementExperiment.Pending(normalDir),
            "R2.3: test-only cutover writes candidate but retains rollback journal");
        SyntheticReplacementExperiment.RollBack(normalDir);
        Check(Safety.ComputeSha256(normalOriginal, CancellationToken.None) == oldHash &&
              NativeFile.GetIdentity(normalOriginal).SameFile(originalIdentity) &&
              !SyntheticReplacementExperiment.Pending(normalDir),
            "R2.3: original content AND NTFS file identity restored");

        // Fail closed before writing backup if the original already has hardlinks.
        string linkedDir = Path.Combine(root, "r23-hardlinked");
        SyntheticReplacementExperiment.PrepareTestDirectory(linkedDir);
        string linkedOriginal = Path.Combine(linkedDir, "original.bin");
        File.WriteAllBytes(linkedOriginal, RandomNumberGenerator.GetBytes(220_000));
        File.WriteAllBytes(Path.Combine(linkedDir, "candidate.bin"), RandomNumberGenerator.GetBytes(70_000));
        NativeFile.NewHardLink(Path.Combine(linkedDir, "another-name.bin"), linkedOriginal);
        ExpectThrows(() => SyntheticReplacementExperiment.Begin(linkedDir),
            "R2.3: synthetic original with hardlinks is refused");
        Check(!SyntheticReplacementExperiment.Pending(linkedDir) &&
              !File.Exists(Path.Combine(linkedDir, ".r23-external-copy.bin")),
            "R2.3: preflight refusal leaves no mutation journal or backup");

        string activeDir = Path.Combine(root, "r23-active-writer");
        SyntheticReplacementExperiment.PrepareTestDirectory(activeDir);
        string activeSource = Path.Combine(activeDir, "original.bin");
        File.WriteAllBytes(activeSource, RandomNumberGenerator.GetBytes(220_000));
        File.WriteAllBytes(Path.Combine(activeDir, "candidate.bin"), RandomNumberGenerator.GetBytes(70_000));
        using (var writer = new FileStream(activeSource, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete))
        {
            ExpectThrows(() => SyntheticReplacementExperiment.Begin(activeDir),
                "R2.3: active writer blocks synthetic replacement");
        }
        Check(!SyntheticReplacementExperiment.Pending(activeDir) &&
              !File.Exists(Path.Combine(activeDir, ".r23-external-copy.bin")),
            "R2.3: active writer refusal leaves original untouched");

        // Do not allow accidental operation outside test-only sentinel directories.
        ExpectThrows(() => SyntheticReplacementExperiment.Begin(root),
            "R2.3: missing isolated-directory sentinel is refused");

        // A tampered journal backup MUST prevent changes to synthetic original.
        string badDir = Path.Combine(root, "r23-tampered");
        SyntheticReplacementExperiment.PrepareTestDirectory(badDir);
        File.WriteAllBytes(Path.Combine(badDir, "original.bin"), RandomNumberGenerator.GetBytes(300_000));
        File.WriteAllBytes(Path.Combine(badDir, "candidate.bin"), RandomNumberGenerator.GetBytes(100_000));
        SyntheticReplacementExperiment.Begin(badDir);
        File.AppendAllText(Path.Combine(badDir, ".r23-external-copy.bin"), "TAMPERED");
        string hashBefore = Safety.ComputeSha256(Path.Combine(badDir, "original.bin"), CancellationToken.None);
        ExpectThrows(() => SyntheticReplacementExperiment.RollBack(badDir),
            "R2.3: tampered backup blocks automatic rollback");
        Check(Safety.ComputeSha256(Path.Combine(badDir, "original.bin"), CancellationToken.None) == hashBefore &&
              SyntheticReplacementExperiment.Pending(badDir),
            "R2.3: uncertain state retained for manual review");
    }

    // R2.4: pre-journal orphan handling + post-journal cleanup boundary.
    // All mutations stay inside the uniquely named disposable TEMP test tree.
    private static void TestR24PreparationRecovery(string root)
    {
        foreach (string checkpoint in new[] { "r23-after-external-copy",
            "r23-after-stage-copy", "r24-after-journal-temp" })
        {
            string dir = Path.Combine(root, "r24-prep-" + checkpoint);
            SyntheticReplacementExperiment.PrepareTestDirectory(dir);
            string original = Path.Combine(dir, "original.bin");
            string candidate = Path.Combine(dir, "candidate.bin");
            File.WriteAllBytes(original, RandomNumberGenerator.GetBytes(180_000));
            File.WriteAllBytes(candidate, RandomNumberGenerator.GetBytes(60_000));
            var before = Safety.Capture(original);
            string expected = Safety.ComputeSha256(original, CancellationToken.None);
            bool triggered = false;
            FaultInjection.OnCheckpoint = name =>
            {
                if (name == checkpoint) throw new SimulatedPowerLossException(name);
            };
            try { SyntheticReplacementExperiment.Begin(dir); }
            catch (SimulatedPowerLossException) { triggered = true; }
            finally { FaultInjection.OnCheckpoint = null; }
            Check(triggered && !SyntheticReplacementExperiment.Pending(dir),
                "R2.4: simulated pre-journal stop " + checkpoint);
            var inspect = SyntheticReplacementExperiment.InspectPreparation(dir);
            Check(inspect.HasArtifacts && inspect.CanDiscard && inspect.StagedBytes >= 180_000,
                "R2.4: exact matching synthetic artifacts can be identified " + checkpoint);
            SyntheticReplacementExperiment.DiscardVerifiedPreparation(dir);
            Check(!SyntheticReplacementExperiment.InspectPreparation(dir).HasArtifacts &&
                  Safety.SameSnapshot(before) &&
                  Safety.ComputeSha256(original, CancellationToken.None) == expected,
                "R2.4: preparatory artifacts removed without changing original " + checkpoint);
            // An interrupted preparation can now be attempted again.
            SyntheticReplacementExperiment.Begin(dir);
            SyntheticReplacementExperiment.RollBack(dir);
            Check(!SyntheticReplacementExperiment.Pending(dir) &&
                  !SyntheticReplacementExperiment.InspectPreparation(dir).HasArtifacts &&
                  Safety.SameSnapshot(before),
                "R2.4: retry and rollback preserve identity and metadata " + checkpoint);
        }

        // After the journal is removed, a crash can leave only a safe orphaned
        // external copy. It must not be mistaken for a pending transaction.
        string cleanupDir = Path.Combine(root, "r24-post-journal-cleanup");
        SyntheticReplacementExperiment.PrepareTestDirectory(cleanupDir);
        string cleanupOriginal = Path.Combine(cleanupDir, "original.bin");
        File.WriteAllBytes(cleanupOriginal, RandomNumberGenerator.GetBytes(180_000));
        File.WriteAllBytes(Path.Combine(cleanupDir, "candidate.bin"), RandomNumberGenerator.GetBytes(60_000));
        var cleanupSnapshot = Safety.Capture(cleanupOriginal);
        SyntheticReplacementExperiment.Begin(cleanupDir);
        bool postJournalStop = false;
        FaultInjection.OnCheckpoint = name =>
        {
            if (name == "r24-after-journal-cleanup") throw new SimulatedPowerLossException(name);
        };
        try { SyntheticReplacementExperiment.RollBack(cleanupDir); }
        catch (SimulatedPowerLossException) { postJournalStop = true; }
        finally { FaultInjection.OnCheckpoint = null; }
        Check(postJournalStop && !SyntheticReplacementExperiment.Pending(cleanupDir) &&
              SyntheticReplacementExperiment.InspectPreparation(cleanupDir).CanDiscard,
            "R2.4: post-journal crash leaves a verifiable test-only orphan");
        SyntheticReplacementExperiment.DiscardVerifiedPreparation(cleanupDir);
        Check(Safety.SameSnapshot(cleanupSnapshot) &&
              !SyntheticReplacementExperiment.InspectPreparation(cleanupDir).HasArtifacts,
            "R2.4: post-journal cleanup keeps original metadata and content");

        // Fail closed: a modified preparation copy may NOT be deleted by an
        // automatic cleanup and the original must remain unchanged.
        string tamperDir = Path.Combine(root, "r24-prep-tamper");
        SyntheticReplacementExperiment.PrepareTestDirectory(tamperDir);
        string tamperSource = Path.Combine(tamperDir, "original.bin");
        File.WriteAllBytes(tamperSource, RandomNumberGenerator.GetBytes(180_000));
        File.WriteAllBytes(Path.Combine(tamperDir, "candidate.bin"), RandomNumberGenerator.GetBytes(60_000));
        string tamperHash = Safety.ComputeSha256(tamperSource, CancellationToken.None);
        FaultInjection.OnCheckpoint = name =>
        {
            if (name == "r23-after-external-copy") throw new SimulatedPowerLossException(name);
        };
        try { SyntheticReplacementExperiment.Begin(tamperDir); }
        catch (SimulatedPowerLossException) { }
        finally { FaultInjection.OnCheckpoint = null; }
        File.AppendAllText(Path.Combine(tamperDir, ".r23-external-copy.bin"), "TAMPERED");
        Check(!SyntheticReplacementExperiment.InspectPreparation(tamperDir).CanDiscard,
            "R2.4: tampered pre-journal backup requires manual review");
        ExpectThrows(() => SyntheticReplacementExperiment.DiscardVerifiedPreparation(tamperDir),
            "R2.4: tampered preparation cleanup is rejected");
        Check(Safety.ComputeSha256(tamperSource, CancellationToken.None) == tamperHash,
            "R2.4: rejected cleanup never changes original");

        // A directory occupying a reserved transaction filename is a collision.
        string collisionDir = Path.Combine(root, "r24-reserved-name-directory");
        SyntheticReplacementExperiment.PrepareTestDirectory(collisionDir);
        File.WriteAllBytes(Path.Combine(collisionDir, "original.bin"), RandomNumberGenerator.GetBytes(180_000));
        File.WriteAllBytes(Path.Combine(collisionDir, "candidate.bin"), RandomNumberGenerator.GetBytes(60_000));
        Directory.CreateDirectory(Path.Combine(collisionDir, ".r23-external-copy.bin"));
        Check(!SyntheticReplacementExperiment.InspectPreparation(collisionDir).CanDiscard,
            "R2.4: occupied reserved directory does not masquerade as a file");
        ExpectThrows(() => SyntheticReplacementExperiment.Begin(collisionDir),
            "R2.4: reserved directory collision prevents synthetic mutation");

        // A writer changing the source between journal flush and final
        // preflight must abort before File.Replace, leaving a journal for manual review.
        string raceDir = Path.Combine(root, "r24-last-moment-source-change");
        SyntheticReplacementExperiment.PrepareTestDirectory(raceDir);
        string raceSource = Path.Combine(raceDir, "original.bin");
        File.WriteAllBytes(raceSource, RandomNumberGenerator.GetBytes(180_000));
        File.WriteAllBytes(Path.Combine(raceDir, "candidate.bin"), RandomNumberGenerator.GetBytes(60_000));
        bool changed = false;
        FaultInjection.OnCheckpoint = name =>
        {
            if (name == "r24-before-final-preflight")
            {
                File.AppendAllText(raceSource, "simulated writer");
                changed = true;
            }
        };
        try
        {
            ExpectThrows(() => SyntheticReplacementExperiment.Begin(raceDir),
                "R2.4: changed source after journal is rejected before cutover");
        }
        finally { FaultInjection.OnCheckpoint = null; }
        Check(changed && SyntheticReplacementExperiment.Pending(raceDir) &&
              !File.Exists(Path.Combine(raceDir, ".r23-original-rollback.bin")),
            "R2.4: source race leaves evidence and never invokes File.Replace");
        ExpectThrows(() => SyntheticReplacementExperiment.RollBack(raceDir),
            "R2.4: changed source is not silently discarded during rollback");

        // The fixed artifact names are never interpreted as a permission to
        // clean arbitrary files outside the disposable test root.
        ExpectThrows(() => SyntheticReplacementExperiment.InspectPreparation(root),
            "R2.4: orphan-inspection rejects unsentinelled user directories");

        // Reject NTFS alternate streams before staging. These are outside the
        // ordinary file byte stream and must not disappear in a replacement.
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            string adsDir = Path.Combine(root, "r24-named-stream");
            SyntheticReplacementExperiment.PrepareTestDirectory(adsDir);
            string source = Path.Combine(adsDir, "original.bin");
            File.WriteAllBytes(source, RandomNumberGenerator.GetBytes(180_000));
            File.WriteAllBytes(Path.Combine(adsDir, "candidate.bin"), RandomNumberGenerator.GetBytes(60_000));
            File.WriteAllText(source + ":chappy_r24_probe", "protected metadata");
            ExpectThrows(() => SyntheticReplacementExperiment.Begin(adsDir),
                "R2.4: named NTFS stream blocks synthetic replacement");
            Check(!SyntheticReplacementExperiment.Pending(adsDir) &&
                  !File.Exists(Path.Combine(adsDir, ".r23-external-copy.bin")),
                "R2.4: named-stream rejection occurs before staging");
        }
    }

    // R2.5: byte-identical but shared/reparse/ADS-bearing transaction artifacts
    // must never be trusted for rollback, cleanup or deletion.
    private static void TestR25ArtifactIsolation(string root)
    {
        string extDir = Path.Combine(root, "r25-shared-external");
        SyntheticReplacementExperiment.PrepareTestDirectory(extDir);
        string extSource = Path.Combine(extDir, "original.bin");
        File.WriteAllBytes(extSource, RandomNumberGenerator.GetBytes(192_000));
        File.WriteAllBytes(Path.Combine(extDir, "candidate.bin"), RandomNumberGenerator.GetBytes(64_000));
        string extHash = Safety.ComputeSha256(extSource, CancellationToken.None);
        FaultInjection.OnCheckpoint = name =>
        {
            if (name == "r23-after-external-copy") throw new SimulatedPowerLossException(name);
        };
        try
        {
            ExpectThrows(() => SyntheticReplacementExperiment.Begin(extDir),
                "R2.5: preparatory synthetic crash captured");
        }
        finally { FaultInjection.OnCheckpoint = null; }
        string extBackup = Path.Combine(extDir, ".r23-external-copy.bin");
        string extAlias = Path.Combine(extDir, "artifact-alias.bin");
        NativeFile.NewHardLink(extAlias, extBackup);
        Check(Safety.ComputeSha256(extAlias, CancellationToken.None) == extHash &&
              !SyntheticReplacementExperiment.InspectPreparation(extDir).CanDiscard,
            "R2.5: SHA一致でも共有バックアップの自動削除は拒否");
        ExpectThrows(() => SyntheticReplacementExperiment.DiscardVerifiedPreparation(extDir),
            "R2.5: second name to backup prevents cleanup");
        Check(File.Exists(extAlias) && File.Exists(extBackup) &&
              Safety.ComputeSha256(extSource, CancellationToken.None) == extHash,
            "R2.5: rejected cleanup preserves every linked artifact and source");

        string journalDir = Path.Combine(root, "r25-shared-journal");
        SyntheticReplacementExperiment.PrepareTestDirectory(journalDir);
        File.WriteAllBytes(Path.Combine(journalDir, "original.bin"), RandomNumberGenerator.GetBytes(192_000));
        File.WriteAllBytes(Path.Combine(journalDir, "candidate.bin"), RandomNumberGenerator.GetBytes(64_000));
        SyntheticReplacementExperiment.Begin(journalDir);
        string journalOriginal = Path.Combine(journalDir, "original.bin");
        string journalHash = Safety.ComputeSha256(journalOriginal, CancellationToken.None);
        string journal = Path.Combine(journalDir, ".r23-rollback.json");
        NativeFile.NewHardLink(Path.Combine(journalDir, "journal-alias.json"), journal);
        ExpectThrows(() => SyntheticReplacementExperiment.RollBack(journalDir),
            "R2.5: shared hardlink journal cannot authorize rollback");
        Check(SyntheticReplacementExperiment.Pending(journalDir) &&
              Safety.ComputeSha256(journalOriginal, CancellationToken.None) == journalHash,
            "R2.5: untrusted journal leaves evidence and original path unchanged");

        string rollbackDir = Path.Combine(root, "r25-shared-rollback");
        SyntheticReplacementExperiment.PrepareTestDirectory(rollbackDir);
        File.WriteAllBytes(Path.Combine(rollbackDir, "original.bin"), RandomNumberGenerator.GetBytes(192_000));
        File.WriteAllBytes(Path.Combine(rollbackDir, "candidate.bin"), RandomNumberGenerator.GetBytes(64_000));
        SyntheticReplacementExperiment.Begin(rollbackDir);
        string rollbackSource = Path.Combine(rollbackDir, "original.bin");
        string rollbackHash = Safety.ComputeSha256(rollbackSource, CancellationToken.None);
        NativeFile.NewHardLink(Path.Combine(rollbackDir, "rollback-alias.bin"),
            Path.Combine(rollbackDir, ".r23-original-rollback.bin"));
        ExpectThrows(() => SyntheticReplacementExperiment.RollBack(rollbackDir),
            "R2.5: shared rollback file is not restored or discarded");
        Check(SyntheticReplacementExperiment.Pending(rollbackDir) &&
              Safety.ComputeSha256(rollbackSource, CancellationToken.None) == rollbackHash,
            "R2.5: pending journal retained on unsafe rollback artifact");

        string sizeDir = Path.Combine(root, "r25-oversized-journal");
        SyntheticReplacementExperiment.PrepareTestDirectory(sizeDir);
        File.WriteAllBytes(Path.Combine(sizeDir, "original.bin"), RandomNumberGenerator.GetBytes(192_000));
        File.WriteAllBytes(Path.Combine(sizeDir, "candidate.bin"), RandomNumberGenerator.GetBytes(64_000));
        SyntheticReplacementExperiment.Begin(sizeDir);
        string sizeOriginal = Path.Combine(sizeDir, "original.bin");
        string sizeHash = Safety.ComputeSha256(sizeOriginal, CancellationToken.None);
        File.AppendAllText(Path.Combine(sizeDir, ".r23-rollback.json"), new string('x', 8193));
        ExpectThrows(() => SyntheticReplacementExperiment.RollBack(sizeDir),
            "R2.5: oversized journal is never accepted");
        Check(SyntheticReplacementExperiment.Pending(sizeDir) &&
              Safety.ComputeSha256(sizeOriginal, CancellationToken.None) == sizeHash,
            "R2.5: oversized journal does not mutate the synthetic original");

        string tempDir = Path.Combine(root, "r25-shared-temporary-journal");
        SyntheticReplacementExperiment.PrepareTestDirectory(tempDir);
        File.WriteAllBytes(Path.Combine(tempDir, "original.bin"), RandomNumberGenerator.GetBytes(192_000));
        File.WriteAllBytes(Path.Combine(tempDir, "candidate.bin"), RandomNumberGenerator.GetBytes(64_000));
        FaultInjection.OnCheckpoint = name =>
        {
            if (name == "r24-after-journal-temp") throw new SimulatedPowerLossException(name);
        };
        try
        {
            ExpectThrows(() => SyntheticReplacementExperiment.Begin(tempDir),
                "R2.5: pending journal write interrupted in synthetic fixture");
        }
        finally { FaultInjection.OnCheckpoint = null; }
        NativeFile.NewHardLink(Path.Combine(tempDir, "pending-alias.json"),
            Path.Combine(tempDir, ".r23-rollback.json.tmp"));
        Check(!SyntheticReplacementExperiment.InspectPreparation(tempDir).CanDiscard,
            "R2.5: shared pre-journal record refused even if JSON bytes match");

        // ADS is not part of the unnamed SHA-256 stream. Only run on NTFS.
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            string adsDir = Path.Combine(root, "r25-stage-ads");
            SyntheticReplacementExperiment.PrepareTestDirectory(adsDir);
            File.WriteAllBytes(Path.Combine(adsDir, "original.bin"), RandomNumberGenerator.GetBytes(192_000));
            File.WriteAllBytes(Path.Combine(adsDir, "candidate.bin"), RandomNumberGenerator.GetBytes(64_000));
            FaultInjection.OnCheckpoint = name =>
            {
                if (name == "r23-after-stage-copy") throw new SimulatedPowerLossException(name);
            };
            try
            {
                ExpectThrows(() => SyntheticReplacementExperiment.Begin(adsDir),
                    "R2.5: staging checkpoint interrupted in synthetic fixture");
            }
            finally { FaultInjection.OnCheckpoint = null; }
            File.WriteAllText(Path.Combine(adsDir, ".r23-stage.bin:chappy_r25_probe"), "test-only metadata");
            Check(!SyntheticReplacementExperiment.InspectPreparation(adsDir).CanDiscard,
                "R2.5: ADS-bearing stage rejected despite unchanged main bytes");
            ExpectThrows(() => SyntheticReplacementExperiment.DiscardVerifiedPreparation(adsDir),
                "R2.5: named stream prevents automatic cleanup");
        }
    }

    // R2.6: reject stage/backup mutation at the LAST preflight, and reject
    // same-content identity substitution while reading a verified artifact.
    // These are disposable synthetic .bin fixtures; no production paths touched.
    private static void TestR26LateMutationAndIdentity(string root)
    {
        foreach (string kind in new[] { "stage-bytes", "backup-bytes", "stage-hardlink" })
        {
            string dir = Path.Combine(root, "r26-late-" + kind);
            SyntheticReplacementExperiment.PrepareTestDirectory(dir);
            string original = Path.Combine(dir, "original.bin");
            string candidate = Path.Combine(dir, "candidate.bin");
            byte[] originalBytes = RandomNumberGenerator.GetBytes(192_000);
            File.WriteAllBytes(original, originalBytes);
            File.WriteAllBytes(candidate, RandomNumberGenerator.GetBytes(64_000));
            FileIdentity originalIdentity = NativeFile.GetIdentity(original);
            string originalHash = Safety.ComputeSha256(original, CancellationToken.None);
            string stage = Path.Combine(dir, ".r23-stage.bin");
            string backup = Path.Combine(dir, ".r23-external-copy.bin");
            bool injected = false;
            FaultInjection.OnCheckpoint = checkpoint =>
            {
                if (checkpoint != "r24-before-final-preflight" || injected) return;
                injected = true;
                switch (kind)
                {
                    case "stage-bytes":
                        File.WriteAllBytes(stage, RandomNumberGenerator.GetBytes(64_000));
                        break;
                    case "backup-bytes":
                        File.WriteAllBytes(backup, RandomNumberGenerator.GetBytes(192_000));
                        break;
                    case "stage-hardlink":
                        NativeFile.NewHardLink(Path.Combine(dir, "stage-alias.bin"), stage);
                        break;
                }
            };
            try
            {
                ExpectThrows(() => SyntheticReplacementExperiment.Begin(dir),
                    "R2.6: cutover refuses late " + kind);
            }
            finally { FaultInjection.OnCheckpoint = null; }
            Check(injected && File.Exists(Path.Combine(dir, ".r23-rollback.json")),
                "R2.6: late " + kind + " leaves evidence for manual review");
            Check(!File.Exists(Path.Combine(dir, ".r23-original-rollback.bin")) &&
                NativeFile.GetIdentity(original).SameFile(originalIdentity) &&
                Safety.ComputeSha256(original, CancellationToken.None) == originalHash,
                "R2.6: late " + kind + " cannot replace original");
        }

        string raceDir = Path.Combine(root, "r26-same-bytes-different-identity");
        SyntheticReplacementExperiment.PrepareTestDirectory(raceDir);
        string raceOriginal = Path.Combine(raceDir, "original.bin");
        string raceCandidate = Path.Combine(raceDir, "candidate.bin");
        File.WriteAllBytes(raceOriginal, RandomNumberGenerator.GetBytes(192_000));
        File.WriteAllBytes(raceCandidate, RandomNumberGenerator.GetBytes(64_000));
        string raceHash = Safety.ComputeSha256(raceOriginal, CancellationToken.None);
        FileIdentity raceIdentity = NativeFile.GetIdentity(raceOriginal);
        string external = Path.Combine(raceDir, ".r23-external-copy.bin");
        bool swapped = false;
        FaultInjection.OnCheckpoint = checkpoint =>
        {
            if (checkpoint != "r26-after-artifact-hash" || swapped || !File.Exists(external)) return;
            byte[] copy = File.ReadAllBytes(external);
            File.Move(external, external + ".original-identity");
            File.WriteAllBytes(external, copy);  // hash unchanged, NTFS ID changed
            swapped = true;
        };
        try
        {
            ExpectThrows(() => SyntheticReplacementExperiment.Begin(raceDir),
                "R2.6: byte-identical artifact identity swap is rejected");
        }
        finally { FaultInjection.OnCheckpoint = null; }
        Check(swapped && !File.Exists(Path.Combine(raceDir, ".r23-rollback.json")),
            "R2.6: identity swap is caught before creating a transaction journal");
        Check(File.Exists(external + ".original-identity") &&
            Safety.ComputeSha256(external, CancellationToken.None) == raceHash &&
            NativeFile.GetIdentity(raceOriginal).SameFile(raceIdentity) &&
            Safety.ComputeSha256(raceOriginal, CancellationToken.None) == raceHash,
            "R2.6: swap rejection preserves original and both backup copies");
    }

    public static int Main()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows NTFS only");
            return 2;
        }
        string root = Path.Combine(Path.GetTempPath(), "ChappyStorageSmoke_" + Guid.NewGuid().ToString("N"));
        string sub = Path.Combine(root, "nested 日本語");
        Directory.CreateDirectory(sub);
        // Tests use their own journal path, never the user's actual recovery directory.
        Journal.TestFolderPath = Path.Combine(root, "test-journal");
        try
        {
            Check(Directory.Exists(root), "隔離されたテスト領域を作成");
            Check(new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase),
                "R2.5: synthetic test drive must actually be NTFS");
            TestR14Guards(root);
            Check(Journal.Pending().Count == 0, "テスト専用ジャーナルは空");
            Check(Journal.UnresolvedRecordFiles().Count == 0, "未解決の試験記録なし");
            string a = Path.Combine(root, "a.jpg");
            string b = Path.Combine(root, "copy.jpg");
            string c = Path.Combine(sub, "different.jpg");
            byte[] bytes = RandomNumberGenerator.GetBytes(128 * 1024);
            File.WriteAllBytes(a, bytes);
            File.WriteAllBytes(b, bytes);
            File.WriteAllBytes(c, RandomNumberGenerator.GetBytes(128 * 1024));
            File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
            File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
            Check(Safety.ByteEqual(a, b, CancellationToken.None), "全バイト比較で同一を検出");
            Check(!Safety.ByteEqual(a, c, CancellationToken.None), "同じサイズで異なる内容は除外");
            Check(Safety.ComputeSha256(a, CancellationToken.None) == Safety.ComputeSha256(b, CancellationToken.None), "SHA-256一致");
            Check(!Safety.IsMedia("file.7z") && Safety.IsMedia("file.mp4"), "対象拡張子フィルタ");
            Check(!Safety.SameSnapshot(new FileSnapshot {
                Path = c, Length = bytes.Length, CreationUtc = DateTime.MinValue,
                ModifiedUtc = DateTime.MinValue, Attributes = FileAttributes.Normal,
                Identity = NativeFile.GetIdentity(c), SecurityDescriptor = NativeFile.ReadSecurity(c)
            }), "タイムスタンプの不一致を検出");

            // ADS is distinct NTFS data, invisible to normal content hashing.
            string ads = Path.Combine(root, "has_stream.jpg");
            File.WriteAllBytes(ads, bytes);
            File.WriteAllText(ads + ":Zone.Identifier", "[ZoneTransfer]\\nZoneId=3");
            Check(NativeFile.HasNamedDataStreams(ads), "代替データストリームを検出");
            ExpectThrows(() => Safety.Capture(ads), "代替ストリームを持つファイルを統合対象から除外");

            var result = Scanner.Run(root, true, null, CancellationToken.None);
            Check(result.Groups.Count == 1, "完全一致グループ1件");
            Check(result.Groups[0].Files.Count == 2, "完全一致ファイル2件");
            Check(result.Groups[0].BlockReason(false) == null, "厳格メタデータ照合を通過");
            Check(result.Groups[0].Savings == bytes.Length, "理論削減量の計算が正しい");

            // An already-open writer must prevent modifying the duplicate.
            using (var activeWriter = new FileStream(b, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                long blocked = DedupeEngine.Run(root, result.Groups, false, null, CancellationToken.None);
                Check(blocked == 0, "書込み使用中のファイルは統合しない");
                Check(!NativeFile.SameFile(a, b), "使用中のファイルの独立性を維持");
                Check(!Directory.EnumerateFiles(Safety.StorePath(root)).Any(),
                    "使用中のファイルしかない場合、統合先リンクを残さない");
            }
            Journal.AssertCleanBeforeMerge();
            long saved = DedupeEngine.Run(root, result.Groups, false, null, CancellationToken.None);
            Check(saved == bytes.Length, "統合結果の理論削減量");
            Check(File.Exists(a) && File.Exists(b), "元の2パスが存在");
            Check(NativeFile.SameFile(a, b), "同じNTFSファイルIDを共有");
            Check(File.ReadAllBytes(a).AsSpan().SequenceEqual(bytes) &&
                File.ReadAllBytes(b).AsSpan().SequenceEqual(bytes), "元の内容は完全一致を維持");
            Check(Directory.Exists(Safety.StorePath(root)), "統合先フォルダを作成");
            Check(Journal.Pending().Count == 0, "成功した統合に復旧待ち記録なし");
            var after = Scanner.Run(root, true, null, CancellationToken.None);
            Check(after.Groups.Count == 0, "再スキャンで既存ハードリンクを除外");

            // A pending journal blocks new mutations, and recovery restores only
            // independently checked content. Both files are inside this test root.
            string restore = Path.Combine(root, "restore.png");
            string backup = restore + ".chappy-backup-test";
            File.WriteAllBytes(restore, bytes);
            var recId = Guid.NewGuid().ToString("N");
            var rec = Operation(recId, restore, restore + ".chappy-backup-" + recId,
                Path.Combine(Safety.StorePath(root), "test.jpg"),
                Safety.ComputeSha256(restore, CancellationToken.None));
            backup = rec.BackupPath;
            Journal.Add(rec);
            ExpectThrows(() => Journal.AssertCleanBeforeMerge(), "未完了ジャーナルがあれば統合禁止");
            File.Move(restore, backup);
            var restoreReport = Journal.RecoverSafely();
            Check(File.Exists(restore) && !File.Exists(backup), "照合済みの退避ファイルを復元");
            Check(restoreReport.Any(x => x.Contains("復元")), "復旧操作のログが残る");
            Check(Journal.Pending().Count == 0, "復旧済みジャーナルを完了");

            // Tampered backup: no restore, delete or journal completion.
            File.WriteAllBytes(restore, bytes);
            var badId = Guid.NewGuid().ToString("N");
            var bad = Operation(badId, restore, restore + ".chappy-backup-" + badId,
                Path.Combine(Safety.StorePath(root), "test.jpg"),
                Safety.ComputeSha256(restore, CancellationToken.None));
            backup = bad.BackupPath;
            Journal.Add(bad);
            File.Move(restore, backup);
            File.AppendAllText(backup, "tampered");
            var reject = Journal.RecoverSafely();
            Check(!File.Exists(restore) && File.Exists(backup), "改変された退避ファイルは復元しない");
            Check(reject.Any(x => x.Contains("不一致")), "復旧保留の理由を出力");
            Check(Journal.Pending().Count == 1, "異常のある復旧記録は保持");
            File.Delete(backup); // Only the test owns this temporary data.
            Journal.Completed(bad.OperationId);

            // A malformed journal or interrupted tmp write also blocks merging.
            Directory.CreateDirectory(Journal.FolderPath);
            string broken = Path.Combine(Journal.FolderPath, "bad.json");
            File.WriteAllText(broken, "{not-json");
            Check(Journal.UnresolvedRecordFiles().Contains(broken), "壊れたジャーナルを発見");
            ExpectThrows(() => Journal.AssertCleanBeforeMerge(), "壊れた記録で統合禁止");
            File.Delete(broken);
            var attackerId = Guid.NewGuid().ToString("N");
            string attacker = Path.Combine(Journal.FolderPath, attackerId + ".json");
            File.WriteAllText(attacker, System.Text.Json.JsonSerializer.Serialize(Operation(attackerId,
                restore, restore + ".wrong-suffix", a, new string('A', 64))));
            Check(Journal.UnresolvedRecordFiles().Contains(attacker),
                "保存名・退避パスが異常なジャーナルを拒否");
            Check(Journal.Pending().Count == 0, "異常なジャーナルは復旧キューへ取り込まない");
            File.Delete(attacker);
            string interrupted = Path.Combine(Journal.FolderPath, "interrupted.json.tmp");
            File.WriteAllText(interrupted, "partial");
            Check(Journal.UnresolvedRecordFiles().Contains(interrupted), "未完了のtmp記録を発見");
            ExpectThrows(() => Journal.AssertCleanBeforeMerge(), "未完了のtmp記録で統合禁止");
            File.Delete(interrupted);
            Journal.AssertCleanBeforeMerge();
            // Simulated power loss at each irreversible transaction boundary.
            foreach (var checkpoint in new[] { "journal-recorded", "backup-renamed",
                "new-link-created", "pre-backup-delete", "backup-deleted" })
                TestPowerLoss(root, checkpoint, bytes);
            foreach (var checkpoint in new[] { "journal-recorded", "backup-renamed",
                "new-link-created", "pre-backup-delete" })
                TestOrdinaryError(root, checkpoint, bytes);
            TestRecoveryRefusesAmbiguous(root, bytes);
            TestRecoveryInterruptedAfterUnlink(root, bytes);
            Journal.TestFolderPath = Path.Combine(root, "test-journal");
            Journal.AssertCleanBeforeMerge();
            // Run preview tests *after* scanner assertions: preview fixtures
            // intentionally contain copies and must not pollute dedupe counts.
            TestR15PngPreview(root);
            TestR16PngAndBatch(root);
            TestR17JournalAndJpeg(root);
            TestR18JpegAndOrphans(root);
            TestR19JpegQueue(root);
            TestR20CombinedAndJpegRepair(root);
            TestR21NonOverlappingEstimate(root);
            TestR22Readiness();
            TestR23SyntheticReplacement(root);
            TestR24PreparationRecovery(root);
            TestR25ArtifactIsolation(root);
            TestR26LateMutationAndIdentity(root);
            R213MediaReportTests.Run(root, Check, ExpectThrows);
            R214PersonalBatchPreflightTests.Run(root, Check, ExpectThrows);
            R215ReconciledCapacityTests.Run(root, Check, ExpectThrows);
            Console.WriteLine($"All {_checks} safety checks passed.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
        finally
        {
            Journal.TestFolderPath = null;
            // Remove only the uniquely generated test directory, never user files.
            try { Directory.Delete(root, true); }
            catch { Console.Error.WriteLine("Please manually remove test directory: " + root); }
        }
    }
}
