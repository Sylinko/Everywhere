namespace Everywhere.Interop;

/// <summary>
/// Shares overlay hit testing and registration lifetime between native input implementations.
/// Watch creation, updates and disposal belong to the UI thread; native callbacks may use another thread.
/// </summary>
public abstract class OverlayInputWatcherBase : IOverlayInputWatcher
{
    /// <inheritdoc />
    public abstract bool IsSupported { get; }

    /// <inheritdoc />
    public PixelPoint? PointerPosition
    {
        get
        {
            using var scope = _syncLock.EnterScope();
            return _pointerPosition;
        }
    }

    private readonly Lock _syncLock = new();
    private readonly List<Registration> _registrations = [];
    private PixelPoint? _pointerPosition;

    /// <inheritdoc />
    public IOverlayInputWatch Watch(PixelRect bounds, Action onDismiss, Action<PixelPoint> onPointerMoved)
    {
        var registration = new Registration(this, bounds, onDismiss, onPointerMoved);
        bool shouldStart;
        using (_syncLock.EnterScope())
        {
            _registrations.Add(registration);
            shouldStart = _registrations.Count == 1;
            if (_pointerPosition is { } point) onPointerMoved(point);
        }

        if (shouldStart)
        {
            try
            {
                StartWatching();
            }
            catch
            {
                registration.Dispose();
                throw;
            }
        }

        return registration;
    }

    /// <summary>
    /// Installs native observation for the first active registration, including the initial pointer position.
    /// </summary>
    protected abstract void StartWatching();

    /// <summary>
    /// Removes native observation after the last registration ends, including partially initialized resources.
    /// </summary>
    protected abstract void StopWatching();

    /// <summary>
    /// Publishes a native pointer change. No allocation or UI work is performed on the input path.
    /// </summary>
    protected void UpdatePointerPosition(PixelPoint point)
    {
        using var scope = _syncLock.EnterScope();
        if (_registrations.Count == 0 || _pointerPosition == point) return;

        _pointerPosition = point;
        foreach (var registration in _registrations)
        {
            registration.NotifyPointerMoved(point);
        }
    }

    /// <summary>
    /// Dismisses registrations whose toolbar and menu do not contain a native button press.
    /// </summary>
    protected void HandlePointerPressed(PixelPoint point)
    {
        using var scope = _syncLock.EnterScope();
        foreach (var registration in _registrations)
        {
            if (!registration.Contains(point)) registration.Dismiss();
        }
    }

    /// <summary>
    /// Allows scrolling inside a menu while dismissing overlays affected by external scrolling.
    /// </summary>
    protected void HandlePointerScrolled(PixelPoint point)
    {
        using var scope = _syncLock.EnterScope();
        foreach (var registration in _registrations)
        {
            if (registration.MenuBounds?.Contains(point) != true) registration.Dismiss();
        }
    }

    /// <summary>
    /// Handles a non-modifier, non-synthetic key press, preserving navigation within an open menu.
    /// </summary>
    protected void HandleKeyPressed(bool isMenuNavigation)
    {
        using var scope = _syncLock.EnterScope();
        foreach (var registration in _registrations)
        {
            if (!isMenuNavigation || registration.MenuBounds is null) registration.Dismiss();
        }
    }

    private void Remove(Registration registration)
    {
        using (_syncLock.EnterScope())
        {
            if (!_registrations.Remove(registration) || _registrations.Count > 0) return;
            _pointerPosition = null;
        }

        // Native hook shutdown may join a callback thread. Never hold its registration lock here.
        StopWatching();
    }

    private sealed class Registration(
        OverlayInputWatcherBase owner,
        PixelRect bounds,
        Action onDismiss,
        Action<PixelPoint> onPointerMoved
    ) : IOverlayInputWatch
    {
        public PixelRect? MenuBounds { get; private set; }

        private PixelRect _bounds = bounds;
        private bool _isCompleted;
        private bool _isDisposed;

        public bool Contains(PixelPoint point) => _bounds.Contains(point) || MenuBounds?.Contains(point) == true;

        public void Update(PixelRect bounds, PixelRect? menuBounds = null)
        {
            using var scope = owner._syncLock.EnterScope();
            if (_isDisposed) return;

            _bounds = bounds;
            MenuBounds = menuBounds;
            _isCompleted = false;
        }

        public void NotifyPointerMoved(PixelPoint point)
        {
            if (!_isDisposed && !_isCompleted) onPointerMoved(point);
        }

        public void Dismiss()
        {
            if (_isCompleted || _isDisposed) return;

            // Native delivery holds the owner's lock, making callback/disposal ordering explicit.
            _isCompleted = true;
            onDismiss();
        }

        public void Dispose()
        {
            using (owner._syncLock.EnterScope())
            {
                if (_isDisposed) return;
                _isDisposed = true;
            }

            owner.Remove(this);
        }
    }
}