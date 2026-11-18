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
    private readonly Button _deleteButton;
    private readonly CancellationTokenSource _closeCancel = new();
    private bool _running;
    private bool _closing;
    private DuplicateCleanupPreflight.Report? _lastReport;

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
            Text = "削除候補ごとの仮確認（未保存・未実行）",
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
                          "\nこのチェックは画面限りで、ディスクや履歴を変更しません。"
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
                   " 件（画面限り。移動・削除は未解禁）",
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
        _deleteButton = MakeButton("確認済みをごみ箱へ（未解禁）",
            (_, _) => { },
            "このビルドでは使用不可。本番のごみ箱・復元検証と独立レビュー・明示承認が必要です。");
        _deleteButton.IsEnabled = false;
        actions.Children.Add(_deleteButton);
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
                _preflightButton.IsEnabled = true;
                foreach (var item in _choices) item.Box.IsEnabled = true;
            }
        }
    }
}
