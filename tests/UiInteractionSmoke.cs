using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor;
using ChappyStorageCompressor.Core;

// R2.17 part 5: exercise REAL WPF XAML controls and routed Click handlers,
// not private-method calls. Hosted CI may not have a visible interactive
// desktop, so this is NOT physical mouse/display automation.
internal static class UiInteractionSmoke
{
    private const int BytesPerFile = 32 * 1024 * 1024;

    [STAThread]
    private static int Main()
    {
        string testRoot = Path.Combine(Path.GetTempPath(),
            "chappy-r217-wpf-routed-click-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        MainWindow? window = null;
        System.Windows.Application? application = null;
        try
        {
            string journal = Path.Combine(testRoot, "journal");
            Directory.CreateDirectory(journal);
            var journalOverride = typeof(Journal).GetProperty(
                "TestFolderPath", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("No isolated journal override");
            journalOverride.SetValue(null, journal);

            // Capture a genuine WPF dispatcher synchronization context for
            // Scan_Click's async continuation and Progress<T> handlers.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var app = new App();
            app.InitializeComponent();
            application = app;
            window = new MainWindow();
            // x:Name fields are internal in the production app assembly.
            // Resolve real WPF named controls from the loaded XAML namescope.
            var scanButton = Named<Button>(window, "ScanButton");
            var stopButton = Named<Button>(window, "StopButton");
            var mergeButton = Named<Button>(window, "MergeButton");
            var dedupePlanButton = Named<Button>(window, "DedupePlanButton");
            var unifiedBatchButton = Named<Button>(window, "UnifiedBatchButton");
            var pngBatchButton = Named<Button>(window, "PngBatchButton");
            var jpegBatchButton = Named<Button>(window, "JpegBatchButton");
            var jobProgress = Named<ProgressBar>(window, "JobProgress");
            var progressText = Named<TextBlock>(window, "ProgressText");
            var groupsGrid = Named<DataGrid>(window, "GroupsGrid");
            var pathBox = Named<TextBox>(window, "PathBox");

            Require(!mergeButton.IsEnabled && !dedupePlanButton.IsEnabled &&
                    !stopButton.IsEnabled && scanButton.IsEnabled,
                "Production merge stays disabled, Scan enabled, Stop initially disabled");
            Require(window.Title.Contains("R2.17", StringComparison.Ordinal) &&
                    unifiedBatchButton.IsEnabled && pngBatchButton.IsEnabled &&
                    jpegBatchButton.IsEnabled,
                "Actual WPF UI shows R2.17 and preview-only batch actions enabled while idle");

            string originals = Path.Combine(testRoot, "synthetic-originals");
            Directory.CreateDirectory(originals);
            string left = Path.Combine(originals, "test-a.mp4");
            string right = Path.Combine(originals, "test-b.mp4");
            byte[] bytes = new byte[BytesPerFile];
            new Random(217555).NextBytes(bytes);
            File.WriteAllBytes(left, bytes);
            File.Copy(left, right);
            File.SetCreationTimeUtc(right, File.GetCreationTimeUtc(left));
            File.SetLastWriteTimeUtc(right, File.GetLastWriteTimeUtc(left));
            bytes = Array.Empty<byte>();

            string beforeA = OracleHash(left), beforeB = OracleHash(right);
            Require(beforeA == beforeB, "Independent identical generated source contents");
            pathBox.Text = originals;
            Require(scanButton.IsEnabled, "Target path allows scan before run");

            // A synthetic routed WPF click, using the real XAML handler.
            scanButton.RaiseEvent(new RoutedEventArgs(
                ButtonBase.ClickEvent, scanButton));
            Require(stopButton.IsEnabled && !scanButton.IsEnabled,
                "Scan_Click enters busy mode and enables Stop in WPF");
            Require(!unifiedBatchButton.IsEnabled && !pngBatchButton.IsEnabled &&
                    !jpegBatchButton.IsEnabled && !mergeButton.IsEnabled,
                "Actual running Scan disables ALL preview batch launch buttons, never enables Merge");
            Require(jobProgress.IsIndeterminate,
                "Scanning begins with indeterminate WPF progress");

            // Click without pumping yet: work starts via Task.Run, while this
            // STA keeps the dispatcher queued until the stop event fires.
            stopButton.RaiseEvent(new RoutedEventArgs(
                ButtonBase.ClickEvent, stopButton));
            Require(progressText.Text == "中断要求中…" &&
                    !stopButton.IsEnabled,
                "Stop routed Click immediately updates status and disables Stop");
            var currentCts = typeof(MainWindow).GetField("_work",
                BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(window) as CancellationTokenSource;
            Require(currentCts is not null && currentCts.IsCancellationRequested,
                "The real Stop_Click handler cancels the active scanner token");

            PumpUntil(() => scanButton.IsEnabled &&
                progressText.Text == "中断", TimeSpan.FromSeconds(20));
            Require(!jobProgress.IsIndeterminate &&
                    !stopButton.IsEnabled &&
                    !mergeButton.IsEnabled &&
                    groupsGrid.Items.Count == 0,
                "Canceled scan clears running UI state and exposes no partial groups");
            Require(unifiedBatchButton.IsEnabled && pngBatchButton.IsEnabled &&
                    jpegBatchButton.IsEnabled && !mergeButton.IsEnabled,
                "After canceled Scan, PNG/JPEG preview-only controls recover safely while Merge stays locked");

            // A second invocation uses the same synthetic files and must
            // complete normally through the real WPF routed Click handler.
            scanButton.RaiseEvent(new RoutedEventArgs(
                ButtonBase.ClickEvent, scanButton));
            Require(stopButton.IsEnabled &&
                    !mergeButton.IsEnabled,
                "Second scan starts safely after a canceled run");

            bool intermediateHashUiSeen = false;
            PumpUntil(() => scanButton.IsEnabled &&
                progressText.Text == "スキャン完了",
                TimeSpan.FromSeconds(25),
                () =>
                {
                    // Informational only, not a timing-flaky hard assertion.
                    if (progressText.Text.StartsWith("SHA ", StringComparison.Ordinal))
                        intermediateHashUiSeen = true;
                });
            Require(jobProgress.Value == 100 &&
                    !jobProgress.IsIndeterminate &&
                    !mergeButton.IsEnabled && dedupePlanButton.IsEnabled &&
                    groupsGrid.Items.Count == 1,
                "Completed scan has one valid duplicate group and 100% WPF progress");
            Require(unifiedBatchButton.IsEnabled && pngBatchButton.IsEnabled &&
                    jpegBatchButton.IsEnabled && !mergeButton.IsEnabled,
                "After completed Scan, all preview-only batch controls return to idle availability");
            Require(OracleHash(left) == beforeA &&
                    OracleHash(right) == beforeB &&
                    Directory.EnumerateFiles(originals).Count() == 2,
                "Routed WPF clicks left two independent synthetic source files unchanged");

            // Part 23: this NEW enabled button is a genuine read-only,
            // revalidated dedupe-first / independent Undo plan. The destructive
            // MergeButton must remain disabled even after the dry run.
            string planBody = "";
            SetPrivate(window, "_showDedupePlan",
                new Action<string>(message => planBody = message));
            FileIdentity beforeLeftId = NativeFile.GetIdentity(left);
            FileIdentity beforeRightId = NativeFile.GetIdentity(right);
            RoutedClick(dedupePlanButton);
            Require(stopButton.IsEnabled && !dedupePlanButton.IsEnabled &&
                    !mergeButton.IsEnabled,
                "R2.17 part 23: real WPF plan button runs read-only revalidation in busy state");
            PumpUntil(() => dedupePlanButton.IsEnabled &&
                progressText.Text == "統合・Undo計画（未実行）", TimeSpan.FromSeconds(35));
            bool part23WpfPlanSafe = planBody.Contains("統合可能グループ: 1", StringComparison.Ordinal) &&
                planBody.Contains("独立Undo", StringComparison.Ordinal) &&
                planBody.Contains("実際に削減した容量: 0 B", StringComparison.Ordinal) &&
                !mergeButton.IsEnabled && !stopButton.IsEnabled &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId) &&
                !NativeFile.SameFile(left, right) &&
                !Directory.Exists(Safety.StorePath(originals)) &&
                !Directory.EnumerateFiles(originals, "*.chappy-backup-*").Any();
            Require(part23WpfPlanSafe,
                "R2.17 part 23: actual WPF duplicate-first/Undo plan never links or changes scanned media");

            // Part 15: use the real UnifiedBatchButton WPF routed Click, with
            // native dialogs replaced by deterministic in-process answers.
            // Hosted Actions has no visible/interactive desktop. We do not
            // fake the actual filesystem preflight or resume inspector.
            string previewInputs = Path.Combine(testRoot, "preview-inputs");
            string previewOutput = Path.Combine(testRoot, "preview-output");
            Directory.CreateDirectory(previewInputs);
            Directory.CreateDirectory(previewOutput);
            string sourcePng = Path.Combine(previewInputs, "synthetic.png");
            string sourceJpeg = Path.Combine(previewInputs, "synthetic.jpg");
            File.WriteAllBytes(sourcePng, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgYGAAAAAEAAH2FzhVAAAAAElFTkSuQmCC"));
            File.WriteAllBytes(sourceJpeg, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            string beforePng = OracleHash(sourcePng), beforeJpeg = OracleHash(sourceJpeg);
            string syntheticTool = Path.Combine(testRoot, "jpegtran.exe");
            File.WriteAllText(syntheticTool, "synthetic marker - MUST NOT EXECUTE");
            pathBox.Text = previewInputs;
            int toolPickerCount = 0, outputPickerCount = 0, confirmationCount = 0;
            int errorCount = 0;
            string errorMessage = "";
            SetPrivate(window, "_showCombinedFailure", new Action<string>(message =>
            {
                errorCount++;
                errorMessage = message;
            }));

            // Tool dialog Cancel: output picker must not open, no busy mode.
            SetPrivate(window, "_pickCombinedTool", new Func<string?, string?>(_ =>
            {
                toolPickerCount++;
                return null;
            }));
            SetPrivate(window, "_pickCombinedOutput", new Func<string?, string?>(_ =>
            {
                outputPickerCount++;
                return previewOutput;
            }));
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(_ =>
            {
                confirmationCount++;
                return true;
            }));
            RoutedClick(unifiedBatchButton);
            Require(toolPickerCount == 1 && outputPickerCount == 0 &&
                    confirmationCount == 0 && unifiedBatchButton.IsEnabled &&
                    !stopButton.IsEnabled && !mergeButton.IsEnabled &&
                    !Directory.EnumerateFileSystemEntries(previewOutput).Any(),
                "R2.17 part 15: real WPF combined-button click honors tool picker Cancel");

            // Output folder dialog Cancel: consent must not run.
            SetPrivate(window, "_pickCombinedTool", new Func<string?, string?>(_ =>
            {
                toolPickerCount++;
                return syntheticTool;
            }));
            SetPrivate(window, "_pickCombinedOutput", new Func<string?, string?>(_ =>
            {
                outputPickerCount++;
                return null;
            }));
            RoutedClick(unifiedBatchButton);
            Require(toolPickerCount == 2 && outputPickerCount == 1 &&
                    confirmationCount == 0 && unifiedBatchButton.IsEnabled &&
                    !stopButton.IsEnabled && !mergeButton.IsEnabled,
                "R2.17 part 15: output picker Cancel never reaches confirmation or engines");

            // Both selections are supplied per run, and the real preflight
            // runs before the test answers No to the visible-UI substitute.
            SetPrivate(window, "_pickCombinedOutput", new Func<string?, string?>(_ =>
            {
                outputPickerCount++;
                return previewOutput;
            }));
            string confirmedBody = "";
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(body =>
            {
                confirmedBody = body;
                confirmationCount++;
                return false;
            }));
            RoutedClick(unifiedBatchButton);
            Require(stopButton.IsEnabled && !unifiedBatchButton.IsEnabled,
                "R2.17 part 15: real combined handler enters busy mode for read-only preflight");
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "確認でキャンセル（未実行）", TimeSpan.FromSeconds(20));
            Require(confirmationCount == 1 && confirmedBody.Contains("PNG:", StringComparison.Ordinal) &&
                    confirmedBody.Contains("JPG/JPEG:", StringComparison.Ordinal) &&
                    confirmedBody.Contains("元ファイルの置換・削除・統合は行いません", StringComparison.Ordinal) &&
                    !stopButton.IsEnabled && !mergeButton.IsEnabled &&
                    !Directory.EnumerateFileSystemEntries(previewOutput).Any(),
                "R2.17 part 15: genuine preflight and explicit No leave no candidates or journals");

            // The consent dialog can be canceled while it is open. Trigger
            // the real CancellationTokenSource without invoking jpegtran.exe.
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(_ =>
            {
                confirmationCount++;
                var cts = typeof(MainWindow).GetField("_work",
                    BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(window)
                    as CancellationTokenSource;
                Require(cts is not null, "R2.17 part 15: combined confirmation has active cancellation token");
                cts.Cancel();
                return true;
            }));
            RoutedClick(unifiedBatchButton);
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "中断（再開可能）", TimeSpan.FromSeconds(20));
            Require(confirmationCount == 2 && !stopButton.IsEnabled &&
                    !mergeButton.IsEnabled && errorCount == 0 &&
                    !Directory.EnumerateFileSystemEntries(previewOutput).Any(),
                "R2.17 part 15: confirmation-time cancellation refuses to start either engine");

            // Another process may create an unrecognized file after the Yes.
            // The exact existing post-confirmation inspector must detect it.
            string foreign = Path.Combine(previewOutput, "foreign-not-to-delete.txt");
            byte[] foreignBytes = { 0x11, 0x22, 0x33, 0x44 };
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(_ =>
            {
                confirmationCount++;
                File.WriteAllBytes(foreign, foreignBytes);
                return true;
            }));
            RoutedClick(unifiedBatchButton);
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "一括処理エラー", TimeSpan.FromSeconds(20));
            Require(confirmationCount == 3 && errorCount == 1 &&
                    errorMessage.Contains("原本は変更していません", StringComparison.Ordinal) &&
                    File.ReadAllBytes(foreign).AsSpan().SequenceEqual(foreignBytes) &&
                    !mergeButton.IsEnabled && !stopButton.IsEnabled,
                "R2.17 part 15: confirmation-time foreign output safely refuses with preserved evidence");
            File.Delete(foreign); // Only disposable test-created synthetic file.

            // Retry the exact WPF button with an empty output. A fresh explicit
            // No still must be required rather than auto-reusing old hints.
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(_ =>
            {
                confirmationCount++;
                return false;
            }));
            RoutedClick(unifiedBatchButton);
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "確認でキャンセル（未実行）", TimeSpan.FromSeconds(20));
            Require(confirmationCount == 4 && toolPickerCount == 6 &&
                    outputPickerCount == 5 && errorCount == 1 &&
                    OracleHash(sourcePng) == beforePng &&
                    OracleHash(sourceJpeg) == beforeJpeg &&
                    !Directory.EnumerateFileSystemEntries(previewOutput).Any() &&
                    !mergeButton.IsEnabled,
                "R2.17 part 15: repeat attempt still selects tool/output and explicitly refuses");

            // R2.17 part 16: successfully run BOTH real preview engines through
            // the original WPF Click event. No test-executable substitution:
            // Actions supplies its independently pinned official jpegtran.exe.
            // Outside official-source Windows CI we skip (not pretend tested).
            string? officialTool = Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN");
            bool liveCombinedRan = false;
            bool liveCombinedResumed = false;
            bool routedPngStop = false, routedJpegStop = false;
            bool partialPngPreserved = false, partialJpegPreserved = false;
            bool stoppedBatchFullyResumed = false, stoppedBatchOriginalsUnchanged = false;
            int partialPngCandidates = 0, partialJpegCandidates = 0;
            int finalStoppedPngCandidates = 0, finalStoppedJpegCandidates = 0;
            int liveSourceCount = 0, pngCandidateCount = 0, jpegCandidateCount = 0;
            if (!string.IsNullOrWhiteSpace(officialTool))
            {
                Require(File.Exists(officialTool) &&
                        Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") ==
                            "7b4993786d856cbccabfc0ed2f923ba000e326cc",
                    "R2.17 part 16: WPF batch uses CI-built officially pinned jpegtran, not synthetic tool");

                string realInput = Path.Combine(testRoot, "generated-real-input");
                string realOutput = Path.Combine(testRoot, "generated-real-output");
                Directory.CreateDirectory(realInput);
                Directory.CreateDirectory(realOutput);
                for (int i = 0; i < 6; i++)
                {
                    WriteGeneratedJpeg(Path.Combine(realInput, $"image-{i:D2}.jpg"),
                        960, 640, i);
                    WriteGeneratedUncompressedPng(Path.Combine(realInput, $"image-{i:D2}.png"),
                        512, 320, i + 10);
                }
                string[] originalsReal = Directory.GetFiles(realInput).OrderBy(x => x).ToArray();
                var originalHashesReal = originalsReal.ToDictionary(x => x, OracleHash);
                liveSourceCount = originalsReal.Length;
                Require(liveSourceCount == 12, "R2.17 part 16: six valid WIC JPEG + six valid PNG synthetic sources");

                int liveToolPicks = 0, liveFolderPicks = 0, liveConsents = 0, liveResults = 0;
                string liveResultBody = "";
                SetPrivate(window, "_pickCombinedTool", new Func<string?, string?>(_ =>
                {
                    liveToolPicks++;
                    return officialTool;
                }));
                SetPrivate(window, "_pickCombinedOutput", new Func<string?, string?>(_ =>
                {
                    liveFolderPicks++;
                    return realOutput;
                }));
                SetPrivate(window, "_confirmCombined", new Func<string, bool>(body =>
                {
                    liveConsents++;
                    Require(body.Contains("PNG: 6件", StringComparison.Ordinal) &&
                            body.Contains("JPG/JPEG: 6件", StringComparison.Ordinal) &&
                            body.Contains("元ファイルの置換・削除・統合は行いません", StringComparison.Ordinal),
                        "R2.17 part 16: WPF approval explicitly displays both formats and no original replacement");
                    return true;
                }));
                SetPrivate(window, "_showCombinedResult", new Action<string>(body =>
                {
                    liveResults++;
                    liveResultBody = body;
                }));
                pathBox.Text = realInput;

                RoutedClick(unifiedBatchButton);
                Require(stopButton.IsEnabled && !unifiedBatchButton.IsEnabled &&
                        !mergeButton.IsEnabled,
                    "R2.17 part 16: real combined-click becomes busy with Stop enabled");
                PumpUntil(() => liveResults == 1 && unifiedBatchButton.IsEnabled &&
                    progressText.Text == "PNG/JPG候補作成終了", TimeSpan.FromSeconds(120));
                var firstState = CombinedBatchResumeInspector.Inspect(
                    realInput, realOutput, officialTool);
                string pngJournalPath = Path.Combine(realOutput, ".chappy-png-batch-r16.progress.jsonl");
                string jpegJournalPath = Path.Combine(realOutput, ".chappy-jpeg-r19.progress.jsonl");
                byte[] pngJournalBeforeResume = File.ReadAllBytes(pngJournalPath);
                byte[] jpegJournalBeforeResume = File.ReadAllBytes(jpegJournalPath);
                string[] pngCandidates = Directory.GetFiles(
                    Path.Combine(realOutput, "chappy-png-previews"), "*.png");
                string[] jpegCandidates = Directory.GetFiles(
                    Path.Combine(realOutput, "chappy-jpeg-r19-previews"), "*.jpg");
                pngCandidateCount = pngCandidates.Length;
                jpegCandidateCount = jpegCandidates.Length;
                var savedCandidates = pngCandidates.Concat(jpegCandidates).ToDictionary(x => x, OracleHash);
                Require(firstState.HasPngPlan && firstState.HasJpegPlan &&
                        firstState.PngPlanEntries == 6 && firstState.JpegPlanEntries == 6 &&
                        pngCandidateCount >= 4 && jpegCandidateCount >= 3 &&
                        errorCount == 1 && liveToolPicks == 1 && liveFolderPicks == 1 &&
                        liveConsents == 1 && liveResultBody.Contains("実際の空き容量が増えたわけではありません", StringComparison.Ordinal) &&
                        jobProgress.Value == 100 && !stopButton.IsEnabled &&
                        !mergeButton.IsEnabled &&
                        originalsReal.All(x => OracleHash(x) == originalHashesReal[x]),
                    "R2.17 part 16: real pinned JPEG and PNG generated candidates via WPF, 100% UI, source hashes unchanged");
                liveCombinedRan = true;

                // Rerun the same real Click. Every tool/output/Yes must be
                // selected again; both engines must reuse the saved candidates.
                RoutedClick(unifiedBatchButton);
                PumpUntil(() => liveResults == 2 && unifiedBatchButton.IsEnabled &&
                    progressText.Text == "PNG/JPG候補作成終了", TimeSpan.FromSeconds(120));
                var resumedState = CombinedBatchResumeInspector.Inspect(
                    realInput, realOutput, officialTool);
                Require(liveToolPicks == 2 && liveFolderPicks == 2 && liveConsents == 2 &&
                        resumedState == firstState &&
                        Directory.GetFiles(Path.Combine(realOutput, "chappy-png-previews"), "*.png").Length ==
                            pngCandidateCount &&
                        Directory.GetFiles(Path.Combine(realOutput, "chappy-jpeg-r19-previews"), "*.jpg").Length ==
                            jpegCandidateCount &&
                        savedCandidates.All(x => File.Exists(x.Key) && OracleHash(x.Key) == x.Value) &&
                        File.ReadAllBytes(pngJournalPath).AsSpan().SequenceEqual(pngJournalBeforeResume) &&
                        File.ReadAllBytes(jpegJournalPath).AsSpan().SequenceEqual(jpegJournalBeforeResume) &&
                        originalsReal.All(x => OracleHash(x) == originalHashesReal[x]) &&
                        !mergeButton.IsEnabled && !stopButton.IsEnabled &&
                        jobProgress.Value == 100 && errorCount == 1,
                    "R2.17 part 16: real WPF restart resumes PNG/JPEG without new candidates or journal edits");
                liveCombinedResumed = true;

                // Part 17 uses a SECOND isolated output root, so the verified
                // Part 16 complete/resume evidence is never modified by
                // stop/restart tests. The real first PNG completion triggers
                // an in-process test tap ON THE WORKER. Dispatcher.Invoke raises
                // the real routed Stop Click before the next engine entry.
                // No timers, sleep, filesystem watcher or timing race.
                string stoppedOutput = Path.Combine(testRoot, "stopped-and-resumed-output");
                Directory.CreateDirectory(stoppedOutput);
                SetPrivate(window, "_pickCombinedOutput", new Func<string?, string?>(_ =>
                {
                    liveFolderPicks++;
                    return stoppedOutput;
                }));
                int pngStopClicks = 0, jpegStopClicks = 0;
                SetPrivate(window, "_combinedPngProgressTestTap",
                    new Action<PngBatchPreviewEngine.Progress>(p =>
                    {
                        if (pngStopClicks != 0 || p.Done != 1 || p.Status != "Completed")
                            return;
                        window.Dispatcher.Invoke(() =>
                        {
                            Require(stopButton.IsEnabled && !mergeButton.IsEnabled,
                                "R2.17 part 17: real WPF Stop enabled between PNG candidate items");
                            RoutedClick(stopButton);
                            pngStopClicks++;
                        });
                    }));
                int earlierResults = liveResults;
                RoutedClick(unifiedBatchButton);
                PumpUntil(() => unifiedBatchButton.IsEnabled &&
                    progressText.Text == "中断（再開可能）", TimeSpan.FromSeconds(120));
                ClearPrivate(window, "_combinedPngProgressTestTap");
                string pngPlanPath = Path.Combine(stoppedOutput, ".chappy-png-batch-r16.json");
                string pngProgressPath = Path.Combine(stoppedOutput, ".chappy-png-batch-r16.progress.jsonl");
                string jpegPlanPath = Path.Combine(stoppedOutput, ".chappy-jpeg-r19.plan.json");
                var afterPngStop = CombinedBatchResumeInspector.Inspect(
                    realInput, stoppedOutput, officialTool);
                string[] partialPngFiles = Directory.GetFiles(
                    Path.Combine(stoppedOutput, "chappy-png-previews"), "*.png");
                partialPngCandidates = partialPngFiles.Length;
                string singlePngCandidateHash = partialPngFiles.Length == 1 ?
                    OracleHash(partialPngFiles[0]) : "";
                Require(pngStopClicks == 1 && afterPngStop.HasPngPlan &&
                        afterPngStop.PngPlanEntries == 6 && !afterPngStop.HasJpegPlan &&
                        !File.Exists(jpegPlanPath) && partialPngCandidates == 1 &&
                        File.Exists(pngPlanPath) && new FileInfo(pngProgressPath).Length > 0 &&
                        liveResults == earlierResults && !stopButton.IsEnabled &&
                        !mergeButton.IsEnabled && originalsReal.All(x => OracleHash(x) == originalHashesReal[x]),
                    "R2.17 part 17: routed Stop after first real PNG candidate saves one resumable plan/journal without starting JPEG");
                routedPngStop = true;

                // Resume PNG from its journal, then route another genuine Stop
                // after exactly one real official-jpegtran candidate. PNG
                // candidate #1 must retain its original exact bytes.
                SetPrivate(window, "_combinedJpegProgressTestTap",
                    new Action<JpegBatchPreviewEngine.Progress>(p =>
                    {
                        if (jpegStopClicks != 0 || p.Done != 1 || p.Status != "Completed")
                            return;
                        window.Dispatcher.Invoke(() =>
                        {
                            Require(stopButton.IsEnabled && !mergeButton.IsEnabled,
                                "R2.17 part 17: real WPF Stop enabled between JPEG candidate items");
                            RoutedClick(stopButton);
                            jpegStopClicks++;
                        });
                    }));
                RoutedClick(unifiedBatchButton);
                PumpUntil(() => unifiedBatchButton.IsEnabled &&
                    progressText.Text == "中断（再開可能）", TimeSpan.FromSeconds(120));
                ClearPrivate(window, "_combinedJpegProgressTestTap");
                var afterJpegStop = CombinedBatchResumeInspector.Inspect(
                    realInput, stoppedOutput, officialTool);
                string jpegProgressPath = Path.Combine(stoppedOutput, ".chappy-jpeg-r19.progress.jsonl");
                string[] partialJpegFiles = Directory.GetFiles(
                    Path.Combine(stoppedOutput, "chappy-jpeg-r19-previews"), "*.jpg");
                partialJpegCandidates = partialJpegFiles.Length;
                string singleJpegCandidateHash = partialJpegFiles.Length == 1 ?
                    OracleHash(partialJpegFiles[0]) : "";
                partialPngPreserved = partialPngFiles.Length == 1 &&
                    File.Exists(partialPngFiles[0]) &&
                    OracleHash(partialPngFiles[0]) == singlePngCandidateHash;
                Require(jpegStopClicks == 1 && afterJpegStop.HasPngPlan &&
                        afterJpegStop.HasJpegPlan &&
                        afterJpegStop.PngPlanEntries == 6 && afterJpegStop.JpegPlanEntries == 6 &&
                        partialJpegCandidates == 1 &&
                        Directory.GetFiles(Path.Combine(stoppedOutput, "chappy-png-previews"), "*.png").Length == 6 &&
                        new FileInfo(jpegProgressPath).Length > 0 && partialPngPreserved &&
                        liveResults == earlierResults && !stopButton.IsEnabled &&
                        !mergeButton.IsEnabled && originalsReal.All(x => OracleHash(x) == originalHashesReal[x]),
                    "R2.17 part 17: routed Stop after first official JPEG candidate retains all resumed PNG and one JPEG");
                routedJpegStop = true;

                // Resume a THIRD time, with neither stop tap installed. Both
                // formats must finish and preserve BOTH earlier candidates.
                RoutedClick(unifiedBatchButton);
                PumpUntil(() => liveResults == earlierResults + 1 &&
                    unifiedBatchButton.IsEnabled &&
                    progressText.Text == "PNG/JPG候補作成終了", TimeSpan.FromSeconds(120));
                var finallyResumed = CombinedBatchResumeInspector.Inspect(
                    realInput, stoppedOutput, officialTool);
                finalStoppedPngCandidates = Directory.GetFiles(
                    Path.Combine(stoppedOutput, "chappy-png-previews"), "*.png").Length;
                finalStoppedJpegCandidates = Directory.GetFiles(
                    Path.Combine(stoppedOutput, "chappy-jpeg-r19-previews"), "*.jpg").Length;
                partialJpegPreserved = partialJpegFiles.Length == 1 &&
                    File.Exists(partialJpegFiles[0]) &&
                    OracleHash(partialJpegFiles[0]) == singleJpegCandidateHash;
                stoppedBatchOriginalsUnchanged =
                    originalsReal.All(x => OracleHash(x) == originalHashesReal[x]);
                Require(finallyResumed.HasPngPlan && finallyResumed.HasJpegPlan &&
                        finallyResumed.PngPlanEntries == 6 && finallyResumed.JpegPlanEntries == 6 &&
                        finalStoppedPngCandidates == 6 && finalStoppedJpegCandidates == 6 &&
                        partialPngPreserved && partialJpegPreserved &&
                        stoppedBatchOriginalsUnchanged &&
                        !stopButton.IsEnabled && !mergeButton.IsEnabled &&
                        jobProgress.Value == 100 && errorCount == 1 &&
                        liveToolPicks == 5 && liveFolderPicks == 5 &&
                        liveConsents == 5,
                    "R2.17 part 17: two mid-batch routed Stops followed by full PNG/JPEG resume preserve exact candidates and sources");
                stoppedBatchFullyResumed = true;
            }
            else Console.WriteLine("R2.17 part 16-17 official jpegtran WPF success/stop/resume SKIPPED outside pinned Windows CI.");

            // Part 21: real WPF Yes path with only the displayed SOURCE
            // inventory changing while the native-default dialog adapter
            // responds. This must fail before creating any PNG/JPG plan or
            // executing the deliberately fake jpegtran.exe.
            pathBox.Text = previewInputs;
            SetPrivate(window, "_pickCombinedTool",
                new Func<string?, string?>(_ => syntheticTool));
            SetPrivate(window, "_pickCombinedOutput",
                new Func<string?, string?>(_ => previewOutput));
            string addedAfterYes = Path.Combine(previewInputs, "added-after-yes.png");
            int priorErrors = errorCount;
            bool yesInjectedNewSource = false;
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(_ =>
            {
                Require(!File.Exists(addedAfterYes),
                    "R2.17 part 21: source-injection fixture starts absent");
                File.Copy(sourcePng, addedAfterYes);
                yesInjectedNewSource = true;
                return true;
            }));
            RoutedClick(unifiedBatchButton);
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "一括処理エラー", TimeSpan.FromSeconds(20));
            bool sourceConsentRefused = yesInjectedNewSource &&
                errorCount == priorErrors + 1 &&
                errorMessage.Contains("入力画像", StringComparison.Ordinal) &&
                OracleHash(sourcePng) == beforePng &&
                OracleHash(sourceJpeg) == beforeJpeg &&
                File.Exists(addedAfterYes) &&
                OracleHash(addedAfterYes) == beforePng &&
                !Directory.EnumerateFileSystemEntries(previewOutput).Any() &&
                !stopButton.IsEnabled && !mergeButton.IsEnabled;
            Require(sourceConsentRefused,
                "R2.17 part 21: real WPF Yes rejects newly added input without running any codec or writing a plan");
            File.Delete(addedAfterYes); // only test-created synthetic source

            // Part 22: the original Yes and both UI rechecks have passed.
            // Inject at the exact dispatch boundary BEFORE the real PNG
            // engine starts, then prove its independent gate refuses before
            // any plan or lock file is created. No fake codec is invoked.
            SetPrivate(window, "_confirmCombined", new Func<string, bool>(_ => true));
            string postUiSource = Path.Combine(previewInputs, "source-added-after-final-recheck.png");
            int errorsBeforeLateSource = errorCount;
            SetPrivate(window, "_combinedBeforePngEngineTestTap", new Action(() =>
                File.Copy(sourcePng, postUiSource)));
            RoutedClick(unifiedBatchButton);
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "一括処理エラー", TimeSpan.FromSeconds(20));
            bool part22LateSourceRefused = errorCount == errorsBeforeLateSource + 1 &&
                File.Exists(postUiSource) && OracleHash(postUiSource) == beforePng &&
                errorMessage.Contains("入力画像", StringComparison.Ordinal) &&
                !Directory.EnumerateFileSystemEntries(previewOutput).Any() &&
                OracleHash(sourcePng) == beforePng && OracleHash(sourceJpeg) == beforeJpeg &&
                !stopButton.IsEnabled && !mergeButton.IsEnabled;
            Require(part22LateSourceRefused,
                "R2.17 part 22: real WPF final-check to engine-entry source race refuses BEFORE any plan/lock");
            File.Delete(postUiSource);

            string postUiForeign = Path.Combine(previewOutput, "foreign-after-final-recheck.txt");
            byte[] lateEvidence = { 0xDD, 0xC0, 0x22, 0x45 };
            int errorsBeforeForeign = errorCount;
            SetPrivate(window, "_combinedBeforePngEngineTestTap", new Action(() =>
                File.WriteAllBytes(postUiForeign, lateEvidence)));
            RoutedClick(unifiedBatchButton);
            PumpUntil(() => unifiedBatchButton.IsEnabled &&
                progressText.Text == "一括処理エラー", TimeSpan.FromSeconds(20));
            bool part22LateOutputRefused = errorCount == errorsBeforeForeign + 1 &&
                errorMessage.Contains("未確認のファイル", StringComparison.Ordinal) &&
                File.ReadAllBytes(postUiForeign).SequenceEqual(lateEvidence) &&
                Directory.EnumerateFileSystemEntries(previewOutput).Count() == 1 &&
                OracleHash(sourcePng) == beforePng && OracleHash(sourceJpeg) == beforeJpeg &&
                !stopButton.IsEnabled && !mergeButton.IsEnabled;
            Require(part22LateOutputRefused,
                "R2.17 part 22: real WPF final-check to engine-entry foreign output race refuses WITHOUT artifact creation");
            File.Delete(postUiForeign);
            SetPrivate(window, "_combinedBeforePngEngineTestTap", (Action?)null);

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-wpf-routed-click.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-wpf-routed-click-v1",
                    generatedOnly = true,
                    syntheticInputFiles = 2,
                    syntheticBytesPerFile = BytesPerFile,
                    applicationXamlLoaded = true,
                    scanStartRoutedClickVerified = true,
                    stopRoutedClickVerified = true,
                    cancellationTokenSignaledByActualHandler = true,
                    canceledUiReturnedToIdle = true,
                    canceledUiHasNoPartialGroups = true,
                    secondScanCompletedRoutedClick = true,
                    previewButtonsDisabledDuringScanAndRestoredAfterCancellation = true,
                    previewButtonsRestoredAfterSuccess = true,
                    safeR217PreviewOnlyTitleShown = true,
                    completedUiDuplicateGroups = 1,
                    completedUiProgressPercent = 100,
                    intermediateHashUiSampled = intermediateHashUiSeen,
                    sourceOracleSha256Unchanged = true,
                    combinedToolPickerCancelRefused = true,
                    combinedOutputPickerCancelRefused = true,
                    combinedNoConsentRefused = true,
                    combinedCancellationDuringConsentRefused = true,
                    combinedLateForeignFileRefused = true,
                    combinedRetryRequiresNewConsent = true,
                    syntheticCombinedInputHashesUnchanged = true,
                    officialJpegtranWpfBatchActuallyExecuted = liveCombinedRan,
                    officialJpegtranWpfBatchResumeVerified = liveCombinedResumed,
                    realWpfBatchSourceFiles = liveSourceCount,
                    realWpfBatchPngCandidates = pngCandidateCount,
                    realWpfBatchJpegCandidates = jpegCandidateCount,
                    part17RoutedPngMidBatchStopVerified = routedPngStop,
                    part17RoutedJpegMidBatchStopVerified = routedJpegStop,
                    part17PartialPngCandidatePreserved = partialPngPreserved,
                    part17PartialJpegCandidatePreserved = partialJpegPreserved,
                    part17TwoStopsThenFullResumeVerified = stoppedBatchFullyResumed,
                    part17StoppedBatchSourceSha256Unchanged = stoppedBatchOriginalsUnchanged,
                    part17PngStoppedCandidateCount = partialPngCandidates,
                    part17JpegStoppedCandidateCount = partialJpegCandidates,
                    part17FinalPngCandidateCount = finalStoppedPngCandidates,
                    part17FinalJpegCandidateCount = finalStoppedJpegCandidates,
                    part17PhysicalMouseStopClickVerified = false,
                    part21RoutedWpfYesSourceChangeRefused = sourceConsentRefused,
                    part21NoPreviewPlanCreatedAfterSourceChange = true,
                    part21SyntheticSourceHashesUnchanged = OracleHash(sourcePng) == beforePng &&
                        OracleHash(sourceJpeg) == beforeJpeg,
                    part22RoutedSourceAddedAfterFinalUiRecheckRefused = part22LateSourceRefused,
                    part22RoutedForeignOutputAfterFinalUiRecheckRefused = part22LateOutputRefused,
                    part22NoLockPlanOrCandidateCreatedForEitherRace = true,
                    part22UntrustedForeignOutputBytesPreserved = true,
                    part23ReadOnlyDedupeUndoPlanRoutedClickVerified = part23WpfPlanSafe,
                    part23MergeStillDisabledAfterPreview = !mergeButton.IsEnabled,
                    part23ScanSourceHashesAndNtfsIdentitiesPreserved =
                        OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                        NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                        NativeFile.GetIdentity(right).SameFile(beforeRightId),
                    actualNativeDialogAutomationVerified = false,
                    actualPhysicalMouseClickVerified = false,
                    interactiveDesktopDisplayed = false,
                    jpegLosslessCandidateMeasured = false,
                    productionHardlinkMergeEnabled = false,
                    productionOriginalReplacementEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 WPF ROUTED CLICK PASS: start/stop/cancel, " +
                "second scan complete, 1 exact group, original SHA256 unchanged");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("R2.17 WPF ROUTED CLICK FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { window?.Close(); } catch { }
            try { application?.Shutdown(); } catch { }
            try
            {
                typeof(Journal).GetProperty("TestFolderPath",
                    BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null);
            }
            catch { }
            try { Directory.Delete(testRoot, recursive: true); }
            catch { Console.Error.WriteLine("Test cleanup failed for disposable root"); }
        }
    }

    private static byte[] GeneratedPixels(int width, int height, int variant)
    {
        var pixels = new byte[checked(width * height * 4)];
        var random = new Random(21717000 + variant);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4;
            pixels[p] = variant % 3 == 1 ? (byte)random.Next(256) :
                (byte)((x / 11 + variant * 7) & 255);
            pixels[p + 1] = variant % 3 == 1 ? (byte)random.Next(256) :
                (byte)((y / 9 + variant * 13) & 255);
            pixels[p + 2] = variant % 3 == 1 ? (byte)random.Next(256) :
                (byte)(((x + y) / 15 + variant * 19) & 255);
            pixels[p + 3] = 255;
        }
        return pixels;
    }

    private static void WriteGeneratedJpeg(string path, int width, int height, int variant)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, GeneratedPixels(width, height, variant), width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 89 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> bytes)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (byte b in header[4..8])
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        Span<byte> digest = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(digest, ~crc);
        output.Write(digest);
    }

    private static void WriteGeneratedUncompressedPng(string path, int width,
        int height, int variant)
    {
        byte[] bgra = GeneratedPixels(width, height, variant);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; header[9] = 6;
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                raw.WriteByte(bgra[p + 2]);
                raw.WriteByte(bgra[p + 1]);
                raw.WriteByte(bgra[p]);
                raw.WriteByte(bgra[p + 3]);
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.NoCompression,
            leaveOpen: true))
            raw.WriteTo(z);
        using var output = File.Create(path);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WritePngChunk(output, "IHDR", header);
        WritePngChunk(output, "tEXt", "Comment\0Generated R2.17 WPF batch"u8);
        WritePngChunk(output, "IDAT", compressed.ToArray());
        WritePngChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static void ClearPrivate(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing private progress tap: " + name);
        field.SetValue(window, null);
    }

    private static void RoutedClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void SetPrivate<T>(MainWindow window, string name, T value)
        where T : Delegate
    {
        var field = typeof(MainWindow).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Instance) ??
            throw new InvalidOperationException("Missing private combined adapter: " + name);
        field.SetValue(window, value);
    }

    private static T Named<T>(MainWindow window, string name) where T : class =>
        window.FindName(name) as T ??
        throw new InvalidOperationException("WPF XAML control not found: " + name);

    private static string OracleHash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static void Require(bool assertion, string description)
    {
        if (!assertion) throw new InvalidOperationException(description);
        Console.WriteLine("[PASS] " + description);
    }

    private static void PumpUntil(Func<bool> condition, TimeSpan timeout,
        Action? sample = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        bool expired = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(12)
        };
        var clock = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            sample?.Invoke();
            if (condition()) frame.Continue = false;
            else if (clock.Elapsed > timeout)
            {
                expired = true;
                frame.Continue = false;
            }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (expired || !condition())
            throw new TimeoutException("WPF routed scan did not reach a final UI state");
    }
}
