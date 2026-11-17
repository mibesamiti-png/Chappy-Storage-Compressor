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
    private static int Main(string[] args)
    {
        if (args is ["--v1-isolated"])
            return RunV1Isolated();
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
            application = CreateSmokeApp();
            window = new MainWindow();
            // x:Name fields are internal in the production app assembly.
            // Resolve real WPF named controls from the loaded XAML namescope.
            var mainTabs = Named<TabControl>(window, "MainTabs");
            var mediaOnly = Named<CheckBox>(window, "MediaOnlyCheck");
            Require(mainTabs.Items.Count == 3 && mainTabs.SelectedIndex == 0 &&
                    mediaOnly.IsChecked == false &&
                    ((TabItem)mainTabs.Items[0]).Header?.ToString() == "重複整理" &&
                    ((TabItem)mainTabs.Items[1]).Header?.ToString() == "画像圧縮" &&
                    ((TabItem)mainTabs.Items[2]).Header?.ToString() == "履歴・復元",
                "UI: three focused tabs, duplicate-first and all-file scanning by default");
            var scanButton = Named<Button>(window, "ScanButton");
            var stopButton = Named<Button>(window, "StopButton");
            var mergeButton = Named<Button>(window, "MergeButton");
            var dedupePlanButton = Named<Button>(window, "DedupePlanButton");
            var selectedMergeReviewButton = Named<Button>(window, "SelectedMergeReviewButton");
            var completedLedgerButton = Named<Button>(window, "CompletedLedgerButton");
            var unifiedBatchButton = Named<Button>(window, "UnifiedBatchButton");
            var pngBatchButton = Named<Button>(window, "PngBatchButton");
            var jpegBatchButton = Named<Button>(window, "JpegBatchButton");
            var jobProgress = Named<ProgressBar>(window, "JobProgress");
            var progressText = Named<TextBlock>(window, "ProgressText");
            var groupsGrid = Named<DataGrid>(window, "GroupsGrid");
            var pathBox = Named<TextBox>(window, "PathBox");
            var dashboardText = Named<TextBlock>(window, "ReadOnlyDashboardText");
            Require(dashboardText.Text.Contains("未スキャン", StringComparison.Ordinal),
                "G1-05: newly opened WPF dashboard never implies that a scan has run");

            Require(!mergeButton.IsEnabled && !dedupePlanButton.IsEnabled &&
                    !selectedMergeReviewButton.IsEnabled &&
                    completedLedgerButton.IsEnabled &&
                    !stopButton.IsEnabled && scanButton.IsEnabled,
                "Production merge stays disabled, Scan enabled, Stop initially disabled");
            Require(window.Title.Contains("Chappy Storage Compressor", StringComparison.Ordinal) &&
                    unifiedBatchButton.IsEnabled && pngBatchButton.IsEnabled &&
                    jpegBatchButton.IsEnabled,
                "Actual WPF UI shows R2.17 and preview-only batch actions enabled while idle");

            // UI acceptance: scanning all extensions is real behavior, not only a checkbox.
            // The new detail window must accept non-image duplicates, list all
            // paths and never make a mutating action available.
            string allFileRoot = Path.Combine(testRoot, "all-file-types");
            Directory.CreateDirectory(allFileRoot);
            string textFile = Path.Combine(allFileRoot, "document.txt");
            string binaryFile = Path.Combine(allFileRoot, "document-copy.bin");
            File.WriteAllText(textFile, "generated all-extension duplicate fixture");
            File.Copy(textFile, binaryFile);
            File.SetCreationTimeUtc(binaryFile, File.GetCreationTimeUtc(textFile));
            File.SetLastWriteTimeUtc(binaryFile, File.GetLastWriteTimeUtc(textFile));
            var allFileResult = Scanner.Run(allFileRoot, mediaOnly.IsChecked == true,
                null, CancellationToken.None);
            Require(allFileResult.Groups.Count == 1 &&
                    allFileResult.Groups[0].Files.Count == 2 &&
                    allFileResult.Groups[0].Files.All(x => x.Identity.LinkCount == 1),
                "UI: all-extension default finds a byte-exact TXT/BIN pair");
            var detailsType = typeof(MainWindow).Assembly.GetType(
                "ChappyStorageCompressor.DuplicateGroupDetailsWindow", throwOnError: true)!;
            using (var _ = new MemoryStream()) // no persistent state or user file writes
            {
                var detail = (Window)Activator.CreateInstance(detailsType,
                    BindingFlags.NonPublic | BindingFlags.Instance, binder: null,
                    args: new object[] { allFileResult.Groups[0] }, culture: null)!;
                var detailGrid = (DataGrid)detailsType.GetField("_grid",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(detail)!;
                var message = (TextBlock)detailsType.GetField("_previewMessage",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(detail)!;
                Require(detailGrid.Items.Count == 2 &&
                        message.Text.Contains("画像以外のプレビューは表示しません",
                            StringComparison.Ordinal) &&
                        detail.Title.Contains("読み取り専用", StringComparison.Ordinal),
                    "UI: full duplicate group details handle non-images read-only");
                detail.Close();
            }
            Require(File.Exists(textFile) && File.Exists(binaryFile) &&
                    OracleHash(textFile) == OracleHash(binaryFile),
                "UI: read-only duplicate inspector preserves both original files");

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
            Require(dashboardText.Text.Contains("完全一致 1組", StringComparison.Ordinal) &&
                    dashboardText.Text.Contains("統合条件一致 1組", StringComparison.Ordinal) &&
                    dashboardText.Text.Contains("二重計上は未評価", StringComparison.Ordinal) &&
                    dashboardText.Text.Contains("未実行（0 B、物理空き容量は未計測）",
                        StringComparison.Ordinal) &&
                    !mergeButton.IsEnabled,
                "G1-05: real WPF scan dashboard separates eligible duplicates, unverified preview overlap and unmeasured recovery");
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

            // B2 G2-07: real WPF selected-row consent and final recheck.
            // Consent is only an acknowledgement, NEVER permission for the
            // public Merge button or internal Core transaction.
            groupsGrid.SelectedIndex = 0;
            Require(selectedMergeReviewButton.IsEnabled && !mergeButton.IsEnabled,
                "B2 G2-07: selecting a scanned group enables ONLY read-only confirmation");
            string selectedRiskText = "", afterRiskText = "";
            SetPrivate(window, "_confirmSelectedMergeRisk",
                new Func<string, bool>(message =>
                {
                    selectedRiskText = message;
                    return true; // consent still MUST NOT mutate data
                }));
            SetPrivate(window, "_showDedupePlan",
                new Action<string>(message => afterRiskText = message));
            RoutedClick(selectedMergeReviewButton);
            Require(!selectedMergeReviewButton.IsEnabled && !mergeButton.IsEnabled,
                "B2 G2-07: selected confirmation enters busy state without enabling Merge");
            PumpUntil(() => selectedMergeReviewButton.IsEnabled &&
                progressText.Text == "再検証済み・本番統合は無効",
                TimeSpan.FromSeconds(35));
            bool selectedConsentReadOnly =
                selectedRiskText.Contains("共有化後", StringComparison.Ordinal) &&
                selectedRiskText.Contains("独立Undoに必要な追加容量", StringComparison.Ordinal) &&
                selectedRiskText.Contains("対象ドライブの確認時空き", StringComparison.Ordinal) &&
                selectedRiskText.Contains(left, StringComparison.OrdinalIgnoreCase) &&
                selectedRiskText.Contains(right, StringComparison.OrdinalIgnoreCase) &&
                afterRiskText.Contains("最終再検証OK", StringComparison.Ordinal) &&
                afterRiskText.Contains("本番統合は実行できません", StringComparison.Ordinal) &&
                !mergeButton.IsEnabled &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId) &&
                !NativeFile.SameFile(left, right) &&
                !Directory.Exists(Safety.StorePath(originals));
            Require(selectedConsentReadOnly,
                "B2 G2-07: routed selected confirmation explains shared edits and Undo capacity, revalidates source, never changes NTFS files");
            // Even a canceled risk dialog must leave all original IDs intact.
            SetPrivate(window, "_confirmSelectedMergeRisk",
                new Func<string, bool>(_ => false));
            RoutedClick(selectedMergeReviewButton);
            PumpUntil(() => selectedMergeReviewButton.IsEnabled &&
                progressText.Text == "確認を中止（未実行）",
                TimeSpan.FromSeconds(35));
            Require(!mergeButton.IsEnabled &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId) &&
                !Directory.Exists(Safety.StorePath(originals)),
                "B2 G2-07: declining selected risk confirmation does not create a store or link");

            // G2-07: even reflection-enabling the PRIVATE test flag on an
            // ordinary non-sentinel folder can only return the read-only plan.
            var generatedMergeFlag = typeof(MainWindow).GetField(
                "_allowGeneratedOnlyMergeTest", BindingFlags.NonPublic | BindingFlags.Instance) ??
                throw new InvalidOperationException("G2-07 private fixture safety switch missing");
            generatedMergeFlag.SetValue(window, true);
            SetPrivate(window, "_confirmSelectedMergeRisk",
                new Func<string, bool>(_ => true));
            afterRiskText = "";
            RoutedClick(selectedMergeReviewButton);
            PumpUntil(() => selectedMergeReviewButton.IsEnabled &&
                progressText.Text == "再検証済み・本番統合は無効",
                TimeSpan.FromSeconds(35));
            bool g207NormalRootForcedFlagStillLocked =
                !mergeButton.IsEnabled &&
                afterRiskText.Contains("本番統合は実行できません",
                    StringComparison.Ordinal) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId) &&
                !Directory.Exists(Safety.StorePath(originals));
            Require(g207NormalRootForcedFlagStillLocked,
                "G2-07: forced private fixture flag on non-sentinel root cannot authorize any merge");
            generatedMergeFlag.SetValue(window, false);
            // The INTERNAL Core must refuse a non-sentinel root even if a
            // caller bypasses the WPF gate and supplies a genuine scan group.
            var internalBatchMerge = typeof(CompletedGroupLedger).GetMethod(
                "MergeVerifiedGroups", BindingFlags.NonPublic | BindingFlags.Static) ??
                throw new InvalidOperationException("G2-07 normal-compiled shared Core missing");
            bool g207CoreDeniedNormalRoot = false;
            try
            {
                internalBatchMerge.Invoke(null,
                    [originals, new[] { ((GroupRow)groupsGrid.Items[0]).Group },
                        CancellationToken.None]);
            }
            catch (TargetInvocationException ex) when
                (ex.InnerException is InvalidOperationException)
            {
                g207CoreDeniedNormalRoot = true;
            }
            Require(g207CoreDeniedNormalRoot &&
                !Directory.Exists(Safety.StorePath(originals)) &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId),
                "G2-07: internal batch Core itself rejects non-sentinel root before any store or original changes");

            // G2-07: the SAME shipped WPF SelectedMergeReviewButton is now
            // tested against a disposable 3-alias and 2-alias NTFS selection.
            // Distinct original file IDs are saved before any Core write.
            // This is not a physical mouse test and never touches user media.
            string mergeRoot = Path.Combine(testRoot, "ChappyStorageSmoke_G207WpfMerge");
            Directory.CreateDirectory(mergeRoot);
            File.WriteAllText(Path.Combine(mergeRoot,
                ".chappy-part23-generated-only"), "Part23 synthetic data only");
            string firstMerge = Path.Combine(mergeRoot, "fixture-a.mp4");
            string secondMerge = Path.Combine(mergeRoot, "fixture-b.mp4");
            string thirdMerge = Path.Combine(mergeRoot, "fixture-c.mp4");
            string fourthMerge = Path.Combine(mergeRoot, "fixture-d.jpg");
            string fifthMerge = Path.Combine(mergeRoot, "fixture-e.jpg");
            byte[] generatedMp4 = new byte[512 * 1024];
            byte[] generatedJpeg = new byte[256 * 1024];
            new Random(2207).NextBytes(generatedMp4);
            new Random(2208).NextBytes(generatedJpeg);
            File.WriteAllBytes(firstMerge, generatedMp4);
            File.WriteAllBytes(fourthMerge, generatedJpeg);
            foreach (string alias in new[] { secondMerge, thirdMerge })
            {
                File.Copy(firstMerge, alias);
                File.SetCreationTimeUtc(alias, File.GetCreationTimeUtc(firstMerge));
                File.SetLastWriteTimeUtc(alias, File.GetLastWriteTimeUtc(firstMerge));
            }
            File.Copy(fourthMerge, fifthMerge);
            File.SetCreationTimeUtc(fifthMerge, File.GetCreationTimeUtc(fourthMerge));
            File.SetLastWriteTimeUtc(fifthMerge, File.GetLastWriteTimeUtc(fourthMerge));
            string[] mergePaths =
                [firstMerge, secondMerge, thirdMerge, fourthMerge, fifthMerge];
            var preMerge = mergePaths.ToDictionary(path => path,
                path => (Hash: OracleHash(path), Id: NativeFile.GetIdentity(path)),
                StringComparer.OrdinalIgnoreCase);
            Require(preMerge.Values.Select(x => x.Id.FileIndex).Distinct().Count() == 5,
                "G2-07: 5 disposable inputs start with 5 independent NTFS file IDs");
            var mergeView = new MainWindow();
            var g2Scan = Named<Button>(mergeView, "ScanButton");
            var g2Selected = Named<Button>(mergeView, "SelectedMergeReviewButton");
            var g2Merge = Named<Button>(mergeView, "MergeButton");
            var g2Groups = Named<DataGrid>(mergeView, "GroupsGrid");
            var g2Path = Named<TextBox>(mergeView, "PathBox");
            var g2Status = Named<TextBlock>(mergeView, "ProgressText");
            Require(!g2Merge.IsEnabled,
                "G2-07: ordinary production MergeButton is locked before generated fixture test");
            g2Path.Text = mergeRoot;
            RoutedClick(g2Scan);
            PumpUntil(() => g2Scan.IsEnabled && g2Status.Text == "スキャン完了",
                TimeSpan.FromSeconds(35));
            Require(g2Groups.Items.Count == 2,
                "G2-07: WPF scanner selected two byte-exact groups on real generated NTFS");
            g2Groups.SelectedItems.Add(g2Groups.Items[0]);
            g2Groups.SelectedItems.Add(g2Groups.Items[1]);
            Require(g2Selected.IsEnabled && !g2Merge.IsEnabled,
                "G2-07: both generated groups are selected, public Merge stays locked");
            generatedMergeFlag.SetValue(mergeView, true);
            string g207RiskText = "", g207ResultText = "";
            bool g207AllowConsent = false;
            int g207ConsentCalls = 0;
            SetPrivate(mergeView, "_confirmSelectedMergeRisk",
                new Func<string, bool>(message =>
                {
                    g207RiskText = message;
                    g207ConsentCalls++;
                    return g207AllowConsent;
                }));
            SetPrivate(mergeView, "_showDedupePlan",
                new Action<string>(message => g207ResultText = message));
            RoutedClick(g2Selected);
            PumpUntil(() => g2Selected.IsEnabled &&
                g2Status.Text == "確認を中止（未実行）",
                TimeSpan.FromSeconds(35));
            bool g207NoConsentPreserved = g207ConsentCalls == 1 &&
                !Directory.Exists(Safety.StorePath(mergeRoot)) &&
                mergePaths.All(path =>
                    OracleHash(path) == preMerge[path].Hash &&
                    NativeFile.GetIdentity(path).SameFile(preMerge[path].Id));
            Require(g207NoConsentPreserved,
                "G2-07: routed No keeps two generated groups' original IDs and store absent");
            g207AllowConsent = true;
            g207ConsentCalls = 0;
            RoutedClick(g2Selected);
            PumpUntil(() => g2Status.Text ==
                "生成テスト統合完了・本番統合は無効",
                TimeSpan.FromSeconds(60));
            var mergedLedger = CompletedGroupLedger.Inspect(mergeRoot);
            bool g207RealWpfCoreMerged =
                g207ConsentCalls == 2 &&
                g207RiskText.Contains("実統合・最終同意",
                    StringComparison.Ordinal) &&
                g207ResultText.Contains("グループ 2件",
                    StringComparison.Ordinal) &&
                g207ResultText.Contains("対象ドライブ空き容量",
                    StringComparison.Ordinal) &&
                mergedLedger.VerifiedCompleted == 2 &&
                mergedLedger.NeedsReview == 0 &&
                NativeFile.SameFile(firstMerge, secondMerge) &&
                NativeFile.SameFile(firstMerge, thirdMerge) &&
                NativeFile.SameFile(fourthMerge, fifthMerge) &&
                !NativeFile.SameFile(firstMerge, fourthMerge) &&
                mergePaths.All(path => OracleHash(path) == preMerge[path].Hash) &&
                !g2Merge.IsEnabled && !g2Selected.IsEnabled &&
                CompletedGroupLedger.InspectUndoCandidates(mergeRoot)
                    .Count(x => x.ReviewReady) == 2;
            Require(g207RealWpfCoreMerged,
                "G2-07: routed selection/2 consents/fresh source verification reaches real shared Core and persists two groups (3+2) with independent Undo candidates");
            mergeView.Close();

            // Part 24: read-only persisted-history check via genuine XAML
            // Click; an empty store must not be created just to inspect it.
            string ledgerBody = "";
            SetPrivate(window, "_showCompletedLedger",
                new Action<string>(body => ledgerBody = body));
            RoutedClick(completedLedgerButton);
            Require(!completedLedgerButton.IsEnabled && !mergeButton.IsEnabled,
                "R2.17 part24: actual WPF completed-ledger read-only handler enters busy state");
            PumpUntil(() => completedLedgerButton.IsEnabled &&
                progressText.Text == "統合履歴を確認（未変更）",
                TimeSpan.FromSeconds(25));
            bool part24WpfReadOnlyLedger = ledgerBody.Contains("検証済み統合: 0件",
                    StringComparison.Ordinal) &&
                ledgerBody.Contains("本番統合・本番独立Undoは無効",
                    StringComparison.Ordinal) &&
                ledgerBody.Contains("独立Undo中断履歴：読み取り専用",
                    StringComparison.Ordinal) &&
                ledgerBody.Contains("未完了: 0件", StringComparison.Ordinal) &&
                ledgerBody.Contains("元ファイルごとの記録：確認済み 0 件", StringComparison.Ordinal) &&
                !Directory.Exists(Safety.StorePath(originals)) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId) &&
                !NativeFile.SameFile(left, right) &&
                !mergeButton.IsEnabled;
            Require(part24WpfReadOnlyLedger,
                "R2.17 part24: production WPF ledger inspector shows verified-empty history without any media/store mutations");

            // Part30: actual RecoveryWindow XAML with separate non-actionable
            // history tab. Use the routed Loaded event in hosted CI; no
            // physical mouse, real desktop, or production source mutation.
            var recoveryView = new RecoveryWindow(originals);
            var historyTabs = recoveryView.FindName("RecoveryTabs") as TabControl ??
                throw new InvalidOperationException("Part30 WPF RecoveryTabs missing");
            var historyGrid = recoveryView.FindName("HistoryGrid") as DataGrid ??
                throw new InvalidOperationException("Part30 WPF HistoryGrid missing");
            var recoveryApply = recoveryView.FindName("ApplyButton") as Button ??
                throw new InvalidOperationException("Part30 WPF ApplyButton missing");
            var undoReviewButton = recoveryView.FindName("UndoReviewButton") as Button ??
                throw new InvalidOperationException("G3 WPF UndoReviewButton missing");
            var undoExecuteButton = recoveryView.FindName("UndoExecuteButton") as Button ??
                throw new InvalidOperationException("G3-07 WPF generated-only UndoExecuteButton missing");
            var historyStatus = recoveryView.FindName("HistorySummaryText") as TextBlock ??
                throw new InvalidOperationException("Part30 WPF history summary missing");
            var collisionGrid = recoveryView.FindName("CollisionGrid") as DataGrid ??
                throw new InvalidOperationException("Part36 WPF read-only collision grid missing");
            var collisionStatus = recoveryView.FindName("CollisionSummaryText") as TextBlock ??
                throw new InvalidOperationException("Part36 WPF collision status missing");
            Require(historyTabs.Items.Count == 4 && historyGrid.IsReadOnly &&
                    collisionGrid.IsReadOnly &&
                    !recoveryApply.IsEnabled && !mergeButton.IsEnabled,
                "R2.17 part30: real recovery XAML separates journal actions from immutable evidence history");
            recoveryView.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => historyStatus.Text.Contains("対象フォルダ履歴",
                StringComparison.Ordinal), TimeSpan.FromSeconds(25));
            historyTabs.SelectedIndex = 3;
            var imageReview = (Button)recoveryView.FindName("ImageReceiptReviewButton");
            var imageText = (TextBlock)recoveryView.FindName("ImageReceiptReviewText");
            string badImageReceipt = Path.Combine(Path.GetDirectoryName(originals)!, ".chappy-g4-preparation.json");
            File.WriteAllText(badImageReceipt, "{");
            var imagePicker = typeof(RecoveryWindow).GetField("_pickImageReceipt",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("G4 image review picker missing");
            imagePicker.SetValue(recoveryView, (Func<RecoveryWindow, string?>)(_ => badImageReceipt));
            imageReview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => imageReview.IsEnabled &&
                imageText.Text.Contains("要手動確認", StringComparison.Ordinal),
                TimeSpan.FromSeconds(15));
            Require(!recoveryApply.IsEnabled && !undoExecuteButton.IsEnabled &&
                File.ReadAllText(badImageReceipt) == "{" &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB,
                "G4-06: RecoveryWindow routes a corrupt image receipt to read-only manual review");

            historyTabs.SelectedIndex = 1;
            bool part30WpfHistoryReadOnly = historyGrid.IsReadOnly &&
                historyGrid.Items.Count == 0 &&
                historyStatus.Text.Contains("要確認・未完了 0 件", StringComparison.Ordinal) &&
                historyStatus.Text.Contains("本番統合・独立Undoは無効",
                    StringComparison.Ordinal) &&
                !recoveryApply.IsEnabled && !mergeButton.IsEnabled &&
                !Directory.Exists(Safety.StorePath(originals)) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId);
            Require(part30WpfHistoryReadOnly,
                "R2.17 part30: recovery history tab routed Loaded refresh never enables Apply or mutates originals/store");
            Require(!undoReviewButton.IsEnabled,
                "G3-01: absent authenticated completed history cannot enable Undo identification");
            Require(!undoExecuteButton.IsEnabled,
                "G3-07: normal product always locks Undo execution even with WPF history selected");
            // Simulate a stale UI row, NOT a fabricated disk receipt.
            // The actual routed click MUST reopen the persisted folder and
            // refuse the row; no previous scan object may authorize Undo.
            var historyRows = historyGrid.ItemsSource as
                System.Collections.ObjectModel.ObservableCollection<RecoveryEvidenceOverview.Row>
                ?? throw new InvalidOperationException("G3 read-only history source missing");
            var staleUndoRow = new RecoveryEvidenceOverview.Row(
                "複数ファイルのグループ履歴", Guid.NewGuid().ToString("N"),
                "VerifiedCompleted", "STA test-only stale row (no disk manifest)", false);
            historyRows.Add(staleUndoRow);
            historyGrid.SelectedItem = staleUndoRow;
            Require(undoReviewButton.IsEnabled && !recoveryApply.IsEnabled,
                "G3-01: history group selection enables only read-only reconstruction, never Apply");
            RoutedClick(undoReviewButton);
            var undoDetail = recoveryView.FindName("DetailText") as TextBlock ??
                throw new InvalidOperationException("G3 WPF history detail missing");
            PumpUntil(() => undoReviewButton.IsEnabled &&
                undoDetail.Text.Contains("元ファイルを安全なUndo候補として再特定できません",
                    StringComparison.Ordinal),
                TimeSpan.FromSeconds(25));
            bool g3NoStaleUndoAuthorization =
                !recoveryApply.IsEnabled && !mergeButton.IsEnabled &&
                !Directory.Exists(Safety.StorePath(originals)) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId);
            Require(g3NoStaleUndoAuthorization,
                "G3-01: real WPF Undo preview rejects a forged in-memory history row with no durable manifest and never changes originals");
            historyGrid.SelectedItem = null;
            historyRows.Remove(staleUndoRow);
            PumpUntil(() => collisionStatus.Text.Contains("同名ファイル調査",
                StringComparison.Ordinal), TimeSpan.FromSeconds(25));
            historyTabs.SelectedIndex = 2;
            bool part36WpfReadOnly = collisionGrid.IsReadOnly &&
                collisionGrid.Items.Count == 0 &&
                collisionStatus.Text.Contains("別フォルダの記録は未検査",
                    StringComparison.Ordinal) &&
                !recoveryApply.IsEnabled && !mergeButton.IsEnabled &&
                !Directory.Exists(Path.Combine(originals, ".chappy-collision-journal")) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId);
            Require(part36WpfReadOnly,
                "R2.17 part36: real WPF read-only collision evidence tab has no apply, creates no journal and preserves existing file IDs");
            var priorityFilter = recoveryView.FindName("CollisionPriorityFilter")
                as ComboBox ?? throw new InvalidOperationException("B1 Part39 priority filter missing");
            var priorityCounts = recoveryView.FindName("CollisionPriorityCounts")
                as TextBlock ?? throw new InvalidOperationException("B1 Part39 priority counts missing");
            bool part39EmptyPriority = priorityFilter.Items.Count == 5 &&
                priorityFilter.SelectedIndex == 0 &&
                priorityCounts.Text.Contains("U 未判定範囲 1件",
                    StringComparison.Ordinal) &&
                !recoveryApply.IsEnabled && collisionGrid.Items.Count == 0;
            priorityFilter.SelectedIndex = 1; // P0
            bool part39FilterSafe = collisionGrid.Items.Count == 0 &&
                !recoveryApply.IsEnabled && !mergeButton.IsEnabled;
            priorityFilter.SelectedIndex = 0; // All, for existing Part37/38 smoke
            Require(part39EmptyPriority && part39FilterSafe &&
                !Directory.Exists(Path.Combine(originals, ".chappy-collision-journal")) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB,
                "B1 Part39: real WPF priority filtering shows unverified empty scope and never enables Apply");
            // A deterministic *visualization row* validates the actual WPF
            // selection and guidance display; it is not fake file evidence.
            // Real NTFS evidence classification is covered by Part36/37 Core.
            var triageRows = collisionGrid.ItemsSource as
                System.Collections.ObjectModel.ObservableCollection<CollisionEvidenceReview.Row>
                ?? throw new InvalidOperationException("Part37 WPF collision source missing");
            var mockTriage = new CollisionEvidenceReview.Row(
                "操作記録", "part37-generated-ui-row", "", "", "ManualReview",
                "同じ台帳に不明な証拠があるため操作全体は未検証", true);
            triageRows.Add(mockTriage);
            collisionGrid.SelectedItem = mockTriage;
            var detail = recoveryView.FindName("DetailText") as TextBlock ??
                throw new InvalidOperationException("Part37 WPF details panel missing");
            bool part37GuidanceShown = detail.Text.Contains("【確認するときの注意】",
                    StringComparison.Ordinal) &&
                detail.Text.Contains("台帳全体に未完了・不整合",
                    StringComparison.Ordinal) &&
                detail.Text.Contains("再開・削除・名前変更・自動修復はできません",
                    StringComparison.Ordinal) &&
                !recoveryApply.IsEnabled && collisionGrid.IsReadOnly &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId);
            Require(part37GuidanceShown,
                "R2.17 part37: actual WPF collision selection shows safe per-record guidance and cannot enable Apply");
            bool part38ChecklistShown =
                detail.Text.Contains("【復旧可否の確認項目：読み取り専用】",
                    StringComparison.Ordinal) &&
                detail.Text.Contains("表示行が最新証拠と不一致",
                    StringComparison.Ordinal) &&
                detail.Text.Contains("本番ファイルへの変更：禁止",
                    StringComparison.Ordinal) &&
                detail.Text.Contains("自動修復・再開・復元許可は発行しません",
                    StringComparison.Ordinal) &&
                collisionStatus.Text.Contains("本番の再開・上書き・削除は禁止",
                    StringComparison.Ordinal) &&
                !recoveryApply.IsEnabled && collisionGrid.IsReadOnly &&
                !Directory.Exists(Path.Combine(originals, ".chappy-collision-journal")) &&
                OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                NativeFile.GetIdentity(right).SameFile(beforeRightId);
            Require(part38ChecklistShown &&
                    detail.Text.Contains("【調査の優先度と次の確認】",
                        StringComparison.Ordinal) &&
                    detail.Text.Contains("U 未判定", StringComparison.Ordinal),
                "R2.17 part38 + B1 Part39: selected evidence displays checklist and conservative triage guidance without enabling Apply");
            recoveryView.Close();

            // G3-07: REAL production WPF XAML routed click to the SAME
            // internal Undo Core, BUT ONLY with an explicitly test-owned
            // generated TEMP+sentinel root and a private default-OFF test
            // flag. No real user file can be selected here.
            string undoRoot = Path.Combine(testRoot, "ChappyStorageSmoke_UndoWpfGroup");
            Directory.CreateDirectory(undoRoot);
            File.WriteAllText(Path.Combine(undoRoot, ".chappy-part23-generated-only"),
                "Part23 synthetic data only");
            string a = Path.Combine(undoRoot, "group-a.mp4");
            File.WriteAllBytes(a, new byte[512 * 1024]);
            foreach (string name in new[] { "group-b.mp4", "group-c.mp4" })
            {
                string path = Path.Combine(undoRoot, name);
                File.Copy(a, path);
                File.SetCreationTimeUtc(path, File.GetCreationTimeUtc(a));
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(a));
            }
            var undoScan = Scanner.Run(undoRoot, true, null, CancellationToken.None);
            Require(undoScan.Groups.Count == 1 && undoScan.Groups.Single().Files.Count == 3,
                "G3-07: WPF fixture uses genuine scanner group with 3 independent generated files");
            var originalUndo = undoScan.Groups.Single().Files.ToDictionary(x => x.Path,
                StringComparer.OrdinalIgnoreCase);
            string originalUndoHash = OracleHash(a);
            // Internal Core is normal-compiled, not a duplicate test engine.
            var internalMerge = typeof(CompletedGroupLedger).GetMethod(
                "MergeVerifiedGroup", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("G3-07 common internal Core merge unavailable");
            string undoOperationId = (string)(internalMerge.Invoke(null,
                [undoRoot, undoScan.Groups.Single(), CancellationToken.None]) ??
                    throw new InvalidOperationException("G3-07 generated group merge failed"));
            Require(CompletedGroupLedger.Inspect(undoRoot).VerifiedCompleted == 1,
                "G3-07: actual compiled Core created a durable completed group before WPF Undo");
            Directory.CreateDirectory(Path.Combine(undoRoot, "g3-wpf-undo-evidence"));
            var undoView = new RecoveryWindow(undoRoot);
            var uiFlag = typeof(RecoveryWindow).GetField("_allowGeneratedOnlyUndoTest",
                BindingFlags.NonPublic | BindingFlags.Instance) ??
                throw new InvalidOperationException("G3-07 WPF synthetic test gate missing");
            uiFlag.SetValue(undoView, true);
            string routedUndoPrompt = "", routedUndoResult = "";
            var consentField = typeof(RecoveryWindow).GetField("_confirmGeneratedOnlyUndo",
                BindingFlags.NonPublic | BindingFlags.Instance) ??
                throw new InvalidOperationException("G3-07 consent adapter missing");
            var resultField = typeof(RecoveryWindow).GetField("_showGeneratedOnlyUndo",
                BindingFlags.NonPublic | BindingFlags.Instance) ??
                throw new InvalidOperationException("G3-07 result adapter missing");
            consentField.SetValue(undoView,
                new Func<string, bool>(message =>
                {
                    routedUndoPrompt = message;
                    return false;
                }));
            resultField.SetValue(undoView,
                new Action<string>(message => routedUndoResult = message));
            var execute = undoView.FindName("UndoExecuteButton") as Button ??
                throw new InvalidOperationException("G3-07 routed Undo execution button missing");
            var undoTabs = undoView.FindName("RecoveryTabs") as TabControl ??
                throw new InvalidOperationException("G3-07 Undo tabs missing");
            var undoHistory = undoView.FindName("HistoryGrid") as DataGrid ??
                throw new InvalidOperationException("G3-07 Undo group history grid missing");
            var undoHistorySummary = undoView.FindName("HistorySummaryText") as TextBlock ??
                throw new InvalidOperationException("G3-07 Undo history summary missing");
            var undoCancel = undoView.FindName("UndoCancelButton") as Button ??
                throw new InvalidOperationException("G3-07 generated-only Undo cancel button missing");
            Require(!undoCancel.IsEnabled,
                "G3-07: cancel button starts disabled and cannot authorize normal Undo");
            var undoWpfDetail = undoView.FindName("DetailText") as TextBlock ??
                throw new InvalidOperationException("G3-07 Undo detail missing");
            var undoLegacyApply = undoView.FindName("ApplyButton") as Button ??
                throw new InvalidOperationException("G3-07 legacy Apply button missing");
            undoView.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => undoHistorySummary.Text.Contains("対象フォルダ履歴",
                StringComparison.Ordinal), TimeSpan.FromSeconds(35));
            undoTabs.SelectedIndex = 1;
            var actualGroupRow = undoHistory.Items.Cast<object>()
                .OfType<RecoveryEvidenceOverview.Row>().Single(x =>
                    x.Source == "複数ファイルのグループ履歴" &&
                    x.Target == undoOperationId &&
                    x.State == "VerifiedCompleted");
            undoHistory.SelectedItem = actualGroupRow;
            Require(execute.IsEnabled && !undoLegacyApply.IsEnabled &&
                !mergeButton.IsEnabled,
                "G3-07: generated-only routed WPF Undo enables only verified group while regular Apply/Merge stay disabled");
            RoutedClick(execute);
            PumpUntil(() => undoWpfDetail.Text.Contains("最終確認を辞退",
                StringComparison.Ordinal), TimeSpan.FromSeconds(35));
            bool undoConsentRejected = routedUndoPrompt.Contains("元NTFS ID=",
                    StringComparison.Ordinal) &&
                routedUndoPrompt.Contains("追加で必要な容量", StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(routedUndoPrompt) &&
                CompletedGroupLedger.Inspect(undoRoot).VerifiedCompleted == 1 &&
                originalUndo.All(x =>
                    OracleHash(x.Key) == originalUndoHash &&
                    NativeFile.SameFile(x.Key, a));
            Require(undoConsentRejected,
                "G3-07: declining explicit routed WPF Undo consent preserves all generated shared links and hashes");
            consentField.SetValue(undoView,
                new Func<string, bool>(message =>
                {
                    routedUndoPrompt = message;
                    return true;
                }));
            // G3-07: inject a routed cancel AFTER the first durable
            // independent-alias checkpoint, never in the middle of an
            // uncommitted user-file write. The same normal-compiled Core and
            // WPF Click handler are used; no second recovery engine.
            var durableAliasHook = typeof(RecoveryWindow).GetField(
                "_afterGeneratedUndoAliasForTest",
                BindingFlags.NonPublic | BindingFlags.Instance) ??
                throw new InvalidOperationException("G3-07 durable UI test probe missing");
            bool routedCancelAtDurableAlias = false;
            durableAliasHook.SetValue(undoView, new Action<int>(count =>
            {
                if (count != 1) return;
                undoView.Dispatcher.Invoke(() =>
                {
                    routedCancelAtDurableAlias = undoCancel.IsEnabled;
                    if (undoCancel.IsEnabled) RoutedClick(undoCancel);
                });
            }));
            RoutedClick(execute);
            PumpUntil(() => undoHistorySummary.Text.Contains("【生成テストUndo中断】",
                StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            var canceledState = CompletedGroupLedger.Inspect(undoRoot);
            bool durablePartialUndoPreserved = routedCancelAtDurableAlias &&
                canceledState.ExplicitResumeRequired == 1 &&
                canceledState.VerifiedRestored == 0 &&
                originalUndo.All(x => OracleHash(x.Key) == originalUndoHash) &&
                undoScan.Groups.Single().Files.Count(x =>
                    !x.Path.Equals(undoScan.Groups.Single().Representative.Path,
                        StringComparison.OrdinalIgnoreCase) &&
                    !NativeFile.SameFile(x.Path,
                        undoScan.Groups.Single().Representative.Path)) == 1 &&
                !undoCancel.IsEnabled && !undoLegacyApply.IsEnabled &&
                undoWpfDetail.Text.Contains("明示的に再開", StringComparison.Ordinal);
            Require(durablePartialUndoPreserved,
                "G3-07: genuine routed Cancel retains one verified detached alias and durable explicit-resume receipt");
            durableAliasHook.SetValue(undoView, null);
            var explicitResumeRow = undoHistory.Items.Cast<object>()
                .OfType<RecoveryEvidenceOverview.Row>().Single(x =>
                    x.Source == "複数ファイルのグループ履歴" &&
                    x.Target == undoOperationId &&
                    x.State == "ExplicitResumeRequired" && x.NeedsAttention);
            undoHistory.SelectedItem = explicitResumeRow;
            Require(execute.IsEnabled && !undoCancel.IsEnabled,
                "G3-07: flagged explicit-resume history is selectable only after fresh evidence checks");
            RoutedClick(execute);
            PumpUntil(() => undoHistorySummary.Text.Contains("【生成テストUndo成功】",
                StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            bool restoredCapacityAndIdsVisible =
                routedUndoResult.Contains("空き容量", StringComparison.Ordinal) &&
                routedUndoResult.Contains("現NTFS ID", StringComparison.Ordinal) &&
                routedUndoResult.Contains("Owner/DACL", StringComparison.Ordinal) &&
                undoHistorySummary.Text.Contains("空き容量の観測差",
                    StringComparison.Ordinal);
            bool realWpfUndo = CompletedGroupLedger.Inspect(undoRoot).VerifiedRestored == 1 &&
                routedUndoResult.Contains("独立化した別名 2件", StringComparison.Ordinal) &&
                originalUndo.All(x =>
                {
                    var after = Safety.Capture(x.Key);
                    return OracleHash(x.Key) == originalUndoHash &&
                        after.CreationUtc == x.Value.CreationUtc &&
                        after.ModifiedUtc == x.Value.ModifiedUtc &&
                        after.Attributes == x.Value.Attributes &&
                        after.SecurityDescriptor.AsSpan().SequenceEqual(
                            x.Value.SecurityDescriptor);
                }) &&
                undoScan.Groups.Single().Files.Where(x =>
                    !x.Path.Equals(undoScan.Groups.Single().Representative.Path,
                        StringComparison.OrdinalIgnoreCase)).All(x =>
                        !NativeFile.SameFile(x.Path,
                            undoScan.Groups.Single().Representative.Path) &&
                        NativeFile.GetIdentity(x.Path).LinkCount == 1) &&
                NativeFile.GetIdentity(
                    undoScan.Groups.Single().Representative.Path).LinkCount == 2 &&
                !execute.IsEnabled && !undoLegacyApply.IsEnabled;
            Require(realWpfUndo && restoredCapacityAndIdsVisible,
                "G3-07: canceled WPF Undo explicitly resumes with all independent NTFS IDs/bytes/ACL and observed capacity, while release Apply stays locked");
            undoView.Close();

            // G3-05: immediately after the REAL WPF-triggered Core Undo,
            // use the EXACT normal-compiled internal collision writer.
            // This proves the shipped binary contains the one existing
            // create-new/receipt/replay implementation, not a test-only copy.
            string collisionOutput = Path.Combine(undoRoot, "g3-wpf-export");
            Directory.CreateDirectory(collisionOutput);
            string occupiedB = Path.Combine(collisionOutput, "group-b.mp4");
            string occupiedC = Path.Combine(collisionOutput, "group-c.mp4");
            File.WriteAllText(occupiedB, "FOREIGN WPF EXISTING B");
            File.WriteAllText(occupiedC, "FOREIGN WPF EXISTING C");
            string preExistingMemo = Path.Combine(collisionOutput, "同名ファイル一覧.txt");
            File.WriteAllText(preExistingMemo, "EXISTING USER TXT - NEVER REPLACE");
            var foreignBId = NativeFile.GetIdentity(occupiedB);
            var foreignCId = NativeFile.GetIdentity(occupiedC);
            var foreignBHash = OracleHash(occupiedB);
            var foreignCHash = OracleHash(occupiedC);
            var oldMemoId = NativeFile.GetIdentity(preExistingMemo);
            var beginExport = typeof(CollisionRecoveryLedger).GetMethod(
                "BeginRestoredUndoExportSynthetic",
                BindingFlags.NonPublic | BindingFlags.Static) ??
                throw new InvalidOperationException(
                    "G3-05 collision writer not compiled into normal product");
            var resumeExport = typeof(CollisionRecoveryLedger).GetMethod(
                "ResumeSynthetic", BindingFlags.NonPublic | BindingFlags.Static) ??
                throw new InvalidOperationException(
                    "G3-05 collision replay not compiled into normal product");
            Require(typeof(CollisionRecoveryLedger).GetMethod(
                    "BeginRestoredUndoExportSynthetic",
                    BindingFlags.Public | BindingFlags.Static) is null &&
                !CollisionRecoveryLedger.ProductionMutationEnabled,
                "G3-05: normal product has NO public collision-export mutation entrypoint");
            bool rejectedUserRoot = false;
            try
            {
                beginExport.Invoke(null, [originals, undoOperationId, collisionOutput]);
            }
            catch (TargetInvocationException ex) when
                (ex.InnerException is InvalidOperationException)
            {
                rejectedUserRoot = true;
            }
            Require(rejectedUserRoot &&
                !Directory.Exists(Path.Combine(collisionOutput,
                    ".chappy-collision-journal")),
                "G3-05: normal-compiled collision writer refuses regular user root before journal or output creation");
            string collisionId = (string)(beginExport.Invoke(null,
                [undoRoot, undoOperationId, collisionOutput]) ??
                throw new InvalidDataException("G3-05 synthetic collision intent not created"));
            var exportResult = resumeExport.Invoke(null,
                [undoRoot, collisionOutput, collisionId, null])
                as CollisionRecoveryLedger.Status ??
                throw new InvalidDataException("G3-05 normal collision export failed");
            var exportEvidence = CollisionEvidenceReview.Inspect(collisionOutput);
            var exportFiles = exportEvidence.Rows.Where(x =>
                x.Kind == "ファイル" && !x.NeedsAttention).ToArray();
            var exportedMemo = Path.Combine(collisionOutput,
                "同名ファイル一覧 (1).txt");
            byte[] noteBytes = File.ReadAllBytes(exportedMemo);
            bool normalWpfToCollision = exportResult.State == "VerifiedCompleted" &&
                exportResult.VerifiedFiles == 2 &&
                exportEvidence.NeedsReview == 0 &&
                exportFiles.Length == 2 &&
                exportFiles.Any(x => x.DestinationRelativePath.StartsWith(
                    "同名ファイル" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)) &&
                exportFiles.All(x =>
                {
                    string src = Path.Combine(undoRoot, x.OriginalRelativePath);
                    string dst = Path.Combine(collisionOutput, x.DestinationRelativePath);
                    return File.Exists(src) && File.Exists(dst) &&
                        OracleHash(src) == OracleHash(dst) &&
                        !NativeFile.SameFile(src, dst);
                }) &&
                OracleHash(occupiedB) == foreignBHash &&
                OracleHash(occupiedC) == foreignCHash &&
                NativeFile.GetIdentity(occupiedB).SameFile(foreignBId) &&
                NativeFile.GetIdentity(occupiedC).SameFile(foreignCId) &&
                File.ReadAllText(preExistingMemo) ==
                    "EXISTING USER TXT - NEVER REPLACE" &&
                NativeFile.GetIdentity(preExistingMemo).SameFile(oldMemoId) &&
                noteBytes.Length >= 3 &&
                noteBytes[0] == 0xEF && noteBytes[1] == 0xBB &&
                noteBytes[2] == 0xBF &&
                CompletedGroupLedger.Inspect(undoRoot).VerifiedRestored == 1;
            Require(normalWpfToCollision,
                "G3-05: real routed WPF Undo -> NORMAL-compiled guarded collision Core preserves existing output IDs and numbered UTF-8 BOM TXT");

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

            // Verify the installed WinExe's Core boundary, including direct callers.
            foreach (string methodName in new[] { "ApplyPreparedImage", "RestorePreparedImage" })
            {
                var imageMutation = typeof(ReplacementReadiness).GetMethod(
                    methodName, BindingFlags.NonPublic | BindingFlags.Static) ??
                    throw new InvalidOperationException("G4 normal-build image Core missing");
                bool productionDenied = false;
                try
                {
                    imageMutation.Invoke(null, [left, right, originals, originals]);
                }
                catch (TargetInvocationException ex) when
                    (ex.InnerException is NotSupportedException)
                {
                    productionDenied = true;
                }
                Require(productionDenied && OracleHash(left) == beforeA && OracleHash(right) == beforeB,
                    "G4 production image mutation refuses direct calls before touching files");
            }

            // R2.17 part 16: successfully run BOTH real preview engines through
            // the original WPF Click event. No test-executable substitution:
            // Actions supplies its independently pinned official jpegtran.exe.
            // Outside official-source Windows CI we skip (not pretend tested).
            // G4-02 product WinExe embeds the release SIMD executable SHA,
            // not the separately CI-built non-SIMD benchmarking hash.
            string? officialTool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
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
                string? embeddedToolSha = typeof(JpegLosslessPreviewEngine).Assembly
                    .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
                    .SingleOrDefault(x => x.Key == "Chappy.TrustedJpegtranSha256")?.Value;
                Require(embeddedToolSha is { Length: 64 } &&
                        embeddedToolSha.Equals(
                            Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_SHA256"),
                            StringComparison.OrdinalIgnoreCase),
                    "G4-02: actual compiled WPF WinExe embeds the exact official SIMD tool SHA-256");
                Require(Path.GetFileName(officialTool).Equals("jpegtran.exe",
                            StringComparison.OrdinalIgnoreCase),
                    "G4-02: WPF uses exactly named official SIMD jpegtran with embedded SHA pin");
                // OpenTrustedTool is intentionally INTERNAL in the actual app;
                // the routed WPF batch below verifies that this compiled Core
                // really accepts this exact pinned binary without exposing
                // a new public mutation or executable-launch entrypoint.

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
                string liveResultBody = "", liveFailureBody = "";
                SetPrivate(window, "_showCombinedFailure",
                    new Action<string>(body =>
                    {
                        liveFailureBody = body;
                        // Keep the Part15/21/22 shared WPF failure probe
                        // alive for all subsequent race/safety regressions.
                        errorCount++;
                        errorMessage = body;
                    }));
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
                PumpUntil(() => unifiedBatchButton.IsEnabled &&
                    ((liveResults == 1 && progressText.Text == "PNG/JPG候補作成終了") ||
                     liveFailureBody.Length > 0), TimeSpan.FromSeconds(120));
                Require(liveFailureBody.Length == 0,
                    "G4-02: real WPF JPEG SIMD execution after SHA attestation: " + liveFailureBody);
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

                AssertJpegPreparationAndReview(window, testRoot, realInput, realOutput);

                // G1-05: route the REAL combined-report WPF Click through
                // the same generated JPEG/PNG plans; select the already
                // verified candidate folder without a native CI dialog.
                // Only the optional CSV question is answered No in-process.
                pathBox.Text = realInput;
                RoutedClick(scanButton);
                PumpUntil(() => scanButton.IsEnabled &&
                    progressText.Text == "スキャン完了", TimeSpan.FromSeconds(30));
                string reportPrompt = "";
                SetPrivate(window, "_pickCombinedOutput",
                    new Func<string?, string?>(_ => realOutput));
                SetPrivate(window, "_confirmCombinedReportCsv",
                    new Func<string, bool>(body =>
                    {
                        reportPrompt = body;
                        return false;
                    }));
                var combinedReportButton = Named<Button>(window, "CombinedReportButton");
                RoutedClick(combinedReportButton);
                PumpUntil(() => combinedReportButton.IsEnabled &&
                    progressText.Text == "統合候補レポート完了" &&
                    reportPrompt.Length > 0, TimeSpan.FromSeconds(45));
                Require(dashboardText.Text.Contains("二重計上の補正", StringComparison.Ordinal) &&
                        dashboardText.Text.Contains("重複補正後の合計見込", StringComparison.Ordinal) &&
                        dashboardText.Text.Contains("PNG", StringComparison.Ordinal) &&
                        dashboardText.Text.Contains("JPG", StringComparison.Ordinal) &&
                        dashboardText.Text.Contains("未実行（0 B、物理空き容量は未計測）",
                            StringComparison.Ordinal) &&
                        reportPrompt.Contains("計算値は論理バイト",
                            StringComparison.Ordinal) &&
                        originalsReal.All(x => OracleHash(x) == originalHashesReal[x]) &&
                        savedCandidates.All(x => File.Exists(x.Key) &&
                            OracleHash(x.Key) == x.Value) &&
                        !mergeButton.IsEnabled,
                    "G1-05: real WPF combined-report click presents validated JPEG/PNG and overlap-corrected scan savings without any original or candidate mutation");

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
                    b2SelectedRiskReviewRoutedClickVerified = selectedConsentReadOnly,
                    b2GeneratedOnlySelectedMergeRoutedTwoConsentAndPersistedCore = g207RealWpfCoreMerged,
                    b2NonSentinelWpfAndInternalCoreStillLocked =
                        g207NormalRootForcedFlagStillLocked && g207CoreDeniedNormalRoot,
                    b2GeneratedSelectionNoConsentKeepsOriginalNtfsIds = g207NoConsentPreserved,
                    b2ProductionMergeStillLockedAfterRiskConsent = !mergeButton.IsEnabled,
                    part23MergeStillDisabledAfterPreview = !mergeButton.IsEnabled,
                    part23ScanSourceHashesAndNtfsIdentitiesPreserved =
                        OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                        NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                        NativeFile.GetIdentity(right).SameFile(beforeRightId),
                    part24ActualWpfReadOnlyCompletedLedgerChecked = part24WpfReadOnlyLedger,
                    part24NoStoreCreatedByReadOnlyLedger = !Directory.Exists(Safety.StorePath(originals)),
                    part24ProductionMergeStillLocked = !mergeButton.IsEnabled,
                    part25WpfInterruptedUndoReadOnlyStatusVerified = part24WpfReadOnlyLedger &&
                        ledgerBody.Contains("未完了: 0件", StringComparison.Ordinal),
                    part25ProductionUndoMutationStillDisabled =
                        !IndependentUndoEngine.ProductionEnabled &&
                        !IndependentUndoCrashJournal.ProductionMutationEnabled,
                    part30RecoveryHistoryTabRoutedLoadedVerified = part30WpfHistoryReadOnly,
                    g3ReadOnlyUndoPreviewForgedHistoryRowRefused = g3NoStaleUndoAuthorization,
                    g3UndoReviewButtonCannotEnableLegacyApply = !recoveryApply.IsEnabled,
                    g3GeneratedOnlyRoutedWpfUndoConsentDeniedWithoutMutation = undoConsentRejected,
                    g3GeneratedOnlyRoutedWpfUndoCancelCheckpointAndResume = durablePartialUndoPreserved,
                    g3GeneratedOnlyRoutedWpfUndoObservedCapacityAndIds = restoredCapacityAndIdsVisible,
                    g3GeneratedOnlyRoutedWpfCoreUndoSuccess = realWpfUndo,
                    g3NormalCompiledCollisionExportAfterRoutedWpfUndo =
                        normalWpfToCollision,
                    g3ProductionUndoControlAlwaysDisabledForNormalRoot =
                        !undoExecuteButton.IsEnabled,
                    part30HistoryTabRemainsReadOnly = historyGrid.IsReadOnly,
                    part30NeverEnabledApplyForHistory = !recoveryApply.IsEnabled,
                    part30ReadOnlyInspectionNeverCreatedStore =
                        !Directory.Exists(Safety.StorePath(originals)),
                    part30CrossLedgerDashboardProductionMutationDisabled =
                        !RecoveryEvidenceOverview.ProductionMutationEnabled,
                    part37WpfReadOnlyTriageGuidanceRendered = part37GuidanceShown,
                    part37WpfApplyNeverEnabledFromCollisionTab =
                        !recoveryApply.IsEnabled,
                    part37WpfSourceShaAndNtfsIdentityPreserved =
                        OracleHash(left) == beforeA && OracleHash(right) == beforeB &&
                        NativeFile.GetIdentity(left).SameFile(beforeLeftId) &&
                        NativeFile.GetIdentity(right).SameFile(beforeRightId),
                    part38WpfReadOnlyChecklistVisible = part38ChecklistShown,
                    part38WpfNoRecoveryButtonFromEvidence =
                        !recoveryApply.IsEnabled && collisionGrid.IsReadOnly,
                    part38WpfOriginalShaAndFileIdPreserved =
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

    private static void AssertJpegPreparationAndReview(MainWindow window, string testRoot,
        string input, string output, bool isolated = false)
    {
        string sourceVolume = Path.GetPathRoot(input)!;
        string backupParent = new[] { Environment.GetEnvironmentVariable("RUNNER_TEMP"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) }
            .OfType<string>().First(path => Directory.Exists(path) &&
                !string.Equals(Path.GetPathRoot(path), sourceVolume, StringComparison.OrdinalIgnoreCase) &&
                new DriveInfo(Path.GetPathRoot(path)!).DriveFormat == "NTFS");
        string backup = Directory.CreateDirectory(Path.Combine(backupParent,
            "ChappyStorageSmoke_G4Backup_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string stage = Directory.CreateDirectory(Path.Combine(testRoot, "g4-cutover-wpf")).FullName;
            string backupRoot = backup;
            backup = Directory.CreateDirectory(Path.Combine(backupRoot, "cutover-wpf")).FullName;
            File.WriteAllText(Path.Combine(backupRoot, ".chappy-g4-generated-only"), "generated WPF CI");
            var report = CombinedCapacityReport.Inspect(input, output);
            var row = ReplacementReadiness.Inspect(report, input, output, backup).Rows
                .First(row => row.Format == "JPG" && row.SpaceRequirementsMet);
            string source = Path.Combine(input, row.RelativePath);
            var before = Safety.Capture(source);
            string hash = OracleHash(source);
            string prompt = "";
            string result = "";
            bool allow = false;
            SetPrivate(window, "_pickImagePreparation",
                new Func<string, (string, string, string, string)?>(_ => (source, output, stage, backup)));
            SetPrivate(window, "_confirmImagePreparation", new Func<string, bool>(message =>
            {
                prompt = message;
                return allow;
            }));
            SetPrivate(window, "_showImagePreparation",
                new Action<string, string, MessageBoxImage>((message, _, _) => result = message));
            var prepareButton = Named<Button>(window, "CopyOnlyPreparationButton");

            RoutedClick(prepareButton);
            PumpUntil(() => prepareButton.IsEnabled && prompt.Length > 0, TimeSpan.FromSeconds(45));
            Require(Directory.GetFileSystemEntries(backup).Length == 0 &&
                Directory.GetFileSystemEntries(stage).Length == 0 &&
                prompt.Contains("空き容量は増えません", StringComparison.Ordinal) &&
                prompt.Contains("画素を維持", StringComparison.Ordinal) &&
                Safety.SameSnapshot(before) && OracleHash(source) == hash,
                "G4-06: declining the real WPF JPEG preparation creates no copies or receipt");

            allow = true;
            RoutedClick(prepareButton);
            PumpUntil(() => prepareButton.IsEnabled && result.Length > 0, TimeSpan.FromSeconds(45));
            string receipt = Path.Combine(backup, ".chappy-g4-preparation.json");
            Require(result.Contains("実際に回復した容量は0 B", StringComparison.Ordinal) &&
                File.Exists(receipt) && Directory.GetFiles(backup, "*.bak").Length == 1 &&
                Directory.GetFiles(stage).Length == 1 &&
                Safety.SameSnapshot(before) && OracleHash(source) == hash,
                "G4-06: WPF preparation saves a valid JPEG receipt and two copies without modifying the original: " +
                result);

            var recovery = new RecoveryWindow(input);
            var tabs = (TabControl)recovery.FindName("RecoveryTabs");
            tabs.SelectedItem = recovery.FindName("ImageRecoveryTab");
            var picker = typeof(RecoveryWindow).GetField("_pickImageReceipt",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
                throw new InvalidOperationException("G4 image review picker missing");
            picker.SetValue(recovery, (Func<RecoveryWindow, string?>)(_ => receipt));
            var reviewButton = (Button)recovery.FindName("ImageReceiptReviewButton");
            var reviewText = (TextBlock)recovery.FindName("ImageReceiptReviewText");
            RoutedClick(reviewButton);
            PumpUntil(() => reviewButton.IsEnabled &&
                (reviewText.Text.Contains("準備済み", StringComparison.Ordinal) ||
                 reviewText.Text.Contains("要手動確認", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30));
            Require(reviewText.Text.Contains("準備済み", StringComparison.Ordinal) &&
                reviewText.Text.Contains(source, StringComparison.OrdinalIgnoreCase) &&
                reviewText.Text.Contains($"原本 {row.SourceBytes:N0} B / 圧縮候補 {row.CandidateBytes:N0} B",
                    StringComparison.Ordinal) &&
                reviewText.Text.Contains("この確認で回復した容量 0 B", StringComparison.Ordinal) &&
                !((Button)recovery.FindName("ApplyButton")).IsEnabled &&
                !((Button)recovery.FindName("UndoExecuteButton")).IsEnabled &&
                Safety.SameSnapshot(before) && OracleHash(source) == hash,
                "G4-06: real WPF recovery reopens the valid JPEG receipt with the recorded sizes and actions locked: " +
                reviewText.Text);
            if (isolated)
                AssertIsolatedImageActions(recovery, source, stage, backup, before, hash);
            recovery.Close();
        }
        finally
        {
            string ownedBackup = Path.GetFileName(backup) == "cutover-wpf" ?
                Path.GetDirectoryName(backup)! : backup;
            Directory.Delete(ownedBackup, recursive: true); // Unique generated backup directory only.
        }
    }

    private static App CreateSmokeApp()
    {
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var app = new App();
        app.InitializeComponent();
        return app;
    }

    private static int RunV1Isolated()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("CHAPPY_V1_VHDX_TEMP") is not { } verifiedTemp ||
            !Path.TrimEndingDirectorySeparator(Path.GetTempPath()).Equals(
                Path.TrimEndingDirectorySeparator(verifiedTemp), StringComparison.OrdinalIgnoreCase) ||
            !new DriveInfo(Path.GetPathRoot(verifiedTemp)!).VolumeLabel.Equals(
                "ChappyPart29", StringComparison.Ordinal))
            return 90;
        string root = Directory.CreateDirectory(Path.Combine(verifiedTemp,
            "ChappyStorageSmoke_" + Guid.NewGuid().ToString("N"))).FullName;
        var journal = typeof(Journal).GetProperty("TestFolderPath",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        journal.SetValue(null, Path.Combine(root, "test-journal"));
        var app = CreateSmokeApp();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            string input = Directory.CreateDirectory(Path.Combine(root, "g4-jpeg-input")).FullName;
            string output = Directory.CreateDirectory(Path.Combine(root, "g4-jpeg-output")).FullName;
            WriteGeneratedJpeg(Path.Combine(input, "g4.jpg"), 1536, 1024, 0);
            WriteGeneratedUncompressedPng(Path.Combine(input, "g4.png"), 512, 320, 10);
            // Synthetic 16x16 one-frame MP4 generated from a solid blue frame.
            byte[] video = Convert.FromBase64String(
                "AAAAHGZ0eXBpc29tAAACAGlzb21pc28ybXA0MQAAAAhmcmVlAAAAHG1kYXQAAAGzABAHAAABthYFGCobYHgFrwAAAwttb292AAAAbG12" +
                "aGQAAAAAAAAAAAAAAAAAAAPoAAAD6AABAAABAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAAAAA" +
                "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAAACWnRyYWsAAABcdGtoZAAAAAMAAAAAAAAAAAAAAAEAAAAAAAAD6AAAAAAAAAAAAAAAAAAA" +
                "AAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAEAAAAAAEAAAABAAAAAAACRlZHRzAAAAHGVsc3QAAAAAAAAAAQAAA+gAAAAA" +
                "AAEAAAAAAdJtZGlhAAAAIG1kaGQAAAAAAAAAAAAAAAAAAEAAAABAAFXEAAAAAAAtaGRscgAAAAAAAAAAdmlkZQAAAAAAAAAAAAAAAFZp" +
                "ZGVvSGFuZGxlcgAAAAF9bWluZgAAABR2bWhkAAAAAQAAAAAAAAAAAAAAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAB" +
                "PXN0YmwAAADZc3RzZAAAAAAAAAABAAAAyW1wNHYAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAEAAQAEgAAABIAAAAAAAAAAEKTGF2YyBt" +
                "cGVnNAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY//8AAABPZXNkcwAAAAADgICAPgABAASAgIAwIBEAAAAAAw1AAAAAoAWAgIAeAAABsAEA" +
                "AAG1iRMAAAEAAAABIADEjYgADQCEAhRjBoCAgAECAAAAEHBhc3AAAAABAAAAAQAAABRidHJ0AAAAAAADDUAAAACgAAAAGHN0dHMAAAAA" +
                "AAAAAQAAAAEAAEAAAAAAHHN0c2MAAAAAAAAAAQAAAAEAAAABAAAAAQAAABRzdHN6AAAAAAAAABQAAAABAAAAFHN0Y28AAAAAAAAAAQAA" +
                "ACwAAAA9dWR0YQAAADVtZXRhAAAAAAAAACFoZGxyAAAAAAAAAABtZGlyYXBwbAAAAAAAAAAAAAAAAAhpbHN0");
            string videoA = Path.Combine(input, "a.mp4"), videoB = Path.Combine(input, "b.mp4");
            File.WriteAllBytes(videoA, video);
            File.Copy(videoA, videoB);
            File.SetCreationTimeUtc(videoB, File.GetCreationTimeUtc(videoA));
            File.SetLastWriteTimeUtc(videoB, File.GetLastWriteTimeUtc(videoA));
            File.WriteAllText(Path.Combine(input, ".chappy-part23-generated-only"), "Part23 synthetic data only");
            var originals = Directory.GetFiles(input).Where(Safety.IsMedia)
                .ToDictionary(path => path, path => (Snapshot: Safety.Capture(path), Hash: OracleHash(path)));
            var main = new MainWindow();
            Named<TextBox>(main, "PathBox").Text = input;
            var status = Named<TextBlock>(main, "ProgressText");
            var scan = Named<Button>(main, "ScanButton");
            RoutedClick(scan);
            PumpUntil(() => scan.IsEnabled && status.Text == "スキャン完了", TimeSpan.FromSeconds(35));
            var groups = Named<DataGrid>(main, "GroupsGrid");
            Require(groups.Items.Count == 1, "G5-01: one decodable MP4 duplicate group selected on VHDX");
            groups.SelectedIndex = 0;
            typeof(MainWindow).GetField("_allowGeneratedOnlyMergeTest",
                BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, true);
            SetPrivate(main, "_confirmSelectedMergeRisk", new Func<string, bool>(_ => true));
            string mergeResult = "";
            SetPrivate(main, "_showDedupePlan", new Action<string>(text => mergeResult = text));
            var merge = Named<Button>(main, "SelectedMergeReviewButton");
            Require(merge.IsEnabled, "G5-01: generated MP4 can be selected for actual merge");
            RoutedClick(merge);
            PumpUntil(() => mergeResult.Length > 0, TimeSpan.FromSeconds(45));
            Require(status.Text == "生成テスト統合完了・本番統合は無効" &&
                NativeFile.SameFile(videoA, videoB),
                "G5-01: WPF merge created a real MP4 hard link: " + status.Text + " / " + mergeResult);
            var recovery = new RecoveryWindow(input);
            typeof(RecoveryWindow).GetField("_allowGeneratedOnlyUndoTest",
                BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(recovery, true);
            typeof(RecoveryWindow).GetField("_confirmGeneratedOnlyUndo",
                BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(recovery, new Func<string, bool>(_ => true));
            typeof(RecoveryWindow).GetField("_showGeneratedOnlyUndo",
                BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(recovery, new Action<string>(_ => { }));
            Directory.CreateDirectory(Path.Combine(input, "g3-wpf-undo-evidence"));
            var undoHistory = (DataGrid)recovery.FindName("HistoryGrid");
            recovery.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => undoHistory.Items.OfType<RecoveryEvidenceOverview.Row>()
                .Any(row => row.State == "VerifiedCompleted"), TimeSpan.FromSeconds(35));
            ((TabControl)recovery.FindName("RecoveryTabs")).SelectedIndex = 1;
            undoHistory.SelectedItem = undoHistory.Items.OfType<RecoveryEvidenceOverview.Row>()
                .Single(row => row.Source == "複数ファイルのグループ履歴" && row.State == "VerifiedCompleted");
            var undo = (Button)recovery.FindName("UndoExecuteButton");
            Require(undo.IsEnabled, "G5-01: verified generated MP4 group can Undo through WPF");
            RoutedClick(undo);
            var undoSummary = (TextBlock)recovery.FindName("HistorySummaryText");
            PumpUntil(() => undoSummary.Text.Contains("【生成テストUndo成功】", StringComparison.Ordinal),
                TimeSpan.FromSeconds(45));
            Require(!NativeFile.SameFile(videoA, videoB) && OracleHash(videoA) == OracleHash(videoB),
                "G5-01: WPF Undo restored independent MP4 files");
            recovery.Close();

            string tool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN")!;
            SetPrivate(main, "_pickCombinedTool", new Func<string?, string?>(_ => tool));
            SetPrivate(main, "_pickCombinedOutput", new Func<string?, string?>(_ => output));
            SetPrivate(main, "_confirmCombined", new Func<string, bool>(_ => true));
            string previewResult = "", previewError = "";
            SetPrivate(main, "_showCombinedResult", new Action<string>(text => previewResult = text));
            SetPrivate(main, "_showCombinedFailure", new Action<string>(text => previewError = text));
            var preview = Named<Button>(main, "UnifiedBatchButton");
            RoutedClick(preview);
            PumpUntil(() => preview.IsEnabled && (previewResult.Length > 0 || previewError.Length > 0),
                TimeSpan.FromSeconds(65));
            Require(previewError.Length == 0 &&
                CombinedCapacityReport.Inspect(input, output).CandidateCount == 2,
                "G5-01: WPF created real PNG and JPEG candidates on the same isolated dataset: " + previewError);
            AssertJpegPreparationAndReview(main, root, input, output, isolated: true);
            Require(originals.All(pair =>
            {
                var after = Safety.Capture(pair.Key);
                uint links = pair.Key.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
                    after.Identity.SameFile(pair.Value.Snapshot.Identity) ? 2u : 1u;
                return OracleHash(pair.Key) == pair.Value.Hash && after.Identity.LinkCount == links &&
                    after.CreationUtc == pair.Value.Snapshot.CreationUtc &&
                    after.ModifiedUtc == pair.Value.Snapshot.ModifiedUtc &&
                    after.Attributes == pair.Value.Snapshot.Attributes &&
                    after.SecurityDescriptor.AsSpan().SequenceEqual(pair.Value.Snapshot.SecurityDescriptor);
            }), "G5-03: all JPEG/PNG/MP4 bytes, permissions, timestamps and independent links preserved");
            Require(NativeFile.GetIdentity(videoA).SameFile(originals[videoA].Snapshot.Identity) ||
                NativeFile.GetIdentity(videoB).SameFile(originals[videoB].Snapshot.Identity),
                "G5-03: the unmodified MP4 representative keeps its original ID");
            string detached = NativeFile.GetIdentity(videoA).LinkCount == 1 ? videoA : videoB;
            string representative = detached == videoA ? videoB : videoA;
            using (var edited = new FileStream(detached, FileMode.Append))
                edited.WriteByte(0); // One generated alias only; independent-write proof.
            Require(OracleHash(representative) == originals[representative].Hash &&
                OracleHash(detached) != originals[detached].Hash, "G5-03: independent edit changes one restored alias");
            main.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 91;
        }
        finally
        {
            app.Shutdown();
            journal.SetValue(null, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertIsolatedImageActions(RecoveryWindow view, string source,
        string stage, string backup, FileSnapshot before, string hash)
    {
        var apply = (Button)view.FindName("GeneratedImageApply");
        var retire = (Button)view.FindName("GeneratedImageRetire");
        var restore = (Button)view.FindName("GeneratedImageRestore");
        var text = (TextBlock)view.FindName("ImageReceiptReviewText");
        Require(!apply.IsEnabled && !retire.IsEnabled && !restore.IsEnabled,
            "G4-06: ordinary WPF keeps image actions locked");
        typeof(RecoveryWindow).GetField("_allowGeneratedImageTest",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, true);
        typeof(RecoveryWindow).GetField("_confirmGeneratedImage",
            BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, new Func<string, bool>(_ => true));
        var review = (Button)view.FindName("ImageReceiptReviewButton");
        RoutedClick(review);
        PumpUntil(() => apply.IsEnabled, TimeSpan.FromSeconds(30));
        var drive = new DriveInfo(Path.GetPathRoot(source)!);
        long prepared = drive.AvailableFreeSpace;
        string stagedPath = Directory.GetFiles(stage).Single();
        string id = Path.GetFileNameWithoutExtension(stagedPath)[".chappy-g4-stage-".Length..];
        string rollback = source + ".chappy-g4-old-" + id + ".rollback";
        string backupFile = Directory.GetFiles(backup, "*.bak").Single();
        string candidateHash = OracleHash(stagedPath);

        // An actual ADS mutation is refused after review, with originals preserved.
        var backupBeforeAds = Safety.Capture(backupFile);
        File.WriteAllText(backupFile + ":g4-interference", "generated ADS");
        RoutedClick(apply);
        PumpUntil(() => review.IsEnabled && text.Text.Contains("要手動確認", StringComparison.Ordinal),
            TimeSpan.FromSeconds(30));
        Require(Safety.SameSnapshot(before) && OracleHash(source) == hash && !File.Exists(rollback),
            "G5-02: VHDX WPF rejects stale review with a modified independent backup");
        File.Delete(backupFile + ":g4-interference");
        File.SetAttributes(backupFile, backupBeforeAds.Attributes);
        File.SetLastWriteTimeUtc(backupFile, backupBeforeAds.ModifiedUtc);
        Require(Safety.SameSnapshot(backupBeforeAds), "ADS test reset preserves the original backup evidence");
        RoutedClick(review);
        PumpUntil(() => apply.IsEnabled, TimeSpan.FromSeconds(30));
        prepared = drive.AvailableFreeSpace;
        RoutedClick(apply);
        PumpUntil(() => retire.IsEnabled, TimeSpan.FromSeconds(30));
        long applied = drive.AvailableFreeSpace;
        Require(OracleHash(source) == candidateHash && OracleHash(rollback) == hash &&
            text.Text.Contains("回復として計上 0 B", StringComparison.Ordinal),
            "G4-06: VHDX WPF applies candidate while retaining both copies and credits zero");

        RoutedClick(retire);
        PumpUntil(() => restore.IsEnabled && text.Text.Contains("AppliedWithoutRollback",
            StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        long retired = drive.AvailableFreeSpace;
        Require(!File.Exists(rollback) && OracleHash(backupFile) == hash && retired > applied,
            "G4-06: verified generated rollback retirement observes actual free-space gain");
        string fill = Path.Combine(Path.GetDirectoryName(stage)!, "generated-capacity-test.bin");
        try
        {
            using (var stream = new FileStream(fill, FileMode.CreateNew, FileAccess.Write))
            {
                var block = new byte[1024 * 1024];
                while (drive.AvailableFreeSpace > 3L * block.Length)
                    stream.Write(block);
                stream.Flush(flushToDisk: true);
            }
            RoutedClick(restore);
            PumpUntil(() => review.IsEnabled && text.Text.Contains("要手動確認", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30));
            Require(text.Text.Contains("空き容量が不足", StringComparison.Ordinal) &&
                OracleHash(source) == candidateHash && OracleHash(backupFile) == hash,
                "G5-02: real near-full VHDX refuses WPF restore and preserves both files");
        }
        finally { File.Delete(fill); }

        string receipt = Path.Combine(backup, ".chappy-g4-preparation.json");
        var record = JsonDocument.Parse(File.ReadAllText(receipt));
        using (record)
        {
            string candidate = record.RootElement.GetProperty("Candidate").GetProperty("Path").GetString()!;
            string worker = Path.Combine(AppContext.BaseDirectory, "ChappyStorageCompressor.SmokeTests.dll");
            var start = new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (string arg in new[] { worker, "--g4-pinned-restore-child",
                source, candidate, stage, backup, "g4-after-candidate-quarantine" })
                start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start) ??
                throw new IOException("G5 isolated restore worker failed to start");
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("G5 isolated restore worker timed out");
            }
            Require(process.ExitCode == 94, "G5-02: real VHDX restore worker stopped after quarantine");
        }
        RoutedClick(review);
        PumpUntil(() => restore.IsEnabled, TimeSpan.FromSeconds(30));
        RoutedClick(restore);
        PumpUntil(() => review.IsEnabled && text.Text.Contains("BackupRestored",
            StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        long restored = drive.AvailableFreeSpace;
        Require(OracleHash(source) == hash && OracleHash(backupFile) == hash &&
            !NativeFile.GetIdentity(source).SameFile(before.Identity) && restored < retired,
            "G5-03: WPF independent restore consumes actual isolated space and preserves original bytes");
        File.WriteAllText(Path.Combine("diagnostics", "v1-isolated-wpf.json"), JsonSerializer.Serialize(new
        {
            prepared, applied, retired, restored,
            rollbackRemovalObservedGainBytes = retired - applied,
            backupRestoreObservedCostBytes = retired - restored,
            realUserCapacityRecoveredBytes = 0
        }));
    }

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
