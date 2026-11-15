using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
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
            var unifiedBatchButton = Named<Button>(window, "UnifiedBatchButton");
            var pngBatchButton = Named<Button>(window, "PngBatchButton");
            var jpegBatchButton = Named<Button>(window, "JpegBatchButton");
            var jobProgress = Named<ProgressBar>(window, "JobProgress");
            var progressText = Named<TextBlock>(window, "ProgressText");
            var groupsGrid = Named<DataGrid>(window, "GroupsGrid");
            var pathBox = Named<TextBox>(window, "PathBox");

            Require(!mergeButton.IsEnabled &&
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
                    !mergeButton.IsEnabled &&
                    groupsGrid.Items.Count == 1,
                "Completed scan has one valid duplicate group and 100% WPF progress");
            Require(unifiedBatchButton.IsEnabled && pngBatchButton.IsEnabled &&
                    jpegBatchButton.IsEnabled && !mergeButton.IsEnabled,
                "After completed Scan, all preview-only batch controls return to idle availability");
            Require(OracleHash(left) == beforeA &&
                    OracleHash(right) == beforeB &&
                    Directory.EnumerateFiles(originals).Count() == 2,
                "Routed WPF clicks left two independent synthetic source files unchanged");

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
