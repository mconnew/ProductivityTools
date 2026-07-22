using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace WcfPrTriage.ViewModels;

/// <summary>
/// Minimal INotifyPropertyChanged base for the view models.
///
/// Change notifications are always delivered on the WPF UI (Dispatcher) thread. When a property is
/// mutated from a background thread — e.g. the PR status scan or a triage running on the thread pool
/// after <c>ConfigureAwait(false)</c> — the notification is marshaled to the Dispatcher at
/// <see cref="DispatcherPriority.Background"/> instead of being raised on the worker thread.
///
/// This matters for responsiveness: if a data-bound property is changed off the UI thread, WPF's own
/// binding engine marshals the update at a priority that competes with (and can starve) scroll
/// <see cref="DispatcherPriority.Input"/> and <see cref="DispatcherPriority.Render"/>. A burst of such
/// updates from the background scan/triage would freeze panes (e.g. the PR list scrollbar) until the
/// network calls finished. By posting notifications ourselves at Background priority, user input and
/// rendering take precedence and the UI stays interactive while updates stream in.
///
/// Notifications raised on the UI thread are delivered synchronously (no added latency for
/// user-driven changes such as selection).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;

        var args = new PropertyChangedEventArgs(name);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            handler(this, args);
        else
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => handler(this, args)));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
