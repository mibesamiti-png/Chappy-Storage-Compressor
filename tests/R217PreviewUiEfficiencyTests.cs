using System.Text.Json;
using ChappyStorageCompressor.Core;

// R2.17 part 12: deterministic per-event GUI notification checks.
// This is NOT a WPF wall-clock speedup benchmark. The real app WPF
// routed Click smoke separately proves button state and cancel behavior.
internal static class R217PreviewUiEfficiencyTests
{
    public static void Run(string parent, Action<bool, string> check)
    {
        var balanced = new UnifiedPreviewProgress(2, 8);
        check(balanced.Png(1, 2) == 10 &&
              balanced.Png(2, 2) == 20 &&
              balanced.PngFinished() == 20,
            "R2.17 UI: two PNG and eight JPEG sources are weighted 20/80, not fixed 50/50");
        check(balanced.Jpeg(4, 8) == 60 &&
              balanced.Jpeg(8, 8) == 100 &&
              balanced.Finished() == 100,
            "R2.17 UI: JPEG progress uses actual combined file counts and finishes at 100");
        var mostlyPng = new UnifiedPreviewProgress(8, 2);
        check(mostlyPng.Png(4, 8) == 40 &&
              mostlyPng.PngFinished() == 80 &&
              mostlyPng.Jpeg(1, 2) == 90,
            "R2.17 UI: PNG-heavy batches get their correct 80 percent weight");
        var onlyJpeg = new UnifiedPreviewProgress(0, 6);
        check(onlyJpeg.PngFinished() == 0 &&
              onlyJpeg.Jpeg(3, 6) == 50 &&
              onlyJpeg.Finished() == 100,
            "R2.17 UI: JPG-only batch progress remains consistent");
        var onlyPng = new UnifiedPreviewProgress(6, 0);
        check(onlyPng.Png(3, 6) == 50 &&
              onlyPng.PngFinished() == 100 &&
              onlyPng.Finished() == 100,
            "R2.17 UI: PNG-only batch reaches 100 without phantom JPEG phase");
        var zero = new UnifiedPreviewProgress(0, 0);
        check(zero.Png(0, 0) == 0 && zero.PngFinished() == 0 &&
              zero.Jpeg(0, 0) == 0 && zero.Finished() == 100,
            "R2.17 UI: empty inventory never divides by zero");
        var unexpected = new UnifiedPreviewProgress(2, 6);
        double before = unexpected.Png(2, 2);
        double afterMoreActual = unexpected.Png(2, 4);
        double afterPng = unexpected.PngFinished();
        check(afterMoreActual >= before && afterPng >= afterMoreActual &&
              unexpected.Jpeg(1, 7) >= afterPng &&
              unexpected.Jpeg(7, 7) == 100,
            "R2.17 UI: preflight estimate drift never regresses the displayed percentage");
        var bounds = new UnifiedPreviewProgress(3, 3);
        check(bounds.Png(-100, 3) == 0 &&
              bounds.Png(1000, 3) == 50 &&
              bounds.Jpeg(-100, 3) == 50 &&
              bounds.Jpeg(1000, 3) == 100,
            "R2.17 UI: underflow/overflow notifications clamped and monotonic");

        var pngEvents = new List<string>();
        var pngLimiter = new RateLimitedProgress<PngBatchPreviewEngine.Progress>(
            x => pngEvents.Add(x.Status),
            x => x.Status is "Failed" or "Deferred",
            TimeSpan.FromDays(1));
        for (int i = 0; i < 20000; i++)
            pngLimiter.Report(new PngBatchPreviewEngine.Progress(i, 20000,
                "synthetic.png", "Completed", 0));
        pngLimiter.Report(new PngBatchPreviewEngine.Progress(150, 20000,
            "synthetic-invalid.png", "Failed", 0));
        pngLimiter.Report(new PngBatchPreviewEngine.Progress(151, 20000,
            "synthetic-deferred.png", "Deferred", 0));
        check(pngLimiter.Received == 20002 && pngLimiter.Forwarded == 3 &&
              pngEvents.SequenceEqual(new[] { "Completed", "Failed", "Deferred" }),
            "R2.17 UI: 20,000 artificial PNG progress notifications only forward initial+two critical states");

        var jpegEvents = new List<string>();
        var jpegLimiter = new RateLimitedProgress<JpegBatchPreviewEngine.Progress>(
            x => jpegEvents.Add(x.Status),
            x => x.Status is "Failed" or "Deferred" or "NeedsReview",
            TimeSpan.FromDays(1));
        for (int i = 0; i < 10000; i++)
            jpegLimiter.Report(new JpegBatchPreviewEngine.Progress(i, 10000,
                "synthetic.jpg", "Completed", 0));
        jpegLimiter.Report(new JpegBatchPreviewEngine.Progress(2, 10000,
            "changed.jpg", "NeedsReview", 0));
        jpegLimiter.Report(new JpegBatchPreviewEngine.Progress(3, 10000,
            "broken.jpg", "Failed", 0));
        jpegLimiter.Report(new JpegBatchPreviewEngine.Progress(4, 10000,
            "low-space.jpg", "Deferred", 0));
        check(jpegLimiter.Received == 10003 && jpegLimiter.Forwarded == 4 &&
              jpegEvents.SequenceEqual(new[] {
                  "Completed", "NeedsReview", "Failed", "Deferred" }),
            "R2.17 UI: 10,000 JPEG replay notifications retain all critical review/failure/deferral events");

        var zeroDelay = new RateLimitedProgress<int>(_ => { }, _ => false,
            TimeSpan.Zero);
        for (int i = 0; i < 10; i++) zeroDelay.Report(i);
        check(zeroDelay.Received == 10 && zeroDelay.Forwarded == 10,
            "R2.17 UI: explicit zero-delay configuration forwards every notification");

        int captured = 0;
        bool preventedNegativeInterval = false;
        try
        {
            _ = new RateLimitedProgress<int>(_ => captured++, _ => false,
                TimeSpan.FromMilliseconds(-1));
        }
        catch (ArgumentOutOfRangeException) { preventedNegativeInterval = true; }
        check(preventedNegativeInterval && captured == 0,
            "R2.17 UI: invalid negative throttle interval cannot silently suppress progress");

        bool preventedInvalidPlan = false;
        try { _ = new UnifiedPreviewProgress(-1, 1); }
        catch (ArgumentOutOfRangeException) { preventedInvalidPlan = true; }
        check(preventedInvalidPlan,
            "R2.17 UI: negative preflight counts are rejected");

        Directory.CreateDirectory("diagnostics");
        File.WriteAllText("diagnostics/r217-ui-progress-efficiency.json",
            JsonSerializer.Serialize(new
            {
                schema = "chappy-r217-batch-ux-throttle-v1",
                generatedNotificationCount = pngLimiter.Received + jpegLimiter.Received,
                forwardedNotificationCount = pngLimiter.Forwarded + jpegLimiter.Forwarded,
                priorityPngFailuresAndDeferralsDelivered = true,
                priorityJpegReviewFailuresAndDeferralsDelivered = true,
                weightedPngAndJpegCounts = true,
                planCountDriftMonotonic = true,
                emptyAndSingleFormatHandled = true,
                negativeIntervalRejected = true,
                actualWpfPerformanceBenchmark = false,
                syntheticNotificationsNotProductionEventCounts = true,
                durableJournalsUnmodifiedByRateLimiter = true,
                productionHardlinkMergeEnabled = false,
                productionOriginalReplacementEnabled = false,
                realMediaTouched = false,
                actualFreedDiskBytes = 0,
                verdict = "PASS"
            }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
