using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ChappyStorageCompressor.Core;

namespace ChappyStorageCompressor;

/// <summary>
/// View-only evidence browser. It has no file-writing, recycle or restore
/// command. Historical filenames are not trusted as proof of NTFS identity.
/// </summary>
internal sealed class DuplicateCleanupHistoryWindow : Window
{
    public DuplicateCleanupHistoryWindow(string root,
        IReadOnlyList<DuplicateCleanupHistory.Row> rows)
    {
        Title = "重複整理の保留記録 — 読み取り専用";
        Width = 1160;
        Height = 680;
        MinWidth = 780;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(17, 17, 17));
        Foreground = new SolidColorBrush(Color.FromRgb(241, 241, 241));
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");

        var layout = new Grid { Margin = new Thickness(18) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock
        {
            Text = "保留記録・元のファイル場所",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold
        });
        head.Children.Add(new TextBlock
        {
            Text = "検索フォルダ: " + root,
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 2)
        });
        head.Children.Add(new TextBlock
        {
            Text = "保留記録 " + rows.Count + " 行。場所・記録の存在だけを確認し、ファイルID・SHA-256・権限の照合は行っていません。",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        });
        Grid.SetRow(head, 0);
        layout.Children.Add(head);

        var table = new DataGrid
        {
            ItemsSource = rows,
            IsReadOnly = true,
            CanUserAddRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
            Foreground = Brushes.WhiteSmoke,
            RowBackground = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
            BorderThickness = new Thickness(0),
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "状態（未検証）", Binding = new Binding(nameof(DuplicateCleanupHistory.Row.State)),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "元のファイルパス", Binding = new Binding(nameof(DuplicateCleanupHistory.Row.OriginalPath)),
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "保留先", Binding = new Binding(nameof(DuplicateCleanupHistory.Row.StagedPath)),
            Width = new DataGridLength(3, DataGridLengthUnitType.Star)
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "操作ID", Binding = new Binding(nameof(DuplicateCleanupHistory.Row.OperationId)),
            Width = new DataGridLength(180)
        });
        table.Columns.Add(new DataGridTextColumn
        {
            Header = "番号", Binding = new Binding(nameof(DuplicateCleanupHistory.Row.CandidateIndex)),
            Width = new DataGridLength(55)
        });
        Grid.SetRow(table, 1);
        layout.Children.Add(table);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        var close = new Button
        {
            Content = "閉じる",
            ToolTip = "この画面は参照専用です。ファイルは変更されません。",
            Padding = new Thickness(16, 8, 16, 8),
            Background = new SolidColorBrush(Color.FromRgb(49, 49, 49))
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var note = new TextBlock
        {
            Text = "ごみ箱移動の記録 ≠ ごみ箱に存在する証明。削除・復元・確認済みへの変更操作はこの画面にありません。",
            Foreground = new SolidColorBrush(Color.FromRgb(222, 178, 111)),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 11
        };
        footer.Children.Add(note);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;
    }
}
