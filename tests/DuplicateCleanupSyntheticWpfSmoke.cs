using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using ChappyStorageCompressor;
using ChappyStorageCompressor.Core;

/// <summary>
/// Runs the SAME review/history WPF source as the shipped app, but builds it
/// into the standalone Core smoke EXE under CHAPPY_TESTING. Four extra
/// generated-only controls are compiled out of the production WPF assembly.
/// Real NTFS mutation is performed by the EXISTING Core transaction, never
/// by an ad-hoc second engine or a production user-file adapter.
/// </summary>
internal static class DuplicateCleanupSyntheticWpfSmoke
{
    internal static void Run(string testParent, Action<bool, string> check)
    {
        Exception? thrown = null;
        var worker = new Thread(() =>
        {
            try { RunSta(testParent, check); }
            catch (Exception ex) { thrown = ex; }
        })
        {
            IsBackground = true,
            Name = "Generated-only duplicate cleanup WPF routed transaction"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(75)))
            throw new TimeoutException(
                "Disposable WPF smoke timed out; NOT an acceptance PASS");
        if (thrown is not null)
            throw new InvalidOperationException(
                "Generated-only WPF Core integration failed", thrown);
    }

    private static void RunSta(string testParent, Action<bool, string> check)
    {
        // Unlike an Application.Run message loop, this standalone console
        // test has no SynchronizationContext by default. An async WPF click
        // must always resume on its owning STA dispatcher, never a worker.
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string parent = Path.Combine(testParent,
            "ChappyStorageSmoke_DuplicateWpf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, ".chappy-part23-generated-only"),
            "Part23 synthetic data only");
        string scanRoot = Path.Combine(parent, "ScanGeneratedOnly");
        string[] dirs = { "Keeper", "ReviewA", "ReviewB" };
        foreach (string dir in dirs) Directory.CreateDirectory(
            Path.Combine(scanRoot, dir));
        string keeper = Path.Combine(scanRoot, "Keeper", "generated.bin");
        string first = Path.Combine(scanRoot, "ReviewA", "generated-a.txt");
        string second = Path.Combine(scanRoot, "ReviewB", "generated-b.dat");
        byte[] bytes = RandomNumberGenerator.GetBytes(64 * 1024);
        foreach (string path in new[] { keeper, first, second })
            File.WriteAllBytes(path, bytes);
        foreach (string path in new[] { first, second })
        {
            File.SetCreationTimeUtc(path, File.GetCreationTimeUtc(keeper));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(keeper));
        }
        string originalHash = Safety.ComputeSha256(keeper, CancellationToken.None);
        var keeperBefore = Safety.Capture(keeper);
        var scan = Scanner.Run(scanRoot, false, null, CancellationToken.None);
        var group = scan.Groups.Single();
        var plan = DuplicateCleanupPreview.Inspect(scanRoot, group, keeper);
        check(plan.DeleteCandidates.Count == 2 &&
              !DuplicateCleanupWorkflow.ProductionMutationEnabled &&
              !DuplicateCleanupReviewStore.CanMoveOrDeleteFiles,
            "WPF-C: generated-only trio and unchanged product deletion lock");

        var original = plan.DeleteCandidates.Select(c =>
            Safety.Capture(c.OriginalPath)).ToArray();
        DuplicateCleanupReviewWindow? window = null;
        try
        {
            window = new DuplicateCleanupReviewWindow(plan, group);
            bool rejectedUnowned = false;
            try { window.ArmGeneratedOnlyForTests(Path.GetTempPath()); }
            catch (Exception ex) when (ex is InvalidOperationException or
                InvalidDataException or ArgumentException)
            {
                rejectedUnowned = true;
            }
            check(rejectedUnowned && !Directory.Exists(
                  Path.Combine(scanRoot, "同名ファイル")),
                "WPF-C: test-only UI refuses an unowned root before any rename");
            window.ArmGeneratedOnlyForTests(parent);
            var review = window;
            var type = typeof(DuplicateCleanupReviewWindow);
            T Field<T>(string name) where T : class =>
                type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)?
                    .GetValue(review) as T ??
                throw new InvalidOperationException("Generated UI field missing: " + name);
            var preflight = Field<Button>("_preflightButton");
            var stage = Field<Button>("_syntheticStageButton");
            var restore = Field<Button>("_syntheticRestoreButton");
            var confirm = Field<Button>("_syntheticConfirmButton");
            var recycle = Field<Button>("_syntheticRecycleButton");
            var productDelete = Field<Button>("_deleteButton");
            var summary = Field<TextBlock>("_summary");
            var checkBoxes = Field<System.Collections.Generic.List<(string Path, CheckBox Box)>>("_choices");
            check(checkBoxes.Count == 2 && !stage.IsEnabled &&
                  !restore.IsEnabled && !confirm.IsEnabled &&
                  !recycle.IsEnabled && !productDelete.IsEnabled,
                "WPF-C: all generated mutation buttons initially gated and shipped delete disabled");

            review.Show();
            // The first candidate is explicitly ticked in the REAL WPF view.
            checkBoxes[0].Box.IsChecked = true;
            preflight.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, preflight));
            PumpUntil(() => preflight.IsEnabled &&
                summary.Text.Contains("直前再照合合格", StringComparison.Ordinal) &&
                stage.IsEnabled, TimeSpan.FromSeconds(25));
            check(!productDelete.IsEnabled && !restore.IsEnabled &&
                  !confirm.IsEnabled && !recycle.IsEnabled &&
                  File.Exists(plan.DeleteCandidates[0].OriginalPath) &&
                  File.Exists(plan.DeleteCandidates[1].OriginalPath),
                "WPF-C: routed preflight gates stage without any source mutation");

            stage.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, stage));
            string id = review.GeneratedOnlyOperationIdForTests ??
                throw new InvalidOperationException("WPF stage did not persist an operation ID");
            var states = DuplicateCleanupWorkflow.InspectSynthetic(parent, scanRoot, id);
            check(states.Count == 2 &&
                  states.All(x => x.State == "StagedUnconfirmed") &&
                  File.Exists(Path.Combine(scanRoot, plan.ReportRelativePath)) &&
                  plan.DeleteCandidates.All(c => !File.Exists(c.OriginalPath) &&
                      File.Exists(Path.Combine(scanRoot, c.ProposedCollectionRelativePath))) &&
                  !stage.IsEnabled && restore.IsEnabled &&
                  !confirm.IsEnabled && !recycle.IsEnabled &&
                  !productDelete.IsEnabled &&
                  Safety.SameSnapshot(keeperBefore),
                "WPF-C: routed Stage moves exactly two generated files and writes top-level memo");

            restore.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, restore));
            states = DuplicateCleanupWorkflow.InspectSynthetic(parent, scanRoot, id);
            check(states[1].State == "RestoredOriginal" &&
                  NativeFile.GetIdentity(plan.DeleteCandidates[1].OriginalPath)
                    .SameFile(original[1].Identity) &&
                  !restore.IsEnabled && confirm.IsEnabled &&
                  !recycle.IsEnabled && !productDelete.IsEnabled,
                "WPF-C: routed Restore returns the unselected file to its exact original NTFS ID");

            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, confirm));
            states = DuplicateCleanupWorkflow.InspectSynthetic(parent, scanRoot, id);
            check(states[0].State == "ConfirmedStaged" &&
                  states[0].Confirmed && !confirm.IsEnabled &&
                  recycle.IsEnabled && !productDelete.IsEnabled,
                "WPF-C: routed Confirm persists ONLY the checked candidate for test-only recycle");

            recycle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, recycle));
            states = DuplicateCleanupWorkflow.InspectSynthetic(parent, scanRoot, id);
            string fakeBin = Path.Combine(parent, ".chappy-test-recycle",
                id + "-0000.bin");
            check(states[0].State == "SimulatedRecycleReceiptRequiresReview" &&
                  states[1].State == "RestoredOriginal" &&
                  File.Exists(fakeBin) &&
                  NativeFile.GetIdentity(fakeBin).SameFile(original[0].Identity) &&
                  Safety.ComputeSha256(fakeBin, CancellationToken.None) ==
                    originalHash &&
                  Safety.ComputeSha256(plan.DeleteCandidates[1].OriginalPath,
                    CancellationToken.None) == originalHash &&
                  Safety.SameSnapshot(keeperBefore) &&
                  !recycle.IsEnabled && !productDelete.IsEnabled,
                "WPF-C: routed fake recycle preserves exact identities and leaves keeper intact");

            var history = DuplicateCleanupHistory.Inspect(scanRoot);
            var historyWindow = new DuplicateCleanupHistoryWindow(scanRoot, history);
            check(history.Count == 2 && history.Any(x => x.State.Contains(
                  "元パス復帰の記録あり", StringComparison.Ordinal)) &&
                  history.Any(x => x.State.Contains(
                  "ごみ箱相当の記録あり", StringComparison.Ordinal)) &&
                  historyWindow.Title.Contains("読み取り専用",
                    StringComparison.Ordinal) &&
                  !DuplicateCleanupHistory.CanModifyUserFiles,
                "WPF-C: existing product read-only history shows restore and fake recycle separately");
            historyWindow.Show();
            historyWindow.Close();
            check(!Directory.Exists(Path.Combine(scanRoot, "some-foreign-folder")) &&
                  !DuplicateCleanupWorkflow.ProductionMutationEnabled,
                "WPF-C: synthetic workflow cannot authorize production file changes");
        }
        finally
        {
            window?.Close();
        }
    }

    private static void PumpUntil(Func<bool> test, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        bool timedOut = false;
        timer.Tick += (_, _) =>
        {
            if (test()) frame.Continue = false;
            else if (clock.Elapsed > timeout)
            {
                timedOut = true;
                frame.Continue = false;
            }
        };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (timedOut || !test())
            throw new TimeoutException("Generated WPF review did not finish independent preflight");
    }
}
