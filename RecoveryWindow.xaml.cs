using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ChappyStorageCompressor.Core;

namespace ChappyStorageCompressor;

public partial class RecoveryWindow : Window
{
    private readonly ObservableCollection<RecoveryCase> _cases = new();
    private bool _busy;

    public RecoveryWindow()
    {
        InitializeComponent();
        RecoveryGrid.ItemsSource = _cases;
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        RecoveryGrid.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && RecoveryGrid.SelectedItem is RecoveryCase c && c.CanApply;
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        Busy(true);
        _cases.Clear();
        DetailText.Text = "ファイル内容・実体の確認を実行しています。大量の動画がある場合は読み込みに時間がかかります。";
        SummaryText.Text = "読み取り専用の復旧状態検証中…";
        try
        {
            // Use background thread: hashing multiple large MP4s must not freeze WPF.
            var inspected = await Task.Run(() => RecoveryService.InspectAll());
            foreach (var item in inspected) _cases.Add(item);
            int safe = inspected.Count(x => x.CanApply);
            int review = inspected.Count - safe;
            SummaryText.Text = inspected.Count == 0
                ? "未解決の操作はありません。新しい重複統合を実行できます。"
                : $"未解決 {inspected.Count:N0} 件 ／ 条件付き復旧可能 {safe:N0} 件 ／ 手動調査 {review:N0} 件。" +
                  "  未解決の記録が残る間は、新しい統合を開始できません。";
            DetailText.Text = inspected.Count == 0 ? "問題は検出されませんでした。" :
                "行を選択して検証理由を確認してください。操作は選択した1件にのみ実行されます。";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "復旧状態の読み込みに失敗しました。";
            DetailText.Text = ex.ToString();
        }
        finally { Busy(false); }
    }

    private void RecoverySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecoveryGrid.SelectedItem is not RecoveryCase c)
        {
            if (ApplyButton != null) ApplyButton.IsEnabled = false;
            return;
        }
        if (ApplyButton != null) ApplyButton.IsEnabled = !_busy && c.CanApply;
        if (DetailText != null)
            DetailText.Text = $"判定：{c.ActionText}\n\n{c.Explanation}\n\n" +
                $"元の保存場所：{c.OriginalPath}\n\n退避ファイル：{c.BackupPath}\n\n" +
                $"専用ストア：{c.MasterPath}\n\n" +
                "注意：複数のファイルが同じ実体を参照するハードリンクは、編集で内容が連動します。";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RecoveryGrid.SelectedItem is not RecoveryCase item || !item.CanApply) return;
        string action = item.Action switch
        {
            RecoveryAction.RestoreIndependentBackup => "独立した退避ファイルを元の名前へ戻します。",
            RecoveryAction.UndoNewHardLink => "共有ハードリンク1件を解除して、残していた独立ファイルを元の場所へ戻します。\nストアにある代表リンクは削除しません。",
            RecoveryAction.ClearVerifiedJournal => "ユーザーデータを変更せず、検証済みの復旧記録だけ削除します。",
            _ => "安全な復旧操作ではありません。"
        };
        if (MessageBox.Show(this,
            $"対象：{item.OriginalPath}\n\n{action}\n\n" +
            "実行直前に再検査し、状態が変わっていれば中止します。\n" +
            "本番データでの利用はまだ未検証です。テスト用コピーだけで実行してください。\n\n実行しますか？",
            "R1.3 復旧操作の最終確認", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) != MessageBoxResult.Yes) return;

        Busy(true);
        try
        {
            var message = await Task.Run(() => RecoveryService.Apply(item.OperationId, item.Action, item.OriginalPath, item.MasterPath));
            MessageBox.Show(this, message, "復旧操作終了", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "復旧操作を中断しました。記録と退避ファイルは再確認が必要です。\n" + ex.Message,
                "安全のため処理を停止", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Busy(false);
            await RefreshAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
