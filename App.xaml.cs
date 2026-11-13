using System;
using System.Threading;
using System.Windows;

namespace ChappyStorageCompressor;

public partial class App : Application
{
    // Session-local named mutex prevents a second copy of the desktop application
    // from starting a competing file-mutation job. Core mutations are separately
    // protected by MutationLease, even when called without the WPF application.
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            _instanceMutex = new Mutex(true, @"Local\ChappyStorageCompressor.SingleInstance", out bool createdNew);
            _ownsMutex = createdNew;
            if (!createdNew)
            {
                MessageBox.Show("Chappy Storage Compressor は既に起動しています。二重起動を防止しました。",
                    "既に起動中", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("二重起動保護を設定できないため、安全のため起動しません。\n" + ex.Message,
                "起動を中止", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
