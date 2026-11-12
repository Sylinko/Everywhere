using Avalonia.Controls;
using Avalonia.Threading;
using Everywhere.Views;
using ShadUI;

namespace Everywhere.Common.Notification;

partial class NotificationCenter
{
    /// <summary>
    /// Occurs when an eligible application window becomes active. Remote sources may refresh here;
    /// the center does not own network access or a polling timer.
    /// </summary>
    public event EventHandler? PopupWindowActivated;

    private readonly List<PopupEntry> _popups = [];
    private readonly List<WindowRegistration> _popupWindows = [];
    private readonly HashSet<string> _sessionAcknowledgments = [];
    private PopupPresentation? _popupPresentation;
    private bool _arePopupsDisposed;

    /// <summary>
    /// Registers a local View and waits for explicit acknowledgment. Hiding or closing a window
    /// only releases that presentation; cancellation withdraws the request without acknowledging it.
    /// </summary>
    public async Task<bool> ShowPopupAsync(
        string source,
        PopupNotification notification,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var added = AddPopup(source, notification);
            SchedulePopup();
            return added;
        });
        if (entry is null) return false;

        await using var cancellationRegistration = cancellationToken.Register(() => Dispatcher.UIThread.PostOnDemand(() =>
        {
            // Acknowledgment and cancellation settle the same entry on the UI thread. A later
            // cancellation must not change an acknowledgment already delivered to the caller.
            if (!entry.Completion.TrySetCanceled(cancellationToken)) return;
            RemovePopup(entry);
            SchedulePopup();
        }));
        return await entry.Completion.Task;
    }

    /// <summary>
    /// Reconciles a complete remote source snapshot. Missing entries are withdrawn, not acknowledged.
    /// Existing entries retain their presentation and local control state across refreshes.
    /// </summary>
    public void ReplacePopups(string source, IReadOnlyList<PopupNotification> notifications)
    {
        Dispatcher.UIThread.VerifyAccess();
        var ids = notifications.Select(notification => notification.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in _popups.Where(entry => entry.Source == source && !ids.Contains(entry.Notification.Id)).ToArray())
        {
            RemovePopup(entry);
        }

        foreach (var notification in notifications)
        {
            var existing = _popups.Find(entry => entry.Source == source && entry.Notification.Id == notification.Id);
            if (existing is not null)
            {
                // Keep the latest descriptor even while its View displays an older reading snapshot.
                // The next presentation applies it without replacing the View or local control state.
                existing.Notification = notification;
            }
            else
            {
                AddPopup(source, notification);
            }
        }

        SchedulePopup();
    }

    /// <summary>
    /// Registers an actual window after its DialogHost is attached. Dispose on View unload.
    /// All registration and presentation state belongs exclusively to the UI thread.
    /// </summary>
    public IDisposable RegisterPopupWindow(Window window, NotificationWindows role)
    {
        Dispatcher.UIThread.VerifyAccess();
        var registration = new WindowRegistration(this, window, role);
        _popupWindows.Add(registration);
        WindowChanged(registration);
        return registration;
    }

    private PopupEntry? AddPopup(string source, PopupNotification notification)
    {
        if (_arePopupsDisposed) return null;
        var key = $"NotificationCenter:Popup:{EscapeStorageSegment(source)}:{EscapeStorageSegment(notification.Id)}";
        if (_sessionAcknowledgments.Contains(key) ||
            notification.Repeat == NotificationRepeat.Once && _keyValueStorage.Contains(key) ||
            notification.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return null;
        }

        var existing = _popups.Find(entry => entry.Key == key);
        if (existing is not null) return existing;

        var entry = new PopupEntry(source, key, notification);
        _popups.Add(entry);
        return entry;
    }

    private void SchedulePopup()
    {
        // Run after Loaded/activation handlers so host registration and business dialogs settle first.
        Dispatcher.UIThread.Post(TryShowPopup, DispatcherPriority.Background);
    }

    private void TryShowPopup()
    {
        if (_arePopupsDisposed || _popupPresentation is not null) return;
        foreach (var entry in _popups.ToArray())
        {
            if (entry.Notification.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                RemovePopup(entry);
                continue;
            }

            var owner = _popupWindows.Find(registration => registration.CanPresent && (registration.Role & entry.Notification.Windows) != 0);
            if (owner?.Host is not { HasOpenedDialog: false } host) continue;

            entry.View ??= new PopupNotificationView(entry.Notification);
            entry.View.DataContext = entry.Notification;
            var presentation = new PopupPresentation(entry, owner, host, entry.View);
            _popupPresentation = presentation;
            entry.View.Acknowledged += HandlePopupAcknowledged;
            ObservePresentationAsync(presentation).Detach(Serilog.Log.ForContext<NotificationCenter>().ToExceptionHandler());
            return;
        }
    }

    private async Task ObservePresentationAsync(PopupPresentation presentation)
    {
        // The registered window owns cancellation through Close(view), not an initialization token.
        await presentation.Host.CreateCustomDialog(presentation.View).WithMaxWidth(560).ShowAsync(CancellationToken.None);
        if (!ReferenceEquals(_popupPresentation, presentation)) return;

        ReleasePresentation(shouldDetachView: false);
        // A global dialog close is not user acknowledgment; the announcement remains pending.
    }

    private void HandlePopupAcknowledged(object? sender, EventArgs e)
    {
        if (_popupPresentation is not { } presentation) return;

        var entry = presentation.Entry;
        _sessionAcknowledgments.Add(entry.Key);
        if (entry.Notification.Repeat == NotificationRepeat.Once) _keyValueStorage.Set(entry.Key, true);
        RemovePopup(entry, isAcknowledged: true);
        SchedulePopup();
    }

    private void RemovePopup(PopupEntry entry, bool isAcknowledged = false)
    {
        _popups.Remove(entry);
        if (ReferenceEquals(_popupPresentation?.Entry, entry)) ReleasePresentation(shouldDetachView: false);
        entry.Completion.TrySetResult(isAcknowledged);
    }

    private void ReleasePresentation(bool shouldDetachView)
    {
        if (_popupPresentation is not { } presentation) return;

        _popupPresentation = null;
        presentation.View.Acknowledged -= HandlePopupAcknowledged;
        presentation.Host.Close(presentation.View);
        // Normal closure keeps the host's exit animation. Hidden/unloaded owners must release
        // the old parent immediately so another window can reuse the same View.
        if (shouldDetachView && ReferenceEquals(presentation.Host.Dialog, presentation.View))
        {
            presentation.Host.Dialog = null;
        }
    }

    private void WindowChanged(WindowRegistration registration)
    {
        if (_popupPresentation?.Owner == registration && !registration.IsVisible) ReleasePresentation(shouldDetachView: true);
        if (registration.CanPresent) PopupWindowActivated?.Invoke(this, EventArgs.Empty);
        SchedulePopup();
    }

    private void DisposePopups()
    {
        _arePopupsDisposed = true;
        ReleasePresentation(shouldDetachView: false);
        foreach (var registration in _popupWindows.ToArray()) registration.Dispose();
        foreach (var entry in _popups) entry.Completion.TrySetResult(false);
        _popups.Clear();
    }

    private sealed class PopupEntry(string source, string key, PopupNotification notification)
    {
        public string Source { get; } = source;
        public string Key { get; } = key;
        public PopupNotification Notification { get; set; } = notification;
        public PopupNotificationView? View { get; set; }
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PopupPresentation(PopupEntry Entry, WindowRegistration Owner, DialogHost Host, PopupNotificationView View);

    private sealed class WindowRegistration : IDisposable
    {
        private Window Window { get; }
        public NotificationWindows Role { get; }
        public DialogHost? Host { get; }
        public bool IsVisible => Window.IsVisible && Window.WindowState != WindowState.Minimized;
        public bool CanPresent => IsVisible && Window.IsActive;

        private readonly NotificationCenter _center;

        public WindowRegistration(NotificationCenter center, Window window, NotificationWindows role)
        {
            _center = center;
            Window = window;
            Role = role;
            Host = DialogManager.ResolveHost(window);
            window.PropertyChanged += HandlePropertyChanged;
            if (Host is not null) Host.PropertyChanged += HandlePropertyChanged;
        }

        private void HandlePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty || e.Property == WindowBase.IsActiveProperty)
            {
                _center.WindowChanged(this);
            }
            else if (e.Property == DialogHost.HasOpenedDialogProperty && Host?.HasOpenedDialog == false)
            {
                _center.SchedulePopup();
            }
        }

        public void Dispose()
        {
            Window.PropertyChanged -= HandlePropertyChanged;
            if (Host is not null) Host.PropertyChanged -= HandlePropertyChanged;
            _center._popupWindows.Remove(this);
            if (_center._popupPresentation?.Owner == this) _center.ReleasePresentation(shouldDetachView: true);
            _center.SchedulePopup();
        }
    }
}