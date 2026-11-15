using System.Diagnostics;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// A read-only UI projection of the independent PNG and JPEG batch journals.
/// This never controls candidate generation, retry logic, or file replacement.
/// It weights overall progress by the number of files, not by a fixed 50/50
/// split, and never moves the UI percentage backwards if plan counts differ
/// slightly from the initial approximate preflight inventory.
/// </summary>
public sealed class UnifiedPreviewProgress
{
    private readonly int plannedPng;
    private readonly int plannedJpeg;
    private double lastPercent;

    public UnifiedPreviewProgress(int plannedPng, int plannedJpeg)
    {
        if (plannedPng < 0 || plannedJpeg < 0)
            throw new ArgumentOutOfRangeException(nameof(plannedPng),
                "Preview source counts must not be negative.");
        this.plannedPng = plannedPng;
        this.plannedJpeg = plannedJpeg;
    }

    public double LastPercent => lastPercent;

    public double Png(int done, int total)
    {
        long pngCount = Math.Max(plannedPng, Math.Max(0, total));
        long jpegCount = plannedJpeg;
        long denominator = pngCount + jpegCount;
        double value = denominator == 0 ? 0 :
            100.0 * Math.Clamp((long)done, 0, pngCount) / denominator;
        return Remember(value);
    }

    /// <summary>The PNG queue has completed; the JPEG queue has not started.</summary>
    public double PngFinished()
    {
        long sum = (long)plannedPng + plannedJpeg;
        return Remember(sum == 0 ? 0 : 100.0 * plannedPng / sum);
    }

    public double Jpeg(int done, int total)
    {
        long jpegCount = Math.Max(plannedJpeg, Math.Max(0, total));
        long denominator = (long)plannedPng + jpegCount;
        double value = denominator == 0 ? 0 :
            100.0 * (plannedPng + Math.Clamp((long)done, 0, jpegCount)) / denominator;
        return Remember(value);
    }

    public double Finished()
    {
        lastPercent = 100;
        return lastPercent;
    }

    private double Remember(double value)
    {
        lastPercent = Math.Max(lastPercent, Math.Clamp(value, 0, 100));
        return lastPercent;
    }
}

/// <summary>
/// Rate-limit only informational progress notifications before scheduling WPF
/// dispatcher work. Errors, review, deferral and terminal updates are priority
/// events and must always pass. The engine's durable per-file journals and
/// cancellation are entirely unaffected.
/// </summary>
public sealed class RateLimitedProgress<T> : IProgress<T>
{
    private readonly object sync = new();
    private readonly Action<T> receiver;
    private readonly Func<T, bool> priority;
    private readonly long intervalTicks;
    private long lastSent;
    private bool initialized;
    private long received;
    private long forwarded;

    public RateLimitedProgress(Action<T> receiver, Func<T, bool> priority,
        TimeSpan minimumInterval)
    {
        this.receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        this.priority = priority ?? throw new ArgumentNullException(nameof(priority));
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        intervalTicks = checked((long)(minimumInterval.TotalSeconds * Stopwatch.Frequency));
    }

    public long Received => Interlocked.Read(ref received);
    public long Forwarded => Interlocked.Read(ref forwarded);

    public void Report(T value)
    {
        Interlocked.Increment(ref received);
        bool send;
        lock (sync)
        {
            long now = Stopwatch.GetTimestamp();
            send = !initialized || priority(value) || now - lastSent >= intervalTicks;
            if (send)
            {
                initialized = true;
                lastSent = now;
                Interlocked.Increment(ref forwarded);
            }
        }
        if (send) receiver(value);
    }
}
