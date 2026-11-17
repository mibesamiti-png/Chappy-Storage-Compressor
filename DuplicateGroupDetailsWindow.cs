using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

namespace ChappyStorageCompressor;

/// <summary>
/// Read-only group inspector. The selected keeper is a UI proposal only:
/// this window never calls file deletion, hard-link mutation or Undo.
/// </summary>
internal sealed class DuplicateGroupDetailsWindow : Window
{
    private sealed record DisplayFile(FileSnapshot Snapshot)
    {
        public string Name => System.IO.Path.GetFileName(Snapshot.Path);
        public string Folder => System.IO.Path.GetDirectoryName(Snapshot.Path) ?? "";
        public string FullPath => Snapshot.Path;
        public string Size => GroupRow.HumanSize(Snapshot.Length);
        public string Modified => Snapshot.ModifiedUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    }

    private static readonly Brush Light = new SolidColorBrush(Color.FromRgb(239, 239, 239));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(178, 178, 178));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(27, 27, 27));

    private readonly DuplicateGroup _group;
    private readonly string? _scanRoot;
    private readonly IReadOnlyList<DisplayFile> _files;
    private readonly DataGrid _grid;
    private readonly Image _preview;
    private readonly TextBlock _previewMessage;
    private readonly TextBlock _path;
    private readonly TextBlock _keeperLabel;
    private DisplayFile _keeper;
    private DisplayFile? _selected;
    private long _previewSequence;

    internal DuplicateGroupDetailsWindow(DuplicateGroup group) : this(group, null) { }

    internal DuplicateGroupDetailsWindow(DuplicateGroup group, string? scanRoot)
    {
        _group = group;
        _scanRoot = scanRoot;
        if (group.Files.Count == 0) throw new ArgumentException("Empty duplicate group", nameof(group));
        _files = group.Files.Select(file => new DisplayFile(file)).ToArray();
        _keeper = _files[0];

        Title = "重複グループの詳細 — 読み取り専用";
        Width = 1060;
        Height = 670;
        MinWidth = 790;
        MinHeight = 490;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 17, 17));
        Foreground = Light;
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock
        {
            Text = $"完全一致グループ  ·  {_files.Count}件  ·  1件 {GroupRow.HumanSize(group.Representative.Length)}",
            FontSize = 19, FontWeight = FontWeights.SemiBold, Foreground = Light
        });
        header.Children.Add(new TextBlock
        {
            Text = "全形式を確認できます。画像のみプレビューします。ここではファイルを削除しません。",
            FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0)
        });
        var sha = new TextBox
        {
            Text = "SHA-256: " + group.Hash,
            IsReadOnly = true,
            FontSize = 11,
            Background = Panel,
            Foreground = Muted,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 9, 0, 0),
            ToolTip = "検出時点の完全一致を表すハッシュ。ファイルの状態は後から変更される場合があります。"
        };
        header.Children.Add(sha);
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(13) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            Background = Panel,
            Foreground = Light,
            RowBackground = Panel,
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(37, 37, 37)),
            HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(55, 55, 55)),
            BorderThickness = new Thickness(0),
            ItemsSource = _files
        };
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "ファイル名", Binding = new Binding(nameof(DisplayFile.Name)), Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "保存フォルダ", Binding = new Binding(nameof(DisplayFile.Folder)), Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "サイズ", Binding = new Binding(nameof(DisplayFile.Size)), Width = new DataGridLength(95)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "更新日時", Binding = new Binding(nameof(DisplayFile.Modified)), Width = new DataGridLength(120)
        });
        _grid.SelectionChanged += (_, _) => DisplaySelection();
        _grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject child) return;
            for (DependencyObject? node = child; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is DataGridRow row)
                {
                    _grid.SelectedItem = row.Item;
                    break;
                }
                if (ReferenceEquals(node, _grid)) break;
            }
        };
        _grid.ContextMenu = CreateContextMenu();
        Grid.SetColumn(_grid, 0);
        columns.Children.Add(_grid);

        var viewer = new StackPanel();
        viewer.Children.Add(new TextBlock
        {
            Text = "プレビュー", Foreground = Light, FontSize = 15,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
        });
        var frame = new Border
        {
            Background = Panel, CornerRadius = new CornerRadius(10),
            Height = 300, Padding = new Thickness(12)
        };
        var layer = new Grid();
        _preview = new Image
        {
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
            ToolTip = "右クリックで画像を開く・保存場所を開く・パスをコピー"
        };
        _previewMessage = new TextBlock
        {
            Text = "左のファイルを選択してください",
            Foreground = Muted,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        layer.Children.Add(_preview);
        layer.Children.Add(_previewMessage);
        frame.Child = layer;
        frame.ContextMenu = CreateContextMenu();
        viewer.Children.Add(frame);
        _path = new TextBlock
        {
            Text = "", Foreground = Muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
            FontSize = 11
        };
        viewer.Children.Add(_path);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(MakeButton("ファイルの場所を開く", (_, _) => OpenFolder(), "選択したファイルをエクスプローラーで選択表示します。"));
        actions.Children.Add(MakeButton("パスをコピー", (_, _) => CopyPath(), "選択したファイルのフルパスをコピーします。"));
        viewer.Children.Add(actions);
        Grid.SetColumn(viewer, 2);
        columns.Children.Add(viewer);

        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(MakeButton("残すファイルに指定（予定）", (_, _) => ChooseKeeper(),
            "この画面内の選択だけを変更します。削除・ごみ箱への移動は行いません。"));
        var reviewButton = MakeButton("整理先・削除候補を確認", (_, _) => ReviewCleanupPlan(),
            "一番上の親フォルダに『同名ファイル』を作る予定と元パス一覧を確認します。ファイル変更は未実行。");
        reviewButton.IsEnabled = !string.IsNullOrWhiteSpace(_scanRoot);
        right.Children.Add(reviewButton);
        right.Children.Add(MakeButton("閉じる", (_, _) => Close(), "確認画面を閉じます。"));
        DockPanel.SetDock(right, Dock.Right);
        footer.Children.Add(right);
        var left = new StackPanel();
        _keeperLabel = new TextBlock { Foreground = Light, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        left.Children.Add(_keeperLabel);
        left.Children.Add(new TextBlock
        {
            Text = "削除候補は確認のみ。安全な削除機能が完成するまでは実行できません。",
            Foreground = new SolidColorBrush(Color.FromRgb(213, 184, 121)),
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
        });
        footer.Children.Add(left);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        UpdateKeeper();
        _grid.SelectedIndex = 0;
    }

    private static Button MakeButton(string label, RoutedEventHandler action, string tooltip)
    {
        var button = new Button
        {
            Content = label, ToolTip = tooltip,
            Background = new SolidColorBrush(Color.FromRgb(48, 48, 48)),
            Foreground = Light,
            Padding = new Thickness(11, 8, 11, 8),
            Margin = new Thickness(0, 0, 7, 0),
        };
        button.Click += action;
        return button;
    }

    private ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "画像を開く" };
        open.Click += (_, _) => OpenImage();
        var folder = new MenuItem { Header = "ファイルの場所を開く" };
        folder.Click += (_, _) => OpenFolder();
        var copy = new MenuItem { Header = "ファイルパスをコピー" };
        copy.Click += (_, _) => CopyPath();
        menu.Items.Add(open);
        menu.Items.Add(folder);
        menu.Items.Add(copy);
        menu.Opened += (_, _) =>
        {
            open.Visibility = _selected is { } item && IsImage(item.FullPath)
                ? Visibility.Visible : Visibility.Collapsed;
            folder.IsEnabled = _selected is not null;
            copy.IsEnabled = _selected is not null;
        };
        return menu;
    }

    private void ChooseKeeper()
    {
        if (_selected is null) return;
        _keeper = _selected;
        UpdateKeeper();
    }

    private void UpdateKeeper()
    {
        _keeperLabel.Text = "残す予定: " + _keeper.FullPath +
            $"  （削除候補は {_files.Count - 1} 件・未実行）";
    }

    private void ReviewCleanupPlan()
    {
        if (string.IsNullOrWhiteSpace(_scanRoot)) return;
        DuplicateCleanupPreview.Result plan;
        try
        {
            plan = DuplicateCleanupPreview.Inspect(_scanRoot, _group, _keeper.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or OverflowException)
        {
            MessageBox.Show(this, ex.Message + "\nスキャン結果と実際のファイル状態を確認してください。",
                "整理先の確認を中止しました", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new Window
        {
            Owner = this,
            Title = "重複ファイルの整理予定・削除前確認（未実行）",
            Width = 900,
            Height = 620,
            MinWidth = 680,
            MinHeight = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Light,
            FontFamily = FontFamily
        };
        var content = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        var header = new TextBlock
        {
            Text = "残すファイルを固定し、元ディレクトリを記録した整理先を確認します。\n" +
                   "同名ファイル/元フォルダ構造/連番と、最上位の同名ファイル一覧.txtの方式を再利用します。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Light,
            Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);

        var status = new TextBlock
        {
            Text = "確認済み 0 / " + plan.DeleteCandidates.Count +
                   " 件（画面上の予定のみ、削除機能は未解禁）",
            Foreground = Muted,
            Margin = new Thickness(0, 8, 0, 6)
        };
        var individual = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 4)
        };
        var choices = new List<CheckBox>();
        void RefreshReviewCount()
        {
            int count = choices.Count(x => x.IsChecked == true);
            status.Text = $"確認済み {count} / {choices.Count} 件 " +
                          "（未保存の仮選択。実際の削除はできません）";
        }
        foreach (var candidate in plan.DeleteCandidates)
        {
            var cb = new CheckBox
            {
                Foreground = Light,
                IsChecked = false,
                Content = "確認済みにする: " + candidate.OriginalRelativePath +
                          "   →   " + candidate.ProposedCollectionRelativePath,
                ToolTip = "この行の確認状態は保存されません。元パスと整理予定先を照合してください。",
                Margin = new Thickness(0, 5, 0, 5)
            };
            cb.Checked += (_, _) => RefreshReviewCount();
            cb.Unchecked += (_, _) => RefreshReviewCount();
            choices.Add(cb);
            individual.Children.Add(cb);
        }
        var reviewList = new ScrollViewer
        {
            Height = 110,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = individual
        };
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(new TextBlock
        {
            Text = "削除候補ごとの確認（まだ実ファイルへ適用しません）",
            Foreground = Light,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        footer.Children.Add(reviewList);
        footer.Children.Add(status);
        var buttons = new WrapPanel();
        buttons.Children.Add(MakeButton("一覧メモをコピー", (_, _) =>
        {
            try { Clipboard.SetText(plan.Describe()); }
            catch (Exception ex) { ShowError(ex); }
        }, "保存先と元パスを含む、未実行の整理予定表をコピーします。"));
        var delete = MakeButton("確認後にごみ箱へ移動（未解禁）", (_, _) => { },
            "実装・復元確認・競合検証・実機受入・明示承認が済むまで操作できません。");
        delete.IsEnabled = false;
        buttons.Children.Add(delete);
        buttons.Children.Add(MakeButton("閉じる", (_, _) => dialog.Close(),
            "対象ファイルは一切変更されません。"));
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);

        var memo = new TextBox
        {
            Text = plan.Describe(),
            IsReadOnly = true,
            Background = Panel,
            Foreground = Light,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12),
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        content.Children.Add(memo);
        dialog.Content = content;
        dialog.ShowDialog();
    }

    private static bool IsImage(string path)
    {
        return System.IO.Path.GetExtension(path).ToLowerInvariant() is
            ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp";
    }

    private async void DisplaySelection()
    {
        _selected = _grid.SelectedItem as DisplayFile;
        long sequence = ++_previewSequence;
        _preview.Source = null;
        _preview.Visibility = Visibility.Collapsed;
        _previewMessage.Visibility = Visibility.Visible;
        if (_selected is not { } item)
        {
            _path.Text = "";
            _previewMessage.Text = "左のファイルを選択してください";
            return;
        }
        _path.Text = item.FullPath;
        if (!IsImage(item.FullPath))
        {
            _previewMessage.Text = "画像以外のプレビューは表示しません。\n「ファイルの場所を開く」で確認できます。";
            return;
        }

        _previewMessage.Text = "画像を読み込んでいます…";
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var fileInfo = new FileInfo(item.FullPath);
                if (!fileInfo.Exists) throw new FileNotFoundException("ファイルが見つかりません");
                if (fileInfo.Length > 64L * 1024 * 1024)
                    throw new IOException("64MBを超える画像は安全のためプレビューを省略します");
                using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.DecodePixelWidth = 920;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            });
            if (sequence != _previewSequence) return;
            _preview.Source = bitmap;
            _preview.Visibility = Visibility.Visible;
            _previewMessage.Visibility = Visibility.Collapsed;
        }
        // Image decoders may throw FileFormatException or a WIC COMException for
        // malformed input. A failed thumbnail must never crash the inspector.
        catch (Exception ex)
        {
            if (sequence != _previewSequence) return;
            _previewMessage.Text = "この画像はプレビューできません。\n" + ex.Message +
                "\nファイルの場所から確認できます。";
        }
    }

    private void OpenImage()
    {
        if (_selected is not { } item || !IsImage(item.FullPath)) return;
        OpenPath(item.FullPath, file: true);
    }

    private void OpenFolder()
    {
        if (_selected is not { } item) return;
        OpenPath(item.FullPath, file: false);
    }

    private void CopyPath()
    {
        if (_selected is not { } item) return;
        try { Clipboard.SetText(item.FullPath); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenPath(string path, bool file)
    {
        try
        {
            if (!File.Exists(path)) throw new FileNotFoundException("ファイルが移動または削除されています", path);
            if (file)
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                {
                    UseShellExecute = true
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowError(ex);
        }
    }

    private void ShowError(Exception ex) =>
        MessageBox.Show(this, ex.Message, "ファイルの確認ができません",
            MessageBoxButton.OK, MessageBoxImage.Warning);
}
