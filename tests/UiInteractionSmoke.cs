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
            var jobProgress = Named<ProgressBar>(window, "JobProgress");
            var progressText = Named<TextBlock>(window, "ProgressText");
            var groupsGrid = Named<DataGrid>(window, "GroupsGrid");
            var pathBox = Named<TextBox>(window, "PathBox");

            Require(!mergeButton.IsEnabled &&
                    !stopButton.IsEnabled && scanButton.IsEnabled,
                "Production merge stays disabled, Scan enabled, Stop initially disabled");

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
            Require(OracleHash(left) == beforeA &&
                    OracleHash(right) == beforeB &&
                    Directory.EnumerateFiles(originals).Count() == 2,
                "Routed WPF clicks left two independent synthetic source files unchanged");

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
                    completedUiDuplicateGroups = 1,
                    completedUiProgressPercent = 100,
                    intermediateHashUiSampled = intermediateHashUiSeen,
                    sourceOracleSha256Unchanged = true,
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
