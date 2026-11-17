using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ChappyStorageCompressor.Core;

namespace ChappyStorageCompressor;

public partial class RecoveryWindow : Window
{
    private readonly ObservableCollection<RecoveryCase> _cases = new();
    private readonly ObservableCollection<RecoveryEvidenceOverview.Row> _history = new();
    private readonly ObservableCollection<CollisionEvidenceReview.Row> _collision = new();
    private readonly List<CollisionEvidenceReview.Row> _allCollisionRows = new();
    private readonly string? _selectedRoot;
    private CollisionEvidenceReview.Summary? _lastCollisionEvidence;
    private bool _busy;
    // Deliberately FALSE in every installed product. The routed WPF CI
    // explicitly opts in for generated TEMP+sentinel fixtures by reflection.
    // This does NOT authorize actual personal media. Core independently
    // rejects roots outside its generated-only test boundary.
    private bool _allowGeneratedOnlyUndoTest;
    // Only the generated-fixture WPF flow owns this token. In normal product
    // mode UndoExecute stays disabled; cancellation never enables mutations.
    private CancellationTokenSource? _undoWork;
    // Private CI observation point after the SAME durable Core checkpoint.
    // Not an authority for replay, release gating, or user file operations.
    private Action<int>? _afterGeneratedUndoAliasForTest;
    private Func<string, bool> _confirmGeneratedOnlyUndo = message =>
        MessageBox.Show(message, "生成データのみ・独立Undoの最終確認",
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    private Action<string> _showGeneratedOnlyUndo = message =>
        MessageBox.Show(message, "生成データの独立Undo結果",
            MessageBoxButton.OK, MessageBoxImage.Information);
    private bool SyntheticUndoSelectionAllowed()
    {
        if (!_allowGeneratedOnlyUndoTest || _busy ||
            RecoveryTabs?.SelectedIndex != 1 ||
            HistoryGrid?.SelectedItem is not RecoveryEvidenceOverview.Row row ||
            row.Source != "複数ファイルのグループ履歴" ||
            // ExplicitResumeRequired is flagged as needing attention in the
            // read-only list, but can be explicitly resumed after fresh Core
            // evidence checks. ManualReview is never selectable here.
            row.State is not
                ("VerifiedCompleted" or "ExplicitResumeRequired") ||
            string.IsNullOrWhiteSpace(_selectedRoot))
            return false;
        try
        {
            string root = Safety.ValidateRoot(_selectedRoot);
            string temp = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.GetTempPath()));
            return root.StartsWith(temp + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                root.Contains("ChappyStorageSmoke_",
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(root, ".chappy-part23-generated-only")) &&
                File.ReadAllText(Path.Combine(root,
                    ".chappy-part23-generated-only")) == "Part23 synthetic data only";
        }
        catch { return false; }
    }

    public RecoveryWindow(string? selectedRoot = null)
    {
        _selectedRoot = selectedRoot;
        InitializeComponent();
        RecoveryGrid.ItemsSource = _cases;
        HistoryGrid.ItemsSource = _history;
        CollisionGrid.ItemsSource = _collision;
        Loaded += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        RecoveryGrid.IsEnabled = !busy;
        HistoryGrid.IsEnabled = !busy;
        CollisionGrid.IsEnabled = !busy;
        CollisionPriorityFilter.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && RecoveryTabs.SelectedIndex == 0 &&
            RecoveryGrid.SelectedItem is RecoveryCase c && c.CanApply;
        // G3 is review-only: no live Apply from completed group history.
        UndoReviewButton.IsEnabled = !busy &&
            RecoveryTabs.SelectedIndex == 1 &&
            HistoryGrid.SelectedItem is RecoveryEvidenceOverview.Row h &&
            h.Source == "複数ファイルのグループ履歴";
        UndoExecuteButton.IsEnabled = !busy && SyntheticUndoSelectionAllowed();
        UndoCancelButton.IsEnabled = busy &&
            _undoWork is { IsCancellationRequested: false };
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        Busy(true);
        _cases.Clear();
        _history.Clear();
        _collision.Clear();
        _allCollisionRows.Clear();
        _lastCollisionEvidence = null;
        DetailText.Text = "ファイル内容・実体の確認を実行しています。大量の動画がある場合は読み込みに時間がかかります。";
        SummaryText.Text = "読み取り専用の復旧状態検証中…";
        HistorySummaryText.Text = "統合済み・Undo履歴を読み取り専用で検証中…";
        CollisionSummaryText.Text = "同名ファイルの保存先と記録を読み取り専用で検証中…";
        try
        {
            // Use background thread: hashing multiple large MP4s must not freeze WPF.
            var result = await Task.Run(() =>
            {
                var pending = RecoveryService.InspectAll();
                RecoveryEvidenceOverview.Summary? history = null;
                CollisionEvidenceReview.Summary? collision = null;
                string? historyError = null, collisionError = null;
                if (string.IsNullOrWhiteSpace(_selectedRoot))
                {
                    historyError = "対象フォルダが未指定です。統合・Undo履歴は未検証。";
                    collisionError = "対象フォルダが未指定です。同名ファイル履歴は未検証。";
                }
                else
                {
                    try { history = RecoveryEvidenceOverview.Inspect(_selectedRoot, pending); }
                    catch (Exception ex)
                    {
                        historyError = "対象フォルダの履歴検証失敗（未検証）: " + ex.Message;
                    }
                    try { collision = CollisionEvidenceReview.Inspect(_selectedRoot); }
                    catch (Exception ex)
                    {
                        collisionError = "同名ファイル証拠の検証失敗（未検証）: " + ex.Message;
                    }
                }
                return (Pending: pending, History: history, HistoryError: historyError,
                    Collision: collision, CollisionError: collisionError);
            });
            var inspected = result.Pending;
            foreach (var item in inspected) _cases.Add(item);
            if (result.History is { } history)
            {
                foreach (var item in history.Rows) _history.Add(item);
                HistorySummaryText.Text =
                    $"対象フォルダ履歴 {history.Rows.Count:N0} 件 ／ 要確認・未完了 {history.RequiresAttention:N0} 件。" +
                    " 履歴タブは読み取り専用。本番統合・独立Undoは無効。";
            }
            else
                HistorySummaryText.Text = result.HistoryError ??
                    "統合・Undo履歴の検証に失敗しました。手動確認が必要です。";
            if (result.Collision is { } collision)
            {
                var priorities = CollisionRecoveryChecklist.Prioritize(collision);
                _lastCollisionEvidence = priorities.Evidence;
                _allCollisionRows.AddRange(priorities.Rows);
                RefreshCollisionPriorityFilter();
                CollisionPriorityCounts.Text = priorities.Describe();
                CollisionSummaryText.Text = collision.Describe() + "\n" +
                    priorities.Describe() + "\n" +
                    CollisionRecoveryChecklist.Analyze(collision).Describe();
            }
            else
            {
                var priorities = CollisionRecoveryChecklist.Prioritize(null);
                CollisionPriorityCounts.Text = priorities.Describe();
                CollisionSummaryText.Text = result.CollisionError ??
                    "同名ファイル履歴の照合に失敗しました。手動確認が必要です。";
                CollisionSummaryText.Text += "\n" +
                    priorities.Describe() + "\n" +
                    CollisionRecoveryChecklist.Analyze(null).Describe();
            }
            int safe = inspected.Count(x => x.CanApply);
            int review = inspected.Count - safe;
            SummaryText.Text = inspected.Count == 0
                ? "旧形式の未解決操作記録はありません。統合・Undo履歴の判定は別タブで確認してください。本番統合は無効です。"
                : $"旧形式の未解決 {inspected.Count:N0} 件 ／ 条件付き復旧可能 {safe:N0} 件 ／ 手動調査 {review:N0} 件。" +
                  "  未解決の記録が残る間は、新しい統合を開始できません。";
            DetailText.Text = inspected.Count == 0 ? "問題は検出されませんでした。" :
                "行を選択して検証理由を確認してください。操作は選択した1件にのみ実行されます。";
        }
        catch (Exception ex)
        {
            SummaryText.Text = "復旧状態の読み込みに失敗しました。";
            HistorySummaryText.Text = "履歴は未検証です。ファイルは変更していません。";
            CollisionSummaryText.Text = "同名ファイル履歴も未検証。データは変更していません。";
            CollisionPriorityCounts.Text = CollisionRecoveryChecklist.Prioritize(null).Describe();
            DetailText.Text = ex.ToString();
        }
        finally { Busy(false); }
    }

    private void CollisionPriorityFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CollisionGrid is null) return;
        RefreshCollisionPriorityFilter();
    }

    private void RefreshCollisionPriorityFilter()
    {
        if (CollisionPriorityFilter is null) return;
        string selected = (CollisionPriorityFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "ALL";
        _collision.Clear();
        foreach (var row in _allCollisionRows)
        {
            if (selected == "ALL" || row.PriorityLabel.StartsWith(selected + " ",
                    StringComparison.Ordinal))
                _collision.Add(row);
        }
        // Filtering only changes displayed rows. It never changes the
        // immutable evidence or permissions in the legacy recovery tab.
        if (ApplyButton != null && RecoveryTabs.SelectedIndex != 0)
            ApplyButton.IsEnabled = false;
    }

    private void RecoverySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecoveryGrid.SelectedItem is not RecoveryCase c)
        {
            if (ApplyButton != null) ApplyButton.IsEnabled = false;
            return;
        }
        if (ApplyButton != null)
            ApplyButton.IsEnabled = !_busy && RecoveryTabs.SelectedIndex == 0 && c.CanApply;
        if (DetailText != null)
            DetailText.Text = $"判定：{c.ActionText}\n\n{c.Explanation}\n\n" +
                $"元の保存場所：{c.OriginalPath}\n\n退避ファイル：{c.BackupPath}\n\n" +
                $"専用ストア：{c.MasterPath}\n\n" +
                "注意：複数のファイルが同じ実体を参照するハードリンクは、編集で内容が連動します。";
    }

    private void RecoveryTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ApplyButton != null && RecoveryGrid != null && RecoveryTabs != null)
            ApplyButton.IsEnabled = !_busy && RecoveryTabs.SelectedIndex == 0 &&
                RecoveryGrid.SelectedItem is RecoveryCase c && c.CanApply;
        if (UndoReviewButton != null && HistoryGrid != null && RecoveryTabs != null)
            UndoReviewButton.IsEnabled = !_busy && RecoveryTabs.SelectedIndex == 1 &&
                HistoryGrid.SelectedItem is RecoveryEvidenceOverview.Row h &&
                h.Source == "複数ファイルのグループ履歴";
        if (UndoExecuteButton != null)
            UndoExecuteButton.IsEnabled = SyntheticUndoSelectionAllowed();
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UndoReviewButton != null && HistoryGrid != null && RecoveryTabs != null)
            UndoReviewButton.IsEnabled = !_busy && RecoveryTabs.SelectedIndex == 1 &&
                HistoryGrid.SelectedItem is RecoveryEvidenceOverview.Row selected &&
                selected.Source == "複数ファイルのグループ履歴";
        if (UndoExecuteButton != null)
            UndoExecuteButton.IsEnabled = SyntheticUndoSelectionAllowed();
        if (HistoryGrid?.SelectedItem is RecoveryEvidenceOverview.Row h && DetailText != null)
            DetailText.Text = $"証拠の種類：{h.Source}\n\n判定：{h.AttentionText} / {h.State}\n\n" +
                $"対象：{h.Target}\n\n{h.Explanation}\n\n" +
                "このタブには復元・修復・削除ボタンはありません。実データは一切変更されません。";
    }

    private void CollisionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CollisionGrid?.SelectedItem is CollisionEvidenceReview.Row x &&
            DetailText != null)
            DetailText.Text = $"【同名ファイルの証拠：読み取り専用】\n\n" +
                $"種類：{x.Kind}\n判定：{x.AttentionText} / {x.State}\n\n" +
                $"操作ID：{x.RecordId}\n元の相対パス：{x.OriginalRelativePath}\n" +
                $"保存先の相対パス：{x.DestinationRelativePath}\n\n" +
                x.Explanation + "\n\n【確認するときの注意】\n" +
                x.ReviewGuidance +
                "\n\n【調査の優先度と次の確認】\n" +
                x.PriorityLabel + "：" + x.PriorityGuidance +
                "\n\n" +
                CollisionRecoveryChecklist.Analyze(_lastCollisionEvidence, x)
                    .DetailText() +
                "\n\nこの画面では再開・削除・名前変更・自動修復はできません。";
    }

    // G3-01: a deliberately NON-actionable WPF handoff to the persisted
    // completed group. This does not take a prior ScanResult, does not write
    // a checkpoint and never calls IndependentUndoEngine.Restore.
    private async void UndoReview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RecoveryTabs.SelectedIndex != 1 ||
            HistoryGrid.SelectedItem is not RecoveryEvidenceOverview.Row row ||
            row.Source != "複数ファイルのグループ履歴")
            return;
        string operationId = row.Target;
        Busy(true);
        DetailText.Text = "永続グループ記録から全元ファイル・空き容量を読み取り専用で再検証中…";
        try
        {
            if (string.IsNullOrWhiteSpace(_selectedRoot))
                throw new InvalidOperationException("対象フォルダが指定されていません");
            var candidates = await Task.Run(() =>
                CompletedGroupLedger.InspectUndoCandidates(_selectedRoot));
            var candidate = candidates.SingleOrDefault(x =>
                x.OperationId.Equals(operationId, StringComparison.OrdinalIgnoreCase));
            // Never mistake a stale WPF row for a recovery authorization.
            if (candidate is null || !candidate.ReviewReady)
            {
                DetailText.Text = candidate?.Describe() ??
                    "未検証または復元済みの履歴です。元ファイルを安全なUndo候補として再特定できません。" +
                    " 復旧記録を保全し、手動調査してください。";
                return;
            }
            DetailText.Text = candidate.Describe() +
                "\n\n【再起動後の永続記録から再特定済み。独立Undoは未実行】";
        }
        catch (Exception ex)
        {
            DetailText.Text = "Undo候補の安全な再特定に失敗（ファイルは変更していません）。" +
                "\n" + ex.Message;
        }
        finally { Busy(false); }
    }

    // G3-07: routed WPF click uses the single normal-compiled Core transaction
    // in a generated TEMP+sentinel fixture only. In normal product builds the
    // default-off private test opt-in and Core root guard both reject writes.
    // Cancellation is cooperative: the durable group checkpoint is retained
    // for explicit reinspection/resume, never auto-deleted or auto-approved.
    private async void UndoExecute_Click(object sender, RoutedEventArgs e)
    {
        if (!SyntheticUndoSelectionAllowed() ||
            HistoryGrid.SelectedItem is not RecoveryEvidenceOverview.Row selected ||
            _selectedRoot is null)
            return;
        string operationId = selected.Target;
        string root = _selectedRoot;
        Busy(true); // include async preflight/consent; reject double clicks
        try
        {
            var first = await Task.Run(() =>
                CompletedGroupLedger.InspectUndoCandidates(root)
                    .SingleOrDefault(x => x.OperationId == operationId));
            if (first is null || !first.ReviewReady)
            {
                DetailText.Text = "元ファイルまたは完了履歴が未検証。手動調査が必要です（未実行）。";
                return;
            }
            if (!_confirmGeneratedOnlyUndo(
                    first.Describe() + "\n\n生成した使い捨てNTFSテストデータだけの独立Undoを実行しますか？"))
            {
                DetailText.Text = "最終確認を辞退。独立Undoは未実行。";
                return;
            }
            var again = await Task.Run(() =>
                CompletedGroupLedger.InspectUndoCandidates(root)
                    .SingleOrDefault(x => x.OperationId == operationId));
            if (!_allowGeneratedOnlyUndoTest ||
                RecoveryTabs.SelectedIndex != 1 ||
                !ReferenceEquals(HistoryGrid.SelectedItem, selected) ||
                again is null || !again.ReviewReady ||
                first.RemainingAliases != again.RemainingAliases ||
                first.RepresentativePath != again.RepresentativePath ||
                !first.RemainingSharedPaths.SequenceEqual(
                    again.RemainingSharedPaths, StringComparer.OrdinalIgnoreCase) ||
                first.OriginalFiles.Count != again.OriginalFiles.Count ||
                first.OriginalFiles.Where((file, i) =>
                    !file.Path.Equals(again.OriginalFiles[i].Path,
                        StringComparison.OrdinalIgnoreCase) ||
                    file.OriginalVolumeSerial != again.OriginalFiles[i].OriginalVolumeSerial ||
                    file.OriginalFileIndex != again.OriginalFiles[i].OriginalFileIndex).Any())
                throw new InvalidDataException(
                    "同意後にUndo対象・元ID・安全条件が変わりました。元データを変更せず中止");
            string evidence = Path.Combine(root, "g3-wpf-undo-evidence");
            if (!Directory.Exists(evidence))
                throw new InvalidDataException("検証用Undo証拠フォルダがありません");

            _undoWork = new CancellationTokenSource();
            CancellationToken token = _undoWork.Token;
            long freeBefore = CapacityGuard.ReadAvailable(root).TargetBytes;
            Busy(true); // only an active generated Core operation can be canceled
            DetailText.Text = "生成データの独立Undoを検証・処理中。中断した場合は証拠を保全します…";
            int restored = await Task.Run(() =>
                CompletedGroupLedger.ResumeUndoCore(root, operationId,
                    evidence, token,
                    afterDurableAlias: count =>
                    {
                        Dispatcher.InvokeAsync(() =>
                            DetailText.Text = "元ファイルを独立化・検証済み: " +
                                count + "件。中断時の再開情報は永続保存済み");
                        _afterGeneratedUndoAliasForTest?.Invoke(count);
                    }), token);

            // Do not call a successful user-facing result based on the Core
            // return value alone: reopen the durable group and all original
            // evidence; measured free space is not a savings guarantee.
            var state = CompletedGroupLedger.Inspect(root).Entries.Single(x =>
                x.OperationId == operationId);
            var originals = CompletedGroupLedger.InspectOriginals(root)
                .Where(x => x.OperationId == operationId &&
                    x.State == "VerifiedRestored").ToArray();
            var currentIds = originals.Select(x =>
            {
                var current = NativeFile.GetIdentity(x.Path);
                return current.VolumeSerial.ToString("X8") + ":" +
                    current.FileIndex.ToString("X16");
            }).ToArray();
            if (state.State != "VerifiedRestored" ||
                originals.Length != first.OriginalFiles.Count ||
                currentIds.Distinct(StringComparer.Ordinal).Count() != originals.Length)
                throw new InvalidDataException(
                    "Undo後の全元ファイルID・永続証拠を再認証できません。手動調査が必要");
            long freeAfter = CapacityGuard.ReadAvailable(root).TargetBytes;
            long delta = freeAfter - freeBefore;
            string deltaText = delta >= 0 ? "+" + GroupRow.HumanSize(delta) :
                "-" + GroupRow.HumanSize(-delta);
            string ids = string.Join("\n", originals.Select((x, i) =>
                Path.GetFileName(x.Path) + " / 現NTFS ID " + currentIds[i]));
            _showGeneratedOnlyUndo(
                "生成データだけの独立Undoが完了：独立化した別名 " +
                restored + "件。\n全元ファイルの別NTFS ID・SHA・Owner/DACLを再照合済み。\n" +
                "対象ドライブ空き容量: " + GroupRow.HumanSize(freeBefore) +
                " → " + GroupRow.HumanSize(freeAfter) +
                "（観測差 " + deltaText + "）。\n" +
                "※ 容量差は周辺I/Oの影響を受けるためUndo専用の割当量ではありません。\n" +
                ids + "\n通常ユーザーのデータ変更機能は引き続き無効。");
            EndGeneratedUndoRun();
            await RefreshAsync();
            HistorySummaryText.Text += "\n【生成テストUndo成功】独立化 " +
                restored + "件。全元ファイルの独立NTFS ID・SHA/権限を再照合済み。" +
                " 空き容量の観測差 " + deltaText + "（専用回復量ではない）。";
        }
        catch (OperationCanceledException)
        {
            EndGeneratedUndoRun();
            await RefreshAsync();
            DetailText.Text = "【生成テストUndo中断】操作 " + operationId +
                "\n処理中のキャンセルを受け付けました。すでに永続化された独立化は巻き戻しません。" +
                "\n次回は履歴を再検証し、ExplicitResumeRequiredの場合だけ明示的に再開できます。" +
                "\n復旧記録とstageを勝手に削除・移動しないでください。";
            HistorySummaryText.Text +=
                "\n【生成テストUndo中断】確定済みの個別ファイルを保全。手動再検証が必要。";
        }
        catch (Exception ex)
        {
            EndGeneratedUndoRun();
            await RefreshAsync();
            DetailText.Text = "【Undo停止：手動調査が必要】" + operationId +
                "\n" + ex.Message +
                "\n未確認のstage・ファイル・復旧証拠は移動・削除しないでください。" +
                "\n同名ファイル調査タブのP0/P1と統合・Undo履歴も確認してください。";
            HistorySummaryText.Text +=
                "\n【G3検証用Undo停止】復旧記録は保全。手動調査・再照合が必要。";
        }
        finally { EndGeneratedUndoRun(); }
    }

    private void UndoCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy || _undoWork is null || _undoWork.IsCancellationRequested)
            return;
        var work = _undoWork;
        UndoCancelButton.IsEnabled = false;
        DetailText.Text = "中断要求中…安全なチェックポイントで停止し、履歴を保全します。";
        work.Cancel();
    }

    private void EndGeneratedUndoRun()
    {
        _undoWork?.Dispose();
        _undoWork = null;
        if (_busy) Busy(false);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RecoveryTabs.SelectedIndex != 0 ||
            RecoveryGrid.SelectedItem is not RecoveryCase item || !item.CanApply) return;
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
