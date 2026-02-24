using System.Windows;
using FastFileExplorer.Services;

namespace FastFileExplorer;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateListenerThread;
    private volatile bool _activateListenerStopRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        var runInBackground = e.Args.Any(arg => string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase));
        var runInTestMode = e.Args.Any(arg => string.Equals(arg, "--test-mode", StringComparison.OrdinalIgnoreCase));
        var disableTray = runInTestMode || e.Args.Any(arg => string.Equals(arg, "--no-tray", StringComparison.OrdinalIgnoreCase));
        var cacheOverride = GetOptionValue(e.Args, "--cache=");
        var rootsOverride = e.Args
            .Where(arg => arg.StartsWith("--root=", StringComparison.OrdinalIgnoreCase))
            .Select(arg => arg.Substring("--root=".Length).Trim().Trim('"'))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        if (!runInBackground && !runInTestMode)
        {
            if (!TryAcquireSingleInstance())
            {
                Shutdown();
                return;
            }
        }

        base.OnStartup(e);

        if (runInBackground)
        {
            var settings = SettingsService.Load();
            try
            {
                var cachePath = string.IsNullOrWhiteSpace(settings.CachePath)
                    ? SettingsService.GetDefaultCachePath()
                    : settings.CachePath;
                SettingsService.MigrateLegacyCacheIfNeeded(cachePath);

                // Keep startup background process lightweight to avoid large memory usage.
                // It now only validates that a usable cache exists and then exits.
                var store = new SqliteIndexStore(cachePath);
                store.EnsureInitialized();
                var metadata = store.TryReadMetadata();
                if (metadata is null || metadata.ItemCount == 0)
                {
                    // No cache yet; foreground app will build index on first open.
                }
            }
            catch
            {
                // Background run should never crash the app.
            }
            finally
            {
                Shutdown();
            }

            return;
        }

        MainWindow = new MainWindow(
            roots: rootsOverride.Length > 0 ? rootsOverride : null,
            cachePath: cacheOverride,
            disableTrayIcon: disableTray);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateListenerStopRequested = true;
        try
        {
            _activateEvent?.Set();
        }
        catch
        {
            // Ignore activation signal failures.
        }

        try
        {
            if (_activateListenerThread is not null && _activateListenerThread.IsAlive)
            {
                _activateListenerThread.Join(500);
            }
        }
        catch
        {
            // Ignore listener shutdown failures.
        }

        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch
        {
            // Ignore mutex release failures.
        }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
            _activateEvent?.Dispose();
            _activateEvent = null;
        }

        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _activateEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                "Local\\FastFileExplorer.Activate");

            _instanceMutex = new Mutex(initiallyOwned: true, "Local\\FastFileExplorer.SingleInstance", out var createdNew);
            if (!createdNew)
            {
                _activateEvent.Set();
                return false;
            }

            StartActivationListener();
            return createdNew;
        }
        catch
        {
            return true;
        }
    }

    private void StartActivationListener()
    {
        if (_activateEvent is null)
        {
            return;
        }

        _activateListenerThread = new Thread(() =>
        {
            while (!_activateListenerStopRequested)
            {
                try
                {
                    _activateEvent.WaitOne();
                }
                catch
                {
                    return;
                }

                if (_activateListenerStopRequested)
                {
                    return;
                }

                _ = Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (Current.MainWindow is MainWindow mainWindow)
                        {
                            if (mainWindow.IsLoaded)
                            {
                                mainWindow.ShowFromExternalActivation();
                                return;
                            }
                        }

                        var recreatedWindow = new MainWindow();
                        Current.MainWindow = recreatedWindow;
                        recreatedWindow.Show();
                    }
                    catch
                    {
                        // Ignore activation failures and keep process alive.
                    }
                });
            }
        })
        {
            IsBackground = true,
            Name = "FastFileExplorer.ActivationListener"
        };
        _activateListenerThread.Start();
    }

    private static string? GetOptionValue(string[] args, string optionPrefix)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(optionPrefix, StringComparison.OrdinalIgnoreCase));
        if (arg is null)
        {
            return null;
        }

        var raw = arg.Substring(optionPrefix.Length).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim('"');
    }
}
