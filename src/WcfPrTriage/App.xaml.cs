using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace WcfPrTriage;

public partial class App : Application
{
    // Session-scoped names (a stable GUID keeps them unique to this app). The mutex flags "an
    // instance is running"; the event is how a second launch asks the first to surface its window.
    private const string MutexName = "WcfPrTriage_SingleInstance_9E7B2C41-5A6D-4F3E-9C1A-7B2D3E4F5A6B";
    private const string ActivateEventName = "WcfPrTriage_Activate_9E7B2C41-5A6D-4F3E-9C1A-7B2D3E4F5A6B";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _activateListener;
    private volatile bool _shuttingDown;
    private Services.ThemeManager? _themeManager;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep the mutex handle open for the whole process lifetime (the field reference does that).
        _singleInstanceMutex = new Mutex(initiallyOwned: false, MutexName, out bool isPrimary);

        if (!isPrimary)
        {
            // Another copy already owns this app — ask it to come to the front, then exit quietly.
            SignalExistingInstance();
            Shutdown();
            return;
        }

        // We are the primary instance: listen for future launches that want us surfaced, then show
        // the window ourselves (StartupUri was removed so a secondary instance never builds a window).
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _activateListener = new Thread(ActivateListenerLoop)
        {
            IsBackground = true,
            Name = "SingleInstanceActivateListener",
        };
        _activateListener.Start();

        base.OnStartup(e);

        // Colour the shared brush resources for the current OS theme before the window renders, so
        // there's no light-to-dark flash on show; the manager then follows OS theme changes live.
        _themeManager = new Services.ThemeManager(this);
        _themeManager.Initialize(e.Args);

        var window = new MainWindow();
        MainWindow = window;
        _themeManager.AttachWindow(window);
        window.Show();
    }

    /// <summary>Opens the already-running instance's activation event and sets it.</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
            {
                existing.Set();
                existing.Dispose();
            }
        }
        catch { /* best-effort — if we can't signal, we still exit so only one window remains */ }
    }

    /// <summary>Background loop: whenever a second launch signals us, surface the main window.</summary>
    private void ActivateListenerLoop()
    {
        var evt = _activateEvent;
        if (evt is null)
            return;

        while (!_shuttingDown)
        {
            try
            {
                if (!evt.WaitOne())
                    return;
            }
            catch (ObjectDisposedException) { return; }
            catch (AbandonedMutexException) { }

            if (_shuttingDown)
                return;

            try { Dispatcher.BeginInvoke(new Action(BringMainWindowToFront)); }
            catch (Exception) { /* dispatcher may be shutting down */ }
        }
    }

    /// <summary>Restores (if minimized) and force-activates the main window above other apps.</summary>
    private void BringMainWindowToFront()
    {
        if (MainWindow is not { } window)
            return;

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Show();
        window.Activate();

        // Windows blocks a background process from stealing focus; a brief Topmost toggle reliably
        // pops the window to the top without leaving it permanently pinned.
        bool wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = wasTopmost;
        window.Activate();
        window.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        _themeManager?.Shutdown();
        try { _activateEvent?.Set(); } catch { /* wake the listener so it can exit */ }
        _activateEvent?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "Unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
