using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ChappyStorageCompressor.Core;

namespace ChappyStorageCompressor;

/// <summary>
/// Actual product review screen. Performs async read-only preflight and
/// exposes per-candidate transient checks. There is NO mutation entrypoint.
/// Passing this view never unlocks Stage/Restore/Recycle/Delete in production.
/// </summary>
internal sealed class DuplicateCleanupReviewWindow : Window
{
    private static readonly Brush TextColor =
        new SolidColorBrush(Color.FromRgb(235, 235, 235));
    private static readonly Brush Secondary =
        new SolidColorBrush(Color.FromRgb(180, 180, 180));
    private static readonly Brush Panel =
        new SolidColorBrush(Color.FromRgb(29, 29, 29));

    private readonly DuplicateCleanupPreview.Result _plan;
    private readonly DuplicateGroup _scanned;
    private readonly List<(string Path, CheckBox Box)> _choices = new();
    private readonly TextBlock _status;
    private readonly TextBlock _summary;
    private readonly Button _preflightButton;
    private readonly Button _saveReviewButton;
    private readonly Button _loadReviewButton;
    private readonly Button _deleteButton;
    private readonly CancellationTokenSource _closeCancel = new();
    private bool _running;
    private bool _closing;
    private DuplicateCleanupPreflight.Report? _lastReport;
#if CHAPPY_TESTING
    // Compiled into the standalone smoke-test assembly ONLY, never the
    // shipped WinExe. The real WPF review screen shares its source with
    // this generated-only test, while all mutations use the existing Core.
    private string? _syntheticOwnedRoot;
    private string? _syntheticId;
    private readonly Button _syntheticStageButton;
    private readonly Button _syntheticRestoreButton;
    private readonly Button _syntheticConfirmButton;
    private readonly Button _syntheticRecycleButton;
    private readonly TextBlock _syntheticStatus;
    private bool _syntheticFailed;
#endif

    internal DuplicateCleanupReviewWindow(DuplicateCleanupPreview.Result plan,
        DuplicateGroup scanned)
    {
        _plan = plan;
        _scanned = scanned;
        Title = "重複ファイルの整理予定・削除前確認（未実行）";
        Width = 930;
        Height = 680;
        MinWidth = 730;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 17, 17));
        Foreground = TextColor;
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");

        var container = new DockPanel
        {
            Margin = new Thickness(18),
            LastChildFill = true
        };

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "残すファイル: " + plan.KeeperPath,
            Foreground = TextColor,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "最上位に「同名ファイル」を作る予定と元ディレクトリの一覧です。" +
                   " チェックは仮確認であり削除の同意ではありません。",
            Foreground = Secondary,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        DockPanel.SetDock(header, Dock.Top);
        container.Children.Add(header);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(new TextBlock
        {
            Text = "削除候補ごとの確認（アプリ内へ保存可能・実処理は未解禁）",
            Foreground = TextColor,
            FontWeight = FontWeights.SemiBold
        });
        var checks = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        foreach (var candidate in plan.DeleteCandidates.Take(1000))
        {
            string path = candidate.OriginalPath;
            var choice = new CheckBox
            {
                Foreground = TextColor,
                Content = candidate.OriginalRelativePath + "  →  " +
                          candidate.ProposedCollectionRelativePath,
                IsChecked = false,
                Margin = new Thickness(0, 5, 0, 5),
                ToolTip = "元のフルパス: " + path +
                          "\n確認を保存するとアプリの設定領域にだけ記録されます。元ファイルは変更されません。"
            };
            choice.Checked += (_, _) => ResetVerification();
            choice.Unchecked += (_, _) => ResetVerification();
            _choices.Add((path, choice));
            checks.Children.Add(choice);
        }
        var scroll = new ScrollViewer
        {
            Content = checks,
            Height = 123,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        footer.Children.Add(scroll);

        _status = new TextBlock
        {
            Text = "確認済み 0 / " + plan.DeleteCandidates.Count +
                   " 件（保存は明示操作。移動・削除は未解禁）",
            Foreground = Secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 3)
        };
        footer.Children.Add(_status);
        _summary = new TextBlock
        {
            Text = "照合前：検索から時間が経つと元ファイルや整理先が変わる場合があります。",
            Foreground = new SolidColorBrush(Color.FromRgb(218, 180, 112)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 9)
        };
        footer.Children.Add(_summary);

        var actions = new WrapPanel();
        actions.Children.Add(MakeButton("一覧メモをコピー", (_, _) =>
        {
            try { Clipboard.SetText(plan.Describe()); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "コピーできません",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, "元パスと整理予定を未実行のテキストとしてコピーします。"));

        _preflightButton = MakeButton("最終照合（読み取り専用）",
            Preflight_Click,
            "チェックした候補だけでなくグループ全体を再照合します。NTFS ID・属性・権限・SHA-256・全バイトと整理先の衝突を確認します。変更は行いません。");
        _preflightButton.IsEnabled = plan.DeleteCandidates.Count <= 1000;
        actions.Children.Add(_preflightButton);
        _saveReviewButton = MakeButton("照合済みの確認を保存", SaveReview_Click,
            "全候補・残すファイルを再照合し、チェック内容だけをアプリ専用領域へ保存します。削除は行いません。");
        _saveReviewButton.Name = "SaveReviewedButton";
        _saveReviewButton.IsEnabled = false;
        actions.Children.Add(_saveReviewButton);
        _loadReviewButton = MakeButton("保存した確認を復元", LoadReview_Click,
            "保存済みのチェックをファイルの再照合後に表示します。削除は許可されません。");
        _loadReviewButton.Name = "LoadReviewedButton";
        _loadReviewButton.IsEnabled = plan.DeleteCandidates.Count <= 1000;
        actions.Children.Add(_loadReviewButton);
        _deleteButton = MakeButton("確認済みをごみ箱へ（未解禁）",
            (_, _) => { },
            "このビルドでは使用不可。本番のごみ箱・復元検証と独立レビュー・明示承認が必要です。");
        _deleteButton.IsEnabled = false;
        actions.Children.Add(_deleteButton);
#if CHAPPY_TESTING
        _syntheticStageButton = MakeButton("生成データ: 保留2件＋メモ",
            (_, _) => SyntheticStage(), "専用TEMP+sentinel上の人工データだけを操作");
        _syntheticRestoreButton = MakeButton("生成データ: 元パスへ1件復元",
            (_, _) => SyntheticRestore(), "NTFS IDを固定した元パスへの復帰");
        _syntheticConfirmButton = MakeButton("生成データ: 1件だけ永続確認",
            (_, _) => SyntheticConfirm(), "選択済み人工ファイルのみ永続確認");
        _syntheticRecycleButton = MakeButton("生成データ: 模擬ごみ箱へ1件",
            (_, _) => SyntheticRecycle(), "本物のごみ箱・永久削除は使わない");
        _syntheticStageButton.Name = "SyntheticStageButton";
        _syntheticRestoreButton.Name = "SyntheticRestoreButton";
        _syntheticConfirmButton.Name = "SyntheticConfirmButton";
        _syntheticRecycleButton.Name = "SyntheticRecycleButton";
        foreach (var button in new[] {
            _syntheticStageButton, _syntheticRestoreButton,
            _syntheticConfirmButton, _syntheticRecycleButton
        })
        {
            button.IsEnabled = false;
            actions.Children.Add(button);
        }
        _syntheticStatus = new TextBlock {
            Text = "テスト用生成NTFSの操作は未設定",
            Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4)
        };
        footer.Children.Add(_syntheticStatus);
#endif
        actions.Children.Add(MakeButton("閉じる", (_, _) => Close(),
            "キャンセルしてもファイルは変更されません。"));
        footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom);
        container.Children.Add(footer);

        var memo = new TextBox
        {
            Text = plan.Describe(),
            IsReadOnly = true,
            Background = Panel,
            Foreground = TextColor,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        container.Children.Add(memo);
        Content = container;

        if (plan.DeleteCandidates.Count > 1000)
            _summary.Text = "安全停止：候補が1000件を超えるため、この画面では照合を実行しません。対象を分割してください。";
        Closing += (_, _) =>
        {
            _closing = true;
            _closeCancel.Cancel();
        };
    }

    private static Button MakeButton(string caption, RoutedEventHandler action,
        string tooltip)
    {
        var button = new Button
        {
            Content = caption,
            ToolTip = tooltip,
            Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            Foreground = TextColor,
            Margin = new Thickness(0, 0, 7, 3),
            Padding = new Thickness(11, 8, 11, 8)
        };
        button.Click += action;
        return button;
    }

    private void ResetVerification()
    {
        if (_running) return;
        _lastReport = null;
        _saveReviewButton.IsEnabled = false;
#if CHAPPY_TESTING
        if (_syntheticId is null) SyntheticRefresh();
#endif
        int checkedCount = _choices.Count(c => c.Box.IsChecked == true);
        _status.Text = $"確認済み {checkedCount} / {_plan.DeleteCandidates.Count} 件 " +
                       "（画面限り。移動・削除は未解禁）";
        _summary.Text = "チェックを変更したため再照合が必要です。";
    }

    private async void Preflight_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _closing || _plan.DeleteCandidates.Count > 1000) return;
        _running = true;
        _lastReport = null;
        _preflightButton.IsEnabled = false;
        _saveReviewButton.IsEnabled = false;
        _loadReviewButton.IsEnabled = false;
        foreach (var item in _choices) item.Box.IsEnabled = false;
        _summary.Text = "元データを読み取って安全照合中…（データ変更なし）";
        string[] checkedPaths = _choices
            .Where(c => c.Box.IsChecked == true)
            .Select(c => c.Path).ToArray();

        try
        {
            var report = await Task.Run(() => DuplicateCleanupPreflight.Check(
                _plan, _scanned, checkedPaths, _closeCancel.Token));
            if (_closing) return;
            _lastReport = report;
            _saveReviewButton.IsEnabled = report.Passed;
#if CHAPPY_TESTING
            SyntheticRefresh();
#endif
            _status.Text = $"選択 {report.CheckedCount} 件・照合合格 " +
                           $"{report.VerifiedCount} / {report.Candidates.Count} 件";
            _summary.Text = report.Summary +
                (report.Passed
                    ? "\n照合は今この瞬間の結果であり、ファイル変更や削除許可ではありません。"
                    : "");
            foreach (var (path, choice) in _choices)
            {
                var row = report.Candidates.Single(x =>
                    string.Equals(x.OriginalPath, path,
                        StringComparison.OrdinalIgnoreCase));
                choice.ToolTip = row.Reason + "\n元のパス: " + path;
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing)
                _summary.Text = "照合を中断しました。ファイルは変更していません。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException)
        {
            if (!_closing)
                _summary.Text = "安全停止：" + ex.Message +
                    "。ファイルの移動・削除は行っていません。";
        }
        finally
        {
            _running = false;
            if (!_closing)
            {
                _preflightButton.IsEnabled = _plan.DeleteCandidates.Count <= 1000;
                _loadReviewButton.IsEnabled = _plan.DeleteCandidates.Count <= 1000;
                foreach (var item in _choices) item.Box.IsEnabled = true;
#if CHAPPY_TESTING
                SyntheticRefresh();
#endif
            }
        }
    }
    private async void SaveReview_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _closing || _lastReport?.Passed != true) return;
        string[] selected = _choices.Where(c => c.Box.IsChecked == true)
            .Select(c => c.Path).ToArray();
        if (_lastReport.CheckedCount != selected.Length ||
            _lastReport.Candidates.Any(c =>
                c.CheckedByUser != selected.Contains(c.OriginalPath,
                    StringComparer.OrdinalIgnoreCase)))
        {
            _summary.Text = "チェックが照合結果と一致しません。最終照合をやり直してください。";
            ResetVerification();
            return;
        }
        _running = true;
        _saveReviewButton.IsEnabled = false;
        _loadReviewButton.IsEnabled = false;
        _preflightButton.IsEnabled = false;
        foreach (var item in _choices) item.Box.IsEnabled = false;
        _summary.Text = "元ファイル・整理先をもう一度照合して確認内容だけを保存中…";
        try
        {
            var result = await Task.Run(() =>
                DuplicateCleanupReviewStore.Save(_plan, _scanned, selected,
                    _closeCancel.Token));
            if (_closing) return;
            _lastReport = null;
            _status.Text = $"保存済み確認 {result.CheckedPaths.Count} 件 / " +
                           _plan.DeleteCandidates.Count + " 件（削除不可）";
            _summary.Text = result.Summary +
                "\n実行操作には別の独立審査と直前照合が必要です。";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) _summary.Text = "保存を中断しました。元ファイルは変更していません。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or ArgumentException or
            NotSupportedException)
        {
            if (!_closing)
                _summary.Text = "確認内容を保存できません：" + ex.Message +
                    "。元ファイルは一切変更していません。";
        }
        finally
        {
            _running = false;
            if (!_closing)
            {
                _preflightButton.IsEnabled = _plan.DeleteCandidates.Count <= 1000;
                _loadReviewButton.IsEnabled = _plan.DeleteCandidates.Count <= 1000;
                foreach (var item in _choices) item.Box.IsEnabled = true;
            }
        }
    }

    private async void LoadReview_Click(object sender, RoutedEventArgs e)
    {
        if (_running || _closing || _plan.DeleteCandidates.Count > 1000) return;
        _running = true;
        _saveReviewButton.IsEnabled = false;
        _loadReviewButton.IsEnabled = false;
        _preflightButton.IsEnabled = false;
        foreach (var item in _choices) item.Box.IsEnabled = false;
        _lastReport = null;
        _summary.Text = "保存済み確認と元ファイル・整理先を再照合中…";
        try
        {
            var result = await Task.Run(() =>
                DuplicateCleanupReviewStore.Load(_plan, _scanned,
                    _closeCancel.Token));
            if (_closing) return;
            if (result.Exists && result.Valid)
            {
                var restored = new HashSet<string>(result.CheckedPaths,
                    StringComparer.OrdinalIgnoreCase);
                foreach (var (path, box) in _choices)
                    box.IsChecked = restored.Contains(path);
                _status.Text = $"保存済み確認 {restored.Count} 件 / " +
                               _plan.DeleteCandidates.Count +
                               " 件（再照合が必要。削除不可）";
            }
            _summary.Text = result.Summary +
                "\nこの記録は実行権限ではありません。削除ボタンは引き続き無効です。";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) _summary.Text = "確認内容の読み取りを中断しました。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or ArgumentException or
            NotSupportedException)
        {
            if (!_closing)
                _summary.Text = "確認内容を復元できません：" + ex.Message +
                    "。元ファイルは変更していません。";
        }
        finally
        {
            _running = false;
            if (!_closing)
            {
                _preflightButton.IsEnabled = _plan.DeleteCandidates.Count <= 1000;
                _loadReviewButton.IsEnabled = _plan.DeleteCandidates.Count <= 1000;
                foreach (var item in _choices) item.Box.IsEnabled = true;
            }
        }
    }

#if CHAPPY_TESTING
    /// <summary>
    /// Only the isolated Core smoke assembly compiles this entrypoint. The
    /// generated root is revalidated by BeginSynthetic/StageSynthetic on each
    /// mutation, so a forged UI flag cannot bypass the NTFS sentinel.
    /// The UI is deliberately NOT made into a production mutation adapter.
    /// </summary>
    internal void ArmGeneratedOnlyForTests(string generatedParent)
    {
        if (_syntheticId is not null || _running || _closing)
            throw new InvalidOperationException("Synthetic UI transaction already active");
        string full = Safety.ValidateRoot(generatedParent);
        string temp = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetTempPath()));
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar +
                "ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(full, ".chappy-part23-generated-only")) ||
            File.ReadAllText(Path.Combine(full, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only" ||
            !_plan.Root.StartsWith(full + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            _plan.DeleteCandidates.Count != 2)
            throw new InvalidOperationException("Generated-only review UI root rejected");
        _syntheticOwnedRoot = full;
        _syntheticStatus.Text = "専用TEMPの生成3件のみ。操作にはまず1候補のチェックと最終照合が必要。";
        SyntheticRefresh();
    }

    private void SyntheticRefresh()
    {
        bool armed = _syntheticOwnedRoot is not null &&
            !_syntheticFailed && !_closing && !_running;
        bool ready = armed && _lastReport?.Passed == true &&
            _lastReport.CheckedCount == 1 && _choices.Count == 2 &&
            _choices[0].Box.IsChecked == true &&
            _choices[1].Box.IsChecked != true;
        _syntheticStageButton.IsEnabled = ready && _syntheticId is null;
        _syntheticRestoreButton.IsEnabled = false;
        _syntheticConfirmButton.IsEnabled = false;
        _syntheticRecycleButton.IsEnabled = false;
        if (!armed || _syntheticId is null) return;
        try
        {
            var states = DuplicateCleanupWorkflow.InspectSynthetic(
                _syntheticOwnedRoot!, _plan.Root, _syntheticId);
            if (states.Count != 2) throw new InvalidDataException("Synthetic states count differs");
            string a = states[0].State, b = states[1].State;
            _syntheticRestoreButton.IsEnabled = b == "StagedUnconfirmed" &&
                a == "StagedUnconfirmed";
            _syntheticConfirmButton.IsEnabled = a == "StagedUnconfirmed" &&
                b == "RestoredOriginal";
            _syntheticRecycleButton.IsEnabled = a == "ConfirmedStaged" &&
                b == "RestoredOriginal";
            _syntheticStatus.Text = "実行ID: " + _syntheticId +
                " | 人工候補0=" + a + " / 候補1=" + b +
                " / 本番削除許可=false";
        }
        catch (Exception ex)
        {
            SyntheticStop("現在の証拠を判定できません：" + ex.GetType().Name);
        }
    }

    private void SyntheticStop(string problem)
    {
        _syntheticFailed = true;
        foreach (var button in new[] { _syntheticStageButton,
            _syntheticRestoreButton, _syntheticConfirmButton, _syntheticRecycleButton })
            button.IsEnabled = false;
        _syntheticStatus.Text = "人工処理は安全停止。再実行せず要手動確認：" + problem;
    }

    private void SyntheticExecute(Action step, string description)
    {
        if (_syntheticOwnedRoot is null || _syntheticFailed || _closing ||
            _running || DuplicateCleanupWorkflow.ProductionMutationEnabled)
        {
            SyntheticStop("実行対象・操作状態が未確認です");
            return;
        }
        try
        {
            // All stages synchronously validate and commit durable evidence;
            // only tiny generated 64KiB files are used in UI smoke.
            step();
            _syntheticStatus.Text = description;
            SyntheticRefresh();
        }
        catch (Exception ex)
        {
            SyntheticStop(description + "：" + ex.GetType().Name + " " + ex.Message);
        }
    }

    private void SyntheticStage()
    {
        if (!_syntheticStageButton.IsEnabled ||
            _syntheticOwnedRoot is null || _syntheticId is not null ||
            _lastReport?.Passed != true ||
            _choices.Count != 2 || _choices[0].Box.IsChecked != true ||
            _choices[1].Box.IsChecked == true)
        {
            SyntheticStop("保留前の1件のみの確認・最終照合が不一致");
            return;
        }
        SyntheticExecute(() =>
        {
            string id = DuplicateCleanupWorkflow.BeginSynthetic(
                _syntheticOwnedRoot, _plan, _scanned);
            _syntheticId = id;
            DuplicateCleanupWorkflow.StageSynthetic(_syntheticOwnedRoot,
                _plan.Root, id, 0);
            DuplicateCleanupWorkflow.StageSynthetic(_syntheticOwnedRoot,
                _plan.Root, id, 1);
            DuplicateCleanupWorkflow.WriteReportSynthetic(_syntheticOwnedRoot,
                _plan.Root, id);
            _lastReport = null;
            _saveReviewButton.IsEnabled = false;
            _loadReviewButton.IsEnabled = false;
            _preflightButton.IsEnabled = false;
            foreach (var item in _choices) item.Box.IsEnabled = false;
        }, "人工候補2件の保留＋最上位メモ記録完了");
    }

    private void SyntheticRestore()
    {
        if (!_syntheticRestoreButton.IsEnabled ||
            _syntheticOwnedRoot is null || _syntheticId is null) return;
        SyntheticExecute(() => DuplicateCleanupWorkflow.RestoreSynthetic(
            _syntheticOwnedRoot, _plan.Root, _syntheticId, 1),
            "人工候補1を元パスへ復元");
    }

    private void SyntheticConfirm()
    {
        if (!_syntheticConfirmButton.IsEnabled ||
            _syntheticOwnedRoot is null || _syntheticId is null) return;
        SyntheticExecute(() => DuplicateCleanupWorkflow.ConfirmSynthetic(
            _syntheticOwnedRoot, _plan.Root, _syntheticId, 0, true),
            "人工候補0のみ永続確認");
    }

    private void SyntheticRecycle()
    {
        if (!_syntheticRecycleButton.IsEnabled ||
            _syntheticOwnedRoot is null || _syntheticId is null) return;
        SyntheticExecute(() => DuplicateCleanupWorkflow.RecycleSimulatedSynthetic(
            _syntheticOwnedRoot, _plan.Root, _syntheticId, 0),
            "人工候補0を模擬ごみ箱へ。実Windowsごみ箱は別のCI試験");
    }

    internal string? GeneratedOnlyOperationIdForTests => _syntheticId;
    internal string GeneratedOnlyStatusForTests => _syntheticStatus.Text;
#endif

}
