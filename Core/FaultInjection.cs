namespace ChappyStorageCompressor.Core;

/// <summary>
/// Synthetic power-loss checkpoints. Enabled only in the test assembly by
/// CHAPPY_TESTING; release builds compile every Hit() to an empty method.
/// </summary>
internal static class FaultInjection
{
#if CHAPPY_TESTING
    internal static Action<string>? OnCheckpoint { get; set; }
#endif

    internal static void Hit(string checkpoint)
    {
#if CHAPPY_TESTING
        OnCheckpoint?.Invoke(checkpoint);
#endif
    }
}

#if CHAPPY_TESTING
/// <summary>Tests use this exception to simulate abrupt termination without
/// executing normal transaction rollback. Never compiled into release builds.</summary>
internal sealed class SimulatedPowerLossException : Exception
{
    internal SimulatedPowerLossException(string checkpoint) : base(checkpoint) { }
}
#endif
