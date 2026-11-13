using System.Threading;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Non-blocking OS-wide, per-logon-session lease for ALL destructive operations.
/// Never hold it across await: Windows mutex ownership belongs to one thread.
/// </summary>
public sealed class MutationLease : IDisposable
{
    private const string Name = @"Local\ChappyStorageCompressor.FileMutations";
    private readonly Mutex _mutex;
    private bool _owns;

    private MutationLease(Mutex mutex)
    {
        _mutex = mutex;
        _owns = true;
    }

    public static MutationLease Acquire()
    {
        var mutex = new Mutex(false, Name);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException)
            {
                // A crashed peer may have left a half-completed transaction.
                // We own the abandoned mutex now, but do not automatically
                // allow mutation until the user explicitly inspects recovery.
                mutex.ReleaseMutex();
                throw new InvalidOperationException(
                    "別プロセスが統合/復旧中に終了した可能性があります。復旧記録を確認してください。");
            }
            if (!acquired)
                throw new InvalidOperationException(
                    "別のChappy Storage Compressorが統合・復旧を実行中です。処理は開始しません。");
            return new MutationLease(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (!_owns) return;
        _owns = false;
        try { _mutex.ReleaseMutex(); }
        finally { _mutex.Dispose(); }
    }
}
