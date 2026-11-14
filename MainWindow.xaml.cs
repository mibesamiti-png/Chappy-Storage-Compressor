using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ChappyStorageCompressor.Core;
using Forms = System.Windows.Forms;

namespace ChappyStorageCompressor;

public partial class MainWindow : Window
{
    private ScanResult? _lastScan;
    private CancellationTokenSource? _work;
    private bool _busy;
    private string _lastRoot = "";
    private readonly ObservableCollection<GroupRow> _rows = new();

    public MainWindow()
    {
        InitializeComponent();
        GroupsGrid.ItemsSource = _rows;
        Log("起動しました。対象を選び「読み取り専用スキャン」を実行してください。");
        var pending = Journal.Pending();
        var damaged = Journal.UnresolvedRecordFiles();
        if (pending.Count != 0 || damaged.Count != 0)
            Log($"未完了記録 {pending.Count} 件、不完全記録 {damaged.Count} 件。新しい統合は禁止です。「復旧記録を確認」を押してください。");
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        BrowseButton.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        MergeButton.IsEnabled = false; // R1.5: release gate until NTFS runtime verified.
        MediaReportButton.IsEnabled = !busy && _lastScan != null;
        DetailsButton.IsEnabled = !busy && GroupsGrid.SelectedItems.Count == 1;
        StopButton.IsEnabled = busy;
        RecoveryButton.IsEnabled = !busy;
        PngInspectButton.IsEnabled = !busy;
        PngPreviewButton.IsEnabled = !busy;
        PngBatchButton.IsEnabled = !busy;
        UnifiedBatchButton.IsEnabled = !busy;
        PngAuditButton.IsEnabled = !busy;
        JpegInspectButton.IsEnabled = !busy;
        PngOrphanButton.IsEnabled = !busy;
        JpegPreviewButton.IsEnabled = !busy;
        JpegBatchButton.IsEnabled = !busy;
        JpegBatchReportButton.IsEnabled = !busy;
        CombinedReportButton.IsEnabled = !busy;
        ReplacementReadinessButton.IsEnabled = !busy;
        JpegRepairButton.IsEnabled = !busy;
        StoreButton.IsEnabled = !busy;
        MediaOnlyCheck.IsEnabled = !busy;
        TimeDifferenceCheck.IsEnabled = !busy;
        PathBox.IsEnabled = !busy;
    }

    private void ClearScan()
    {
        _lastScan = null;
        _rows.Clear();
        ScannedCountText.Text = GroupCountText.Text = SafeCountText.Text = SavingsText.Text = "—";
        JobProgress.IsIndeterminate = false;
        JobProgress.Value = 0;
        ProgressText.Text = "待機中";
        MergeButton.IsEnabled = false;
        MediaReportButton.IsEnabled = false;
        DetailsButton.IsEnabled = false;
    }

    private void PathChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized && !_busy) ClearScan();
    }

    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _busy) return;
        if (_lastScan != null && _lastScan.MediaOnly != (MediaOnlyCheck.IsChecked == true))
        {
            ClearScan();
        }
        else RefreshRows();
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MergeButton != null) MergeButton.IsEnabled = false; // R1.5: never enable from selection.
        if (DetailsButton != null) DetailsButton.IsEnabled = !_busy && GroupsGrid.SelectedItems.Count == 1;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "写真・動画の親フォルダを選択してください（ドライブ全体はR1対象外）",
            ShowNewFolderButton = false,
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) PathBox.Text = dialog.SelectedPath;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        string root;
        try { root = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "対象フォルダを確認", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        ClearScan();
        _lastRoot = root;
        _work = new CancellationTokenSource();
        Busy(true);
        JobProgress.IsIndeterminate = true; // Inventory has no known total.
        ProgressText.Text = "読み取り専用スキャン中";
        Log("スキャン開始: " + root);
        bool mediaOnly = MediaOnlyCheck.IsChecked == true;
        var token = _work.Token;
        // Hash progress is separate from messages: no per-chunk log flood.
        // Progress<T> marshals worker updates to the WPF UI context.
        var progress = new Progress<string>(message =>
        {
            if (!_busy || token.IsCancellationRequested) return;
            if (!message.StartsWith("SHA-256計算", StringComparison.Ordinal))
                ProgressText.Text = "サイズ・安全性を検査中";
            Log(message);
        });
        var hashProgress = new Progress<ScanHashProgress>(p =>
        {
            if (!_busy || token.IsCancellationRequested) return;
            JobProgress.IsIndeterminate = false;
            JobProgress.Value = p.Percent;
            ProgressText.Text = p.CandidateFiles == 0 ? "同サイズの候補なし" :
                p.CurrentFileTotalBytes > 0
                    ? $"SHA {p.Percent:F0}%・単体 {p.CurrentFilePercent:F0}%"
                    : $"SHA {p.Percent:F0}%・{p.SettledFiles}/{p.CandidateFiles}";
        });
        try
        {
            var result = await Task.Run(() => Scanner.Run(root, mediaOnly, progress,
                token, hashProgress));
            _lastScan = result;
            RefreshRows();
            ProgressText.Text = "スキャン完了";
            JobProgress.Value = 100;
            Log($"スキャン完了。検出 {result.FileCount:N0} 件、スキップ {result.Skipped:N0} 件、アクセス等のエラー {result.Errors:N0} 件、完全一致 {result.Groups.Count:N0} グループ。");
            Log($"R2.16: サイズによる一次候補 {result.FileCount:N0} 件中、NTFS厳密検査 {result.DeepInspectedFiles:N0} 件、SHA-256試行 {result.HashAttemptedFiles:N0} 件。単独サイズは内容を読み込まずスキップ。");
            Log("※ 読み取り専用スキャンではファイルの削除・統合は一切行いません。");
        }
        catch (OperationCanceledException) { Log("スキャンを中断しました。ファイル変更はありません。"); ProgressText.Text = "中断"; }
        catch (Exception ex) { Log("スキャンエラー: " + ex); MessageBox.Show(ex.Message, "スキャン失敗", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            JobProgress.IsIndeterminate = false;
            Busy(false);
            _work?.Dispose();
            _work = null;
        }
    }

    private void RefreshRows()
    {
        _rows.Clear();
        if (_lastScan is null) return;
        bool allowTime = TimeDifferenceCheck.IsChecked == true;
        foreach (var group in _lastScan.Groups)
        {
            _rows.Add(new GroupRow
            {
                Group = group,
                RepresentativeDisplay = Path.GetRelativePath(_lastScan.Root, group.Representative.Path),
                Status = group.BlockReason(allowTime) is string reason ? "対象外: " + reason : "統合可能",
            });
        }
        ScannedCountText.Text = _lastScan.FileCount.ToString("N0");
        GroupCountText.Text = _rows.Count.ToString("N0");
        var safe = _rows.Where(x => x.Status == "統合可能").ToList();
        SafeCountText.Text = safe.Count.ToString("N0");
        SavingsText.Text = GroupRow.HumanSize(safe.Sum(x => x.Group.Savings));
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("R1.5のハードリンク統合はWindows/NTFS実機検証完了まで無効です。",
            "安全のため停止", MessageBoxButton.OK, MessageBoxImage.Information);
        return;
        if (_lastScan == null || GroupsGrid.SelectedItems.Count == 0) return;
        if (Journal.Pending().Count != 0 || Journal.UnresolvedRecordFiles().Count != 0)
        {
            MessageBox.Show("未完了の復旧記録があります。『復旧記録を確認』で状態を確認してから統合してください。", "未完了操作あり", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var selected = GroupsGrid.SelectedItems.Cast<GroupRow>().Where(x => x.Status == "統合可能")
            .Select(x => x.Group).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show("選択中のグループには安全条件を満たした統合対象がありません。", "統合対象なし");
            return;
        }
        try { CapacityGuard.AssertCanStart(_lastScan.Root); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message + "\n読み取り専用スキャン・PNG診断は引き続き利用できます。",
                "空き容量の安全チェック", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        int files = selected.Sum(g => g.Files.Count - 1);
        long savings = selected.Sum(g => g.Savings);
        string warning = $"選択した {selected.Count:N0} グループ・{files:N0} ファイルを統合します。\n\n" +
            $"論理上の削減見込: {GroupRow.HumanSize(savings)}\n" +
            $"保存先: {Safety.StorePath(_lastScan.Root)}\n\n" +
            "【重要】統合後は複数のファイル名が同じ実体を参照します。\n" +
            "ある場所で上書き保存すると、他の場所にも変更が反映されることがあります。\n" +
            "また、統合前の独立したコピーは削除するため、空き容量なしでは簡単に元へ戻せません。\n" +
            "重要なファイルは必ず別ドライブ等へバックアップしてください。\n\n" +
            "処理を開始しますか？";
        if (MessageBox.Show(warning, "最終確認：独立コピーの統合", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes) return;
        if (TimeDifferenceCheck.IsChecked == true)
        {
            if (MessageBox.Show("日時差を許容する設定です。統合前後でファイルの作成日時・更新日時が代表ファイルのものに統一される可能性があります。続行しますか？",
                "日時について再確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }

        _work = new CancellationTokenSource();
        Busy(true);
        JobProgress.Value = 0;
        _lastRoot = _lastScan.Root;
        long initialFree = -1, finalFree = -1;
        try { initialFree = new DriveInfo(Path.GetPathRoot(_lastRoot)!).AvailableFreeSpace; } catch { }
        Log($"ハードリンク統合開始: {selected.Count:N0} グループ / {files:N0} 件");
        var progress = new Progress<EngineProgress>(p =>
        {
            JobProgress.Value = p.Total == 0 ? 100 : p.Done * 100.0 / p.Total;
            ProgressText.Text = $"{p.Done:N0}/{p.Total:N0} 件";
            Log(p.Message);
        });
        bool allowTime = TimeDifferenceCheck.IsChecked == true;
        string mergeRoot = _lastRoot;
        var token = _work.Token;
        try
        {
            long estimated = await Task.Run(() => DedupeEngine.Run(mergeRoot, selected,
                allowTime, progress, token));
            try { finalFree = new DriveInfo(Path.GetPathRoot(_lastRoot)!).AvailableFreeSpace; } catch { }
            Log("統合が終了。理論上の回復量: " + GroupRow.HumanSize(estimated));
            if (initialFree >= 0 && finalFree >= 0)
                Log("ディスク空き容量の実測差: " + GroupRow.HumanSize(Math.Abs(finalFree - initialFree)) +
                    (finalFree >= initialFree ? " 増加" : " 減少") + "（他の処理による変動を含む場合があります）");
            ProgressText.Text = "統合終了・再スキャンを推奨";
        }
        catch (OperationCanceledException) { Log("中断しました。進行中のファイル置換は完了または復旧の対象になります。"); ProgressText.Text = "中断"; }
        catch (Exception ex) { Log("統合エラー: " + ex); MessageBox.Show("統合処理でエラーが起きました。復旧記録を確認してください。\n" + ex.Message, "統合失敗", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally
        {
            Busy(false);
            _work?.Dispose(); _work = null;
            // スキャン結果は統合前のファイルIDを保持しているため再使用しない。
            ClearScan();
            Log("統合済みファイルの状態を更新するには、もう一度スキャンしてください。");
        }
    }

    // R2.13: format-specific, read-only deduplication analysis of media scans.
    // Does not create hardlinks or replace original files.
    private async void MediaReport_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _lastScan == null) return;
        var scan = _lastScan;
        bool allowTimestampDifference = TimeDifferenceCheck.IsChecked == true;
        _work = new CancellationTokenSource();
        CancellationToken ct = _work.Token;
        Busy(true);
        ProgressText.Text = "JPG/PNG/MP4を再照合中";
        JobProgress.Value = 0;
        Log("拡張子別の重複レポートを生成中（既存ファイルは変更しません）");
        var recheckProgress = new Progress<(int Done, int Total)>(p =>
        {
            JobProgress.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText.Text = $"再照合 {p.Done:N0}/{p.Total:N0}";
        });
        try
        {
            var report = await Task.Run(() =>
                MediaDuplicateReport.Analyze(scan, allowTimestampDifference, ct, recheckProgress), ct);
            ct.ThrowIfCancellationRequested();
            ProgressText.Text = "重複容量レポート完成";
            JobProgress.Value = 100;
            string byFormat = string.Join(Environment.NewLine, report.Formats
                .Select(x => $"{x.Format}: {x.EligibleGroups:N0}グループ・{x.EligibleFiles:N0}件 / {GroupRow.HumanSize(x.PotentialBytes)}"));
            string info = $"再照合したグループ: {report.InspectedGroups:N0}件\n" +
                $"条件一致: {report.EligibleGroups:N0}件、対象外: {report.BlockedGroups:N0}件、変更あり: {report.ChangedGroups:N0}件\n" +
                $"重複削減の理論値: {GroupRow.HumanSize(report.PotentialBytes)}\n" +
                $"実際に回復した容量: {GroupRow.HumanSize(report.ActualFreedBytes)}\n\n" +
                byFormat + "\n\n" +
                "拡張子別の分類です。MP4の再エンコード・画質変更はしません。\n" +
                "これは原本変更なしの見込み値です。統合機能は現在も無効です。\n" +
                "グループ別のCSVを保存しますか？";
            Log($"拡張子別の重複検出: 条件一致 {report.EligibleGroups:N0}、再スキャン必要 {report.ChangedGroups:N0}、" +
                $"理論削減 {GroupRow.HumanSize(report.PotentialBytes)}、実回復 0B。");
            if (MessageBox.Show(info, "JPG/PNG/MP4 重複容量レポート",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "重複容量のCSV（スキャン対象フォルダの外に保存）",
                Filter = "CSV (*.csv)|*.csv", AddExtension = true,
                FileName = "chappy-r213-media-duplicate-report.csv",
                OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) == true)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Run(() => MediaDuplicateReport.ExportCsv(report, scan.Root, dialog.FileName), ct);
                Log("JPG/PNG/MP4の読み取り専用CSVを保存: " + dialog.FileName);
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "重複レポート中断";
            Log("拡張子別の重複レポートを中断しました。原本変更なし。");
        }
        catch (Exception ex)
        {
            ProgressText.Text = "重複レポート失敗";
            Log("拡張子別重複レポートのエラー: " + ex.Message);
            MessageBox.Show(ex.Message, "重複レポートを確認してください",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Busy(false);
            _work?.Dispose();
            _work = null;
        }
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (GroupsGrid.SelectedItem is not GroupRow item) return;
        var panel = new DockPanel { Margin = new Thickness(14) };
        var caption = new TextBlock
        {
            Text = $"SHA-256: {item.Group.Hash}\n1ファイル: {item.FileSizeText} / {item.Count}件\n統合条件: {item.Status}\n\n下の全パスを確認してください。",
            Foreground = System.Windows.Media.Brushes.WhiteSmoke, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        var copyButton = new Button { Content = "全パスをコピー", Margin = new Thickness(0, 12, 0, 0) };
        copyButton.Click += (_, _) => System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, item.Group.Files.Select(f => f.Path)));
        DockPanel.SetDock(copyButton, Dock.Bottom);
        panel.Children.Add(copyButton);
        var paths = new ListBox
        {
            ItemsSource = item.Group.Files.Select(f => f.Path).ToList(),
            Foreground = System.Windows.Media.Brushes.WhiteSmoke,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 36, 52)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 68, 85)),
        };
        panel.Children.Add(paths);
        var dialog = new Window
        {
            Owner = this, Title = "完全一致グループ内の全ファイル", Width = 850, Height = 490,
            MinWidth = 550, MinHeight = 330, Content = panel,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 23, 36)),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        dialog.ShowDialog();
    }

    private async void PngPreview_Click(object sender, RoutedEventArgs e)
    {
        var input = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PNG画像 (*.png)|*.png",
            Title = "無劣化プレビューを生成するPNGを選択",
            CheckFileExists = true
        };
        if (input.ShowDialog(this) != true) return;
        using var folder = new Forms.FolderBrowserDialog
        {
            Description = "検証済みPNGプレビューの保存先（空き容量のある場所）を選択",
            ShowNewFolderButton = true
        };
        if (folder.ShowDialog() != Forms.DialogResult.OK) return;
        _work = new CancellationTokenSource();
        Busy(true);
        JobProgress.Value = 0;
        ProgressText.Text = "PNG無劣化候補を検証中";
        string png = input.FileName, outputDir = folder.SelectedPath;
        var token = _work.Token;
        Log("PNGプレビュー作成: " + png);
        try
        {
            var result = await Task.Run(() => PngLosslessOptimizer.CreatePreview(png,
                outputDir, token));
            JobProgress.Value = 100;
            if (result.PreviewPath != null)
            {
                Log($"成功: {result.PreviewPath} / 元 {GroupRow.HumanSize(result.OriginalBytes)} " +
                    $"→ 候補 {GroupRow.HumanSize(result.PreviewBytes)} " +
                    $"（削減 {GroupRow.HumanSize(result.SavedBytes)}）");
                Log("元ファイルには一切変更していません。候補の原本置換は禁止です。");
                MessageBox.Show("無劣化プレビューを作成しました。\n" + result.PreviewPath +
                    "\n\n元ファイルは変更していません。重要なデータにはまだ適用しないでください。",
                    "PNG候補を作成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else Log("採用できる候補なし: " + result.Explanation);
            ProgressText.Text = "PNG候補の検証終了";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "中断";
            Log("PNGプレビューを中断しました。元ファイルは変更していません。");
        }
        catch (Exception ex)
        {
            ProgressText.Text = "失敗";
            Log("PNGプレビューエラー: " + ex);
            MessageBox.Show(ex.Message, "PNG候補生成失敗", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    // R2.14: One source/output selection for the existing PNG and JPEG
    // preview-only engines. Their separate journals remain authoritative.
    // Neither engine is permitted to modify original user media.
    private async void UnifiedBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "入力フォルダを確認", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var toolPicker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "公式libjpeg-turboのjpegtran.exeを選択（PNGのみの場合は従来のPNG専用ボタン）",
            Filter = "jpegtran.exe|jpegtran.exe",
            CheckFileExists = true
        };
        if (toolPicker.ShowDialog(this) != true) return;

        using var folder = new Forms.FolderBrowserDialog
        {
            Description = "PNG/JPG両方の候補と再開記録の保存先（原本とは別のフォルダ階層）",
            ShowNewFolderButton = true
        };
        if (folder.ShowDialog() != Forms.DialogResult.OK) return;
        string output = folder.SelectedPath, tool = toolPicker.FileName;

        _work = new CancellationTokenSource();
        CancellationToken token = _work.Token;
        Busy(true);
        JobProgress.Value = 0;
        ProgressText.Text = "PNG/JPGの事前容量調査";
        try
        {
            // This inventory never modifies the source and is deliberately
            // only a rough capacity forecast, not a safety approval.
            var preflight = await Task.Run(() =>
                PersonalMediaBatchPreflight.Inspect(input, output, token), token);
            token.ThrowIfCancellationRequested();
            if (preflight.TotalFiles == 0)
            {
                MessageBox.Show("一般的なPNG/JPG候補がありませんでした。原本は変更していません。",
                    "一括候補の事前確認", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string warning = preflight.OutputMayNeedMoreSpace
                ? "\n\n候補保存先の空き容量が原本合計より少ないため、途中で容量不足となる可能性があります。"
                : "";
            string preview = $"入力: {input}\n保存先: {output}\n" +
                $"PNG: {preflight.PngFiles:N0}件 / {GroupRow.HumanSize(preflight.PngSourceBytes)}\n" +
                $"JPG/JPEG: {preflight.JpegFiles:N0}件 / {GroupRow.HumanSize(preflight.JpegSourceBytes)}\n" +
                $"入力の合計: {GroupRow.HumanSize(preflight.TotalSourceBytes)}\n" +
                $"候補保存先の空き: {GroupRow.HumanSize(preflight.OutputAvailableBytes)}\n" +
                $"特殊属性の除外: {preflight.SkippedSpecialFiles:N0}件、調査エラー: {preflight.ScanErrors:N0}件" +
                warning +
                "\n\nPNGの後にJPGを実行し、それぞれの進捗記録から再開します。" +
                "\n候補生成は追加のディスク容量を消費します。" +
                "\n元ファイルの置換・削除・統合は行いません。" +
                "\n\n処理を開始しますか？";
            if (MessageBox.Show(preview, "PNG/JPGまとめて無劣化候補作成",
                MessageBoxButton.YesNo, MessageBoxImage.Information,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;

            int updates = 0;
            var pngProgress = new Progress<PngBatchPreviewEngine.Progress>(p =>
            {
                JobProgress.Value = p.Total == 0 ? 50 : 50.0 * p.Done / p.Total;
                ProgressText.Text = $"PNG {p.Done:N0}/{p.Total:N0}";
                if (p.Status is "Failed" or "Deferred" || ++updates % 100 == 0)
                    Log($"PNG {p.Status}: {p.RelativePath} / 候補削減 {GroupRow.HumanSize(p.SavedBytes)}");
            });
            var jpegProgress = new Progress<JpegBatchPreviewEngine.Progress>(p =>
            {
                JobProgress.Value = p.Total == 0 ? 100 : 50.0 + 50.0 * p.Done / p.Total;
                ProgressText.Text = $"JPG {p.Done:N0}/{p.Total:N0}";
                if (p.Status is "Failed" or "Deferred" or "NeedsReview" || ++updates % 100 == 0)
                    Log($"JPG {p.Status}: {p.RelativePath} / 候補削減 {GroupRow.HumanSize(p.SavingBytes)}");
            });

            Log($"PNG/JPGまとめて候補作成開始。PNG {preflight.PngFiles:N0}件、JPG {preflight.JpegFiles:N0}件。");
            ProgressText.Text = "PNG一括候補を生成中";
            var png = await Task.Run(() =>
                PngBatchPreviewEngine.Run(input, output, pngProgress, token), token);
            Log($"PNG: {png.Done:N0}/{png.Total:N0}件、候補 {png.Optimized:N0}件、失敗 {png.Failed:N0}件。" +
                $" 候補削減見込 {GroupRow.HumanSize(png.SavedBytes)}。");
            Log("PNG再開記録: " + png.ManifestPath);
            token.ThrowIfCancellationRequested();
            if (png.Done < png.Total)
            {
                ProgressText.Text = "PNG保留（再開可能）";
                Log("PNGが容量不足等により保留となったためJPGは開始しません。空き容量を確保して再開してください。");
                return;
            }

            JobProgress.Value = 50;
            ProgressText.Text = "JPG一括候補を生成中";
            var jpeg = await Task.Run(() =>
                JpegBatchPreviewEngine.Run(input, output, tool, jpegProgress, token), token);
            token.ThrowIfCancellationRequested();
            JobProgress.Value = jpeg.Done == jpeg.Total ? 100 :
                50.0 + 50.0 * jpeg.Done / Math.Max(1, jpeg.Total);
            ProgressText.Text = jpeg.Done == jpeg.Total
                ? "PNG/JPG候補作成終了"
                : "JPG保留（再開可能）";

            long totalCandidateSaving = checked(png.SavedBytes + jpeg.EstimatedSavingBytes);
            Log($"JPG: {jpeg.Done:N0}/{jpeg.Total:N0}件、候補 {jpeg.Completed:N0}件、" +
                $"失敗 {jpeg.Failed:N0}件、要確認 {jpeg.NeedsReview:N0}件。" +
                $" 候補削減見込 {GroupRow.HumanSize(jpeg.EstimatedSavingBytes)}。");
            Log("JPG再開記録: " + jpeg.PlanPath);
            Log($"PNG＋JPG候補の合計論理削減見込: {GroupRow.HumanSize(totalCandidateSaving)}。" +
                " これは候補ファイルのサイズ差であり、元ファイルは無変更・ディスクの実回復は0Bです。");
            MessageBox.Show($"PNG候補: {png.Optimized:N0}件、JPG候補: {jpeg.Completed:N0}件\n" +
                $"サイズ差の見込: {GroupRow.HumanSize(totalCandidateSaving)}\n" +
                "元ファイルは変更していません。実際の空き容量が増えたわけではありません。\n" +
                "続きから再開するには同じ入力・候補フォルダ・jpegtran.exeを選んでください。",
                "一括無劣化候補の結果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "中断（再開可能）";
            Log("PNG/JPGまとめて候補作成を中断しました。各フォーマットの記録から再開できます。");
        }
        catch (Exception ex)
        {
            ProgressText.Text = "一括処理エラー";
            Log("PNG/JPGまとめて候補処理のエラー: " + ex);
            MessageBox.Show(ex.Message + "\n原本は変更していません。保存済みの候補と記録は削除しません。",
                "一括候補作成の安全停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Busy(false);
            _work?.Dispose();
            _work = null;
        }
    }

    private async void PngBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "親フォルダを確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "PNG候補・再開記録の保存先を指定（入力とは別階層、空き容量のある場所）",
            ShowNewFolderButton = true
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        string target = picker.SelectedPath;
        if (MessageBox.Show($"PNGを小さいファイルから順次検査します。\n\n入力: {input}\n候補保存先: {target}\n\n元ファイルの置換・削除・ハードリンク化は行いません。\n既存の再開記録があれば残りを処理します。",
            "読み取り専用一括プレビュー", MessageBoxButton.YesNo,
            MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        Busy(true);
        JobProgress.Value = 0;
        ProgressText.Text = "PNGを順次検査中";
        int updates = 0;
        var progress = new Progress<PngBatchPreviewEngine.Progress>(p =>
        {
            JobProgress.Value = p.Total == 0 ? 100 : p.Done * 100.0 / p.Total;
            ProgressText.Text = $"PNG {p.Done:N0}/{p.Total:N0}";
            if (p.Status is "Failed" or "Deferred" || ++updates % 100 == 0)
                Log($"{p.Status}: {p.RelativePath} / 追加削減候補 {GroupRow.HumanSize(p.SavedBytes)}");
        });
        try
        {
            var result = await Task.Run(() => PngBatchPreviewEngine.Run(input,
                target, progress, token));
            ProgressText.Text = result.Done == result.Total ? "PNG一括処理終了" : "PNG一括処理保留（再開可）";
            JobProgress.Value = result.Total == 0 ? 100 : result.Done * 100.0 / result.Total;
            Log($"PNG {result.Done:N0}/{result.Total:N0}件、候補 {result.Optimized:N0}件、除外 {result.Skipped:N0}件、失敗 {result.Failed:N0}件");
            Log($"候補の合計削減見込: {GroupRow.HumanSize(result.SavedBytes)}（元データは未変更・実ディスク空き容量は回復していません）");
            Log("再開記録: " + result.ManifestPath);
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "中断（再開可）";
            Log("PNG一括処理を中断。保存済み作業記録を使って次回再開できます。");
        }
        catch (Exception ex)
        {
            ProgressText.Text = "PNG一括処理失敗";
            Log("PNG一括処理エラー: " + ex);
            MessageBox.Show(ex.Message, "PNG一括処理エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    private async void PngAudit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "親フォルダを確認", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "R1.6/R1.7のPNG候補と作業記録がある出力フォルダを選択",
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        string output = picker.SelectedPath;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        Busy(true);
        ProgressText.Text = "PNG記録を診断中";
        try
        {
            var tail = await Task.Run(() => PngBatchJournalTools.InspectTail(input, output), token);
            Log($"PNG記録診断: {tail.State} / {tail.Message}");
            if (tail.State is "PartialTail" or "MissingNewline")
            {
                if (MessageBox.Show($"末尾の状態: {tail.State}\n{tail.Message}\n\n" +
                    "元のログを別ファイルで完全保管してから、最後の1レコードのみ補正します。" +
                    "PNG原本や候補ファイルは変更しません。実行しますか？",
                    "PNGログ末尾の安全修復", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes) return;
                var repair = await Task.Run(() => PngBatchJournalTools.RepairTail(input, output), token);
                Log($"PNG作業記録の修復終了：破棄した断片 {repair.DiscardedTailBytes:N0} bytes / 完全バックアップ: {repair.BackupPath}");
            }
            var audit = await Task.Run(() => PngBatchJournalTools.ReadAudit(input, output, token), token);
            ProgressText.Text = "PNG記録診断完了";
            string overview = $"全件: {audit.Total:N0}\n候補あり: {audit.Completed:N0}\n" +
                $"未処理: {audit.Pending:N0} / スキップ: {audit.Skipped:N0} / 失敗: {audit.Failed:N0}\n" +
                $"保留: {audit.Deferred:N0}\n候補の削減見込: {GroupRow.HumanSize(audit.EstimatedSavingBytes)}\n\n" +
                "候補のSHA-256と元ファイルのサイズ・日時を検査しています。" +
                "元ファイルのバイト列全体を再照合したわけではありません。\n" +
                "実際の空き容量はまだ回復していません。\n\nCSVに出力しますか？";
            if (MessageBox.Show(overview, "PNG候補の結果", MessageBoxButton.YesNo,
                MessageBoxImage.Information, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "PNGプレビュー結果のCSV保存先（原本の外）",
                    Filter = "CSV (*.csv)|*.csv", FileName = "chappy-png-r17-report.csv",
                    AddExtension = true
                };
                if (dialog.ShowDialog(this) == true)
                {
                    PngBatchJournalTools.ExportCsv(audit, input, dialog.FileName);
                    Log("CSV出力: " + dialog.FileName);
                }
            }
        }
        catch (OperationCanceledException) { Log("PNG記録診断を中断しました。"); }
        catch (Exception ex)
        {
            Log("PNG記録診断エラー: " + ex.Message);
            MessageBox.Show(ex.Message, "PNG記録診断を安全停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    private async void PngOrphan_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "親フォルダを確認"); return; }
        using var choose = new Forms.FolderBrowserDialog
        {
            Description = "PNG一括プレビューの出力先（進捗記録がある場所）を選択",
            ShowNewFolderButton = false
        };
        if (choose.ShowDialog() != Forms.DialogResult.OK) return;
        string output = choose.SelectedPath;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        Busy(true);
        try
        {
            ProgressText.Text = "PNG未参照候補を監査中";
            var result = await Task.Run(() => PngOrphanAudit.Inspect(input, output, ct), ct);
            string examples = string.Join("\n", result.Untracked.Take(8).Select(x => x.Path));
            string overview = $"記録上の候補: {result.Tracked:N0}件\n" +
                $"未参照ファイル: {result.Untracked.Count:N0}件 / {GroupRow.HumanSize(result.UntrackedBytes)}\n\n" +
                result.Note + "\n\n" + examples + "\n\n一覧をCSVで保存しますか？";
            Log($"PNG候補監査終了: 未参照 {result.Untracked.Count:N0}件。元画像・候補とも変更なし。");
            ProgressText.Text = "PNG未参照候補の監査完了";
            if (MessageBox.Show(overview, "PNG未参照候補（自動削除なし）",
                MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes) == MessageBoxResult.Yes)
            {
                var save = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "監査CSVを原本の外に保存", Filter = "CSV (*.csv)|*.csv",
                    FileName = "chappy-png-r18-untracked.csv", AddExtension = true
                };
                if (save.ShowDialog(this) == true)
                {
                    PngOrphanAudit.ExportCsv(result, input, save.FileName);
                    Log("PNG未参照候補CSV: " + save.FileName);
                }
            }
        }
        catch (OperationCanceledException) { Log("PNG候補監査を中断しました。"); }
        catch (Exception ex)
        {
            Log("PNG候補監査停止: " + ex.Message);
            MessageBox.Show(ex.Message, "安全のため停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }


    // R1.9: JPEG folder queue, never writes to originals. The jpegtran executable
    // is deliberately chosen by the user, and candidates remain in a separate folder.
    private async void JpegBatch_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "親フォルダを確認"); return; }
        var exePicker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "公式libjpeg-turboのjpegtran.exeを選択",
            Filter = "jpegtran.exe|jpegtran.exe", CheckFileExists = true
        };
        if (exePicker.ShowDialog(this) != true) return;
        using var folder = new Forms.FolderBrowserDialog
        {
            Description = "JPG候補と再開ログの保存先（親フォルダとは別階層）",
            ShowNewFolderButton = true
        };
        if (folder.ShowDialog() != Forms.DialogResult.OK) return;
        string output = folder.SelectedPath, tool = exePicker.FileName;
        if (MessageBox.Show($"親フォルダ: {input}\n候補保存先: {output}\n\n" +
            "JPG/JPEGを小さい順に試します。途中で停止しても再開ログを利用します。\n" +
            "原本は変更・削除しません。候補は追加保存されるためディスクの空き容量は増えません。\n" +
            "保存先には十分な空き容量が必要です。開始しますか？",
            "JPG完全無劣化候補を一括生成", MessageBoxButton.YesNo,
            MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        Busy(true);
        JobProgress.Value = 0;
        ProgressText.Text = "JPG一括候補を検証中";
        int updates = 0;
        var progress = new Progress<JpegBatchPreviewEngine.Progress>(p =>
        {
            JobProgress.Value = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            ProgressText.Text = $"JPG {p.Done:N0}/{p.Total:N0}";
            if (p.Status is "Failed" or "Deferred" or "NeedsReview" || ++updates % 100 == 0)
                Log($"{p.Status}: {p.RelativePath} / 削減候補 {GroupRow.HumanSize(p.SavingBytes)}");
        });
        try
        {
            var result = await Task.Run(() => JpegBatchPreviewEngine.Run(input, output,
                tool, progress, ct));
            JobProgress.Value = result.Total == 0 ? 100 : result.Done * 100.0 / result.Total;
            ProgressText.Text = result.Done == result.Total ? "JPG一括処理終了" : "JPG一括処理保留（再開可）";
            Log($"JPG {result.Done:N0}/{result.Total:N0}件、候補{result.Completed:N0}件、" +
                $"スキップ{result.Skipped:N0}件、失敗{result.Failed:N0}件、要確認{result.NeedsReview:N0}件");
            Log($"計上候補の削減見込：{GroupRow.HumanSize(result.EstimatedSavingBytes)}" +
                "。原本は未変更で、実際の空き容量はまだ回復しません。");
            Log("JPG計画: " + result.PlanPath);
        }
        catch (OperationCanceledException) { Log("JPG一括処理を中断。再開ログからやり直せます。"); ProgressText.Text = "中断（再開可）"; }
        catch (Exception ex)
        {
            Log("JPG一括処理停止: " + ex.Message);
            MessageBox.Show(ex.Message, "安全のため停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    private async void JpegBatchReport_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "親フォルダを確認"); return; }
        var exePicker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "JPGバッチ実行時と同じjpegtran.exeを選択",
            Filter = "jpegtran.exe|jpegtran.exe", CheckFileExists = true
        };
        if (exePicker.ShowDialog(this) != true) return;
        using var folder = new Forms.FolderBrowserDialog
        {
            Description = "前回のJPGバッチ候補・記録がある出力先を指定",
            ShowNewFolderButton = false
        };
        if (folder.ShowDialog() != Forms.DialogResult.OK) return;
        string output = folder.SelectedPath, tool = exePicker.FileName;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        Busy(true);
        ProgressText.Text = "JPG候補と原本のSHA-256を照合中";
        try
        {
            var audit = await Task.Run(() => JpegBatchPreviewEngine.Inspect(input, output, tool, ct));
            ProgressText.Text = "JPG容量見込を検証完了";
            string report = $"記録 {audit.Rows.Count:N0}件 / 要確認 {audit.NeedsReview:N0}件\n" +
                $"元データと候補の再照合後の削減見込：{GroupRow.HumanSize(audit.VerifiedSavingBytes)}\n\n" +
                "実際の空き容量の増加ではありません。原本は未変更です。\n" +
                "詳細CSVを保存しますか？（保存先は親フォルダ・候補保存先とは別の場所）";
            Log(report);
            if (MessageBox.Show(report, "JPGプレビュー容量レポート", MessageBoxButton.YesNo,
                MessageBoxImage.Information, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "JPGプレビュー結果CSV（原本/候補フォルダの外）",
                Filter = "CSV (*.csv)|*.csv", AddExtension = true,
                FileName = "chappy-jpeg-r19-report.csv", OverwritePrompt = true
            };
            if (save.ShowDialog(this) == true)
            {
                JpegBatchPreviewEngine.ExportCsv(audit, input, output, save.FileName);
                Log("JPGレポート: " + save.FileName);
            }
        }
        catch (OperationCanceledException) { Log("JPGレポートを中断しました。"); }
        catch (Exception ex)
        {
            Log("JPGレポート停止: " + ex.Message);
            MessageBox.Show(ex.Message, "JPG記録を確認してください", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }
    // R2.0: cross-format candidate report; no source is mutated, and no executable runs.
    private async void CombinedReport_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "親フォルダを確認"); return; }
        using var folder = new Forms.FolderBrowserDialog
        {
            Description = "PNG/JPG一括候補の記録がある保存先を指定",
            ShowNewFolderButton = false
        };
        if (folder.ShowDialog() != Forms.DialogResult.OK) return;
        string output = folder.SelectedPath;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        Busy(true);
        JobProgress.Value = 0;
        ProgressText.Text = "PNG/JPG候補の照合中";
        var recheckProgress = new Progress<(int Done, int Total)>(p =>
        {
            JobProgress.Value = p.Total == 0 ? 100 : 10.0 + 90.0 * p.Done / p.Total;
            ProgressText.Text = $"重複・候補を再照合中 {p.Done:N0}/{p.Total:N0}";
        });
        try
        {
            var lastScan = _lastScan;
            bool allowTimeForEstimate = TimeDifferenceCheck.IsChecked == true;
            var (report, combinedEstimate) = await Task.Run(() =>
            {
                var r = CombinedCapacityReport.Inspect(input, output, ct);
                CombinedSavingsPlanner.Estimate? estimate = null;
                if (lastScan != null &&
                    Path.GetFullPath(lastScan.Root).Equals(Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
                    estimate = CombinedSavingsPlanner.Analyze(r, lastScan,
                        allowTimeForEstimate, ct, recheckProgress);
                return (r, estimate);
            }, ct);
            JobProgress.Value = 100;
            ProgressText.Text = "統合候補レポート完了";
            string formatSummary = combinedEstimate is null ? "" :
                string.Join("\n", combinedEstimate.Formats.Select(f =>
                    $"{f.Format}: 重複 {GroupRow.HumanSize(f.DedupeOnlyBytes)}、" +
                    $"候補 {GroupRow.HumanSize(f.PreviewOnlyBytes)}、" +
                    $"重なり -{GroupRow.HumanSize(f.DoubleCountAvoidedBytes)}、" +
                    $"補正後 {GroupRow.HumanSize(f.CombinedHypotheticalBytes)}"));
            string overlapSummary = combinedEstimate is null
                ? "先に同じフォルダを読み取り専用スキャンすると、重複と圧縮の二重計上を分析できます。\n"
                : $"重複の理論削減量 {GroupRow.HumanSize(combinedEstimate.DedupeOnlyBytes)}\n" +
                  $"重複と圧縮の重なり {GroupRow.HumanSize(combinedEstimate.DoubleCountAvoidedBytes)}\n" +
                  $"重複も含めた非重複の理論上限（概算） {GroupRow.HumanSize(combinedEstimate.CombinedHypotheticalBytes)}\n" +
                  $"再検証済み重複グループ {combinedEstimate.VerifiedDuplicateGroups:N0}件・除外 {combinedEstimate.SkippedDuplicateGroups:N0}件\n\n" +
                  $"形式別の補正見込:\n{formatSummary}\n";
            string summary = $"PNG {report.PngFiles:N0}件・JPG {report.JpegFiles:N0}件\n" +
                $"有効な圧縮候補 {report.CandidateCount:N0}件・要確認 {report.NeedsReview:N0}件\n" +
                $"候補による削減見込 {GroupRow.HumanSize(report.EstimatedSavingBytes)}\n" +
                $"実際に回復した容量 {GroupRow.HumanSize(report.ActualFreedBytes)}\n\n" +
                overlapSummary + "\n" +
                "計算値は論理バイトの仮定上の目安で、実空き容量ではありません。\n" +
                "CSVに保存しますか？（重複スキャンがある場合は形式別補正表と候補詳細を選択可能）";
            Log(summary);
            if (MessageBox.Show(summary, "PNG/JPG統合候補レポート", MessageBoxButton.YesNo,
                MessageBoxImage.Information, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
            var save = new Microsoft.Win32.SaveFileDialog
            {
                Title = "統合容量CSV（原本・候補フォルダの外に保存）",
                Filter = combinedEstimate is null ? "候補詳細CSV (*.csv)|*.csv" :
                    "形式別の非重複見込CSV (*.csv)|*.csv|候補詳細CSV (*.csv)|*.csv",
                AddExtension = true,
                FileName = combinedEstimate is null
                    ? "chappy-r20-combined-report.csv"
                    : "chappy-r215-nonoverlapping-by-format.csv",
                OverwritePrompt = false
            };
            if (save.ShowDialog(this) == true)
            {
                ct.ThrowIfCancellationRequested();
                if (combinedEstimate is not null && save.FilterIndex == 1)
                    CombinedSavingsPlanner.ExportFormatCsv(combinedEstimate, input, output, save.FileName);
                else
                    CombinedCapacityReport.ExportCsv(report, input, output, save.FileName);
                Log("PNG/JPG統合CSV: " + save.FileName);
            }
        }
        catch (OperationCanceledException) { Log("統合候補レポートを中断しました。"); }
        catch (Exception ex)
        {
            Log("統合レポートを中止: " + ex.Message);
            MessageBox.Show(ex.Message, "記録を確認してください", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    // R2.2 advisory-only. Never modifies a source file or enables actual replacement.
    private async void ReplacementReadiness_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "親フォルダを確認"); return; }
        using var outPicker = new Forms.FolderBrowserDialog
        {
            Description = "JPG/PNG候補の記録がある保存先を選択（読み取り専用）",
            ShowNewFolderButton = false
        };
        if (outPicker.ShowDialog() != Forms.DialogResult.OK) return;
        using var backupPicker = new Forms.FolderBrowserDialog
        {
            Description = "原本の復旧コピーを保存できる別ボリュームを選択（何も書き込みません）",
            ShowNewFolderButton = false
        };
        if (backupPicker.ShowDialog() != Forms.DialogResult.OK) return;
        string output = outPicker.SelectedPath, backup = backupPicker.SelectedPath;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        Busy(true);
        ProgressText.Text = "容量回復の条件を読み取り検査中";
        try
        {
            var readiness = await Task.Run(() =>
            {
                var report = CombinedCapacityReport.Inspect(input, output, ct);
                return ReplacementReadiness.Inspect(report, input, output, backup, ct);
            }, ct);
            string examples = string.Join("\n", readiness.Rows
                .Where(r => !r.SpaceRequirementsMet).Take(5)
                .Select(r => $"・{r.RelativePath}: {r.Explanation}"));
            string message = $"圧縮候補 {readiness.CandidateCount:N0} 件\n" +
                $"容量・ファイル属性の事前条件合格 {readiness.SpaceFeasibleCount:N0} 件\n" +
                $"合格候補の理論削減見込 {GroupRow.HumanSize(readiness.SpaceFeasibleLogicalSavingBytes)}\n" +
                $"元ボリューム空き {GroupRow.HumanSize(readiness.InputVolumeFreeBytes)}\n" +
                $"バックアップ先空き {GroupRow.HumanSize(readiness.BackupVolumeFreeBytes)}\n" +
                $"バックアップ先が別ボリューム: {(readiness.IndependentBackupVolume ? "はい" : "いいえ")}\n" +
                $"実際に回復した容量 {GroupRow.HumanSize(readiness.ActualFreedBytes)}\n\n" +
                (examples.Length == 0 ? "" : "主な保留理由（最大5件）:\n" + examples + "\n\n") +
                "これは容量と属性の読み取り専用診断です。合格しても安全な原本置換は未実装・無効です。";
            Log(message);
            MessageBox.Show(message, "R2.2 容量回復の事前条件", MessageBoxButton.OK,
                MessageBoxImage.Information);
            ProgressText.Text = "事前条件診断完了（変更なし）";
        }
        catch (OperationCanceledException) { Log("事前条件診断を中断しました。"); }
        catch (Exception ex)
        {
            Log("事前条件診断を停止: " + ex.Message);
            MessageBox.Show(ex.Message, "安全のため停止", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    // Explicit, confirmed repair of a torn JPEG progress-journal tail only.
    private async void JpegRepair_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        string input;
        try { input = Safety.ValidateRoot(PathBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "親フォルダを確認"); return; }
        using var folder = new Forms.FolderBrowserDialog
        {
            Description = "JPG一括処理の記録がある候補保存先を指定",
            ShowNewFolderButton = false
        };
        if (folder.ShowDialog() != Forms.DialogResult.OK) return;
        string output = folder.SelectedPath;
        _work = new CancellationTokenSource();
        Busy(true);
        try
        {
            var result = await Task.Run(() => JpegBatchJournalTools.InspectTail(input, output));
            Log("JPGログ診断: " + result.State + " / " + result.Message);
            if (result.State is "Healthy" or "Empty")
            {
                MessageBox.Show(result.Message, "JPGログ診断", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            if (result.State is not ("PartialTail" or "MissingNewline"))
                throw new InvalidDataException("自動修復できない状態です。記録を手動確認してください。");
            if (MessageBox.Show(result.Message + "\n\n元ログの全バックアップを作成し、" +
                "修復を実行しますか？（原本・画像候補は一切変更しません）",
                "JPGログ修復の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var repaired = await Task.Run(() => JpegBatchJournalTools.RepairTail(input, output));
            Log($"JPGログ修復完了。バックアップ: {repaired.BackupPath} / " +
                $"除外した末尾バイト数: {repaired.RemovedTailBytes}");
            MessageBox.Show("進捗ログの末尾を修復しました。バックアップ: " + repaired.BackupPath,
                "JPGログ修復", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log("JPGログ診断・修復を中止: " + ex.Message);
            MessageBox.Show(ex.Message, "安全のため停止", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    private async void JpegPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Title = "無劣化候補を作成するJPEGを選択", Filter = "JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg"
        };
        if (open.ShowDialog(this) != true) return;
        var toolPicker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "libjpeg-turboのjpegtran.exeを選択（同梱していません）",
            Filter = "実行ファイル (jpegtran.exe)|jpegtran.exe", CheckFileExists = true
        };
        if (toolPicker.ShowDialog(this) != true) return;
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "JPEG候補の保存先（原本フォルダ外・空き容量がある場所）を選択",
            ShowNewFolderButton = true
        };
        if (picker.ShowDialog() != Forms.DialogResult.OK) return;
        string source = open.FileName, dest = picker.SelectedPath, exe = toolPicker.FileName;
        if (MessageBox.Show("jpegtran.exeでJPEGの符号化を最適化し、WIC画素比較と保護マーカー比較を行います。\n" +
            "元ファイルには一切書き込まず、失敗時や容量が減らない場合は候補を破棄します。\n\n続けますか？",
            "JPG読み取り・候補生成", MessageBoxButton.YesNo, MessageBoxImage.Information,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        Busy(true);
        ProgressText.Text = "JPG無劣化候補を検証中";
        try
        {
            var result = await Task.Run(() => JpegLosslessPreviewEngine.CreatePreview(source, dest, exe, ct), ct);
            if (result.Candidate is null)
            {
                Log("JPG候補なし: " + result.Explanation);
                MessageBox.Show(result.Explanation, "JPG候補なし");
            }
            else
            {
                Log($"JPG候補: {result.Candidate} / 見込削減 {GroupRow.HumanSize(result.EstimatedSavingBytes)}");
                MessageBox.Show("検証済み候補: " + result.Candidate + "\n\n原本は無変更。\n容量回復はまだ行いません。",
                    "JPG候補作成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            ProgressText.Text = "JPG候補の検証終了";
        }
        catch (OperationCanceledException) { Log("JPG候補を中断。元ファイル無変更。"); }
        catch (Exception ex)
        {
            Log("JPG候補の検証失敗: " + ex.Message);
            MessageBox.Show(ex.Message, "JPG候補を破棄・安全停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    private async void JpegInspect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var choose = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JPEG画像 (*.jpg;*.jpeg)|*.jpg;*.jpeg", Title = "読み取り専用で診断するJPGを選択"
        };
        if (choose.ShowDialog(this) != true) return;
        string path = choose.FileName;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        Busy(true);
        ProgressText.Text = "JPGの構造を検査中";
        try
        {
            var result = await Task.Run(() => JpegInspector.Inspect(path, token), token);
            string info = $"JPEG: {result.Width:N0} × {result.Height:N0} / {result.Precision}bit\n" +
                $"プログレッシブ: {result.Progressive}\nAPPマーカー: {result.ApplicationMarkers}\n" +
                $"EXIF: {result.HasExif} / ICC: {result.HasIcc} / XMP: {result.HasXmp}\n" +
                $"末尾EOI: {result.HasEndMarker}\n\n{result.Warning}\n" +
                "画像やメタデータは一切変更していません。";
            MessageBox.Show(info, "JPG読み取り診断", MessageBoxButton.OK, MessageBoxImage.Information);
            ProgressText.Text = "JPG読み取り診断完了";
            Log("JPG診断終了: " + path);
        }
        catch (OperationCanceledException) { Log("JPG診断を中断しました。"); }
        catch (Exception ex)
        {
            Log("JPG診断エラー: " + ex.Message);
            MessageBox.Show(ex.Message, "JPG診断を安全停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { Busy(false); _work?.Dispose(); _work = null; }
    }

    private async void InspectPng_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "読み取り専用で構造検査するPNGファイルを選択",
            Filter = "PNG画像 (*.png)|*.png",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;
        string chosen = dialog.FileName;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        Busy(true);
        ProgressText.Text = "PNG構造を検査中";
        try
        {
            var result = await Task.Run(() => PngInspector.Inspect(chosen, token));
            string message = $"ファイル: {chosen}\n" +
                $"画像: {result.Width:N0} × {result.Height:N0}\n" +
                $"IDATデータ: {GroupRow.HumanSize(result.IdatBytes)}\n" +
                $"チャンク数: {result.ChunkCount:N0}\n" +
                $"アニメーション: {(result.IsAnimated ? "あり" : "なし")}\n" +
                $"拡張メタデータ: {(result.HasAncillaryChunk ? "あり" : "なし")}\n" +
                $"未知の必須チャンク: {(result.HasUnknownCriticalChunk ? "あり" : "なし")}\n" +
                $"R2の無劣化圧縮候補: {(result.CandidateForFutureLosslessOptimization ? "候補" : "要除外")}\n\n" +
                "これは読み取り専用の構造・CRC診断です。画素の完全なデコード検証や圧縮はまだ実施しません。";
            MessageBox.Show(message, "PNG読み取り診断結果", MessageBoxButton.OK, MessageBoxImage.Information);
            Log("PNG診断完了: " + chosen);
            ProgressText.Text = "PNG診断完了";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "PNG診断を中断";
            Log("PNG診断を中断しました。元ファイルの変更はありません。");
        }
        catch (Exception ex)
        {
            ProgressText.Text = "PNG診断エラー";
            Log("PNG診断エラー: " + ex.Message);
            MessageBox.Show("PNGが破損している、または構造がサポート対象外です。\n" + ex.Message,
                "PNG診断失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Busy(false);
            _work?.Dispose(); _work = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        StopButton.IsEnabled = false;
        Log("中断を要求しました。安全な区切りまで処理して停止します。");
    }

    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new RecoveryWindow { Owner = this };
        dialog.ShowDialog();
        var pending = Journal.Pending();
        var damaged = Journal.UnresolvedRecordFiles();
        Log($"復旧画面を閉じました。未解決記録 {pending.Count:N0} 件、不正記録 {damaged.Count:N0} 件。");
        if (pending.Count != 0 || damaged.Count != 0)
            Log("未解決記録が残っています。新しい統合は引き続き禁止です。");
    }

    private void Store_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = Safety.ValidateRoot(PathBox.Text);
            var store = Safety.StorePath(root);
            if (!Directory.Exists(store)) { MessageBox.Show("まだ統合先フォルダは作成されていません。", "統合先なし"); return; }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{store}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "統合先を開けません", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
