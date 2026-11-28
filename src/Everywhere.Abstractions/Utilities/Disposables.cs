namespace Everywhere.Utilities;

/// <summary>
/// Provides a set of static methods for creating <see cref="IDisposable"/> objects.
/// </summary>
public static class Disposable
{
    /// <summary>
    /// A disposable that invokes a specified action when disposed.
    /// </summary>
    private sealed class AnonymousDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose()
        {
            var dispose = Interlocked.Exchange(ref _dispose, null);
            dispose?.Invoke();
        }
    }

    /// <summary>
    /// A disposable that invokes a specified action when disposed, with a state parameter.
    /// </summary>
    /// <param name="state"></param>
    /// <param name="dispose"></param>
    /// <typeparam name="TState"></typeparam>
    private sealed class AnonymousDisposable<TState>(TState state, Action<TState> dispose) : IDisposable
    {
        private TState _state = state;
        private Action<TState>? _dispose = dispose;

        public void Dispose()
        {
            var dispose = Interlocked.Exchange(ref _dispose, null);
            if (dispose is null)
            {
                return;
            }

            var state = _state;
            _state = default!;
            dispose(state);
        }
    }

    /// <summary>
    /// Represents a disposable that does nothing on disposal.
    /// </summary>
    private sealed class EmptyDisposable : IDisposable
    {
        /// <summary>
        /// Singleton default disposable.
        /// </summary>
        public static readonly EmptyDisposable Instance = new();

        private EmptyDisposable()
        {
        }

        /// <summary>
        /// Does nothing.
        /// </summary>
        public void Dispose()
        {
            // no op
        }
    }

    /// <summary>
    /// Gets the disposable that does nothing when disposed.
    /// </summary>
    public static IDisposable Empty => EmptyDisposable.Instance;

    /// <summary>
    /// Creates a disposable object that invokes the specified action when disposed.
    /// </summary>
    /// <param name="dispose">Action to run during the first call to <see cref="IDisposable.Dispose"/>. The action is guaranteed to be run at most once.</param>
    /// <returns>The disposable object that runs the given action upon disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dispose"/> is <c>null</c>.</exception>
    public static IDisposable Create(Action dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        return new AnonymousDisposable(dispose);
    }

    /// <summary>
    /// Creates a disposable object that invokes the specified action when disposed.
    /// </summary>
    /// <param name="state">The state to be passed to the action.</param>
    /// <param name="dispose">Action to run during the first call to <see cref="IDisposable.Dispose"/>. The action is guaranteed to be run at most once.</param>
    /// <returns>The disposable object that runs the given action upon disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dispose"/> is <c>null</c>.</exception>
    public static IDisposable Create<TState>(TState state, Action<TState> dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        return new AnonymousDisposable<TState>(state, dispose);
    }
}

/// <summary>
/// Represents an asynchronous disposable resource that executes a specified asynchronous action when disposed.
/// </summary>
public static class AsyncDisposable
{
    /// <summary>
    /// Creates an asynchronous disposable object that invokes the specified asynchronous action when disposed.
    /// </summary>
    /// <param name="dispose"></param>
    private sealed class AnonymousAsyncDisposable(Func<ValueTask> dispose) : IAsyncDisposable
    {
        private Func<ValueTask>? _dispose = dispose;

        public ValueTask DisposeAsync()
        {
            var dispose = Interlocked.Exchange(ref _dispose, null);
            return dispose?.Invoke() ?? default;
        }
    }

    /// <summary>
    /// Creates an asynchronous disposable object that invokes the specified asynchronous action when disposed, with a state parameter.
    /// </summary>
    /// <param name="state"></param>
    /// <param name="dispose"></param>
    /// <typeparam name="TState"></typeparam>
    private sealed class AnonymousAsyncDisposable<TState>(TState state, Func<TState, ValueTask> dispose) : IAsyncDisposable
    {
        private TState _state = state;
        private Func<TState, ValueTask>? _dispose = dispose;

        public ValueTask DisposeAsync()
        {
            var dispose = Interlocked.Exchange(ref _dispose, null);
            if (dispose is null)
            {
                return default;
            }

            var state = _state;
            _state = default!;
            return dispose(state);
        }
    }

    private sealed class EmptyAsyncDisposable : IAsyncDisposable
    {
        public static readonly EmptyAsyncDisposable Instance = new();

        private EmptyAsyncDisposable()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>
    /// Gets the asynchronous disposable that does nothing when disposed.
    /// </summary>
    public static IAsyncDisposable Empty => EmptyAsyncDisposable.Instance;

    /// <summary>
    /// Creates an asynchronous disposable object that invokes the specified asynchronous action when disposed.
    /// </summary>
    /// <param name="dispose">Asynchronous action to run during the first call to <see cref="IAsyncDisposable.DisposeAsync"/>. The action is guaranteed to be run at most once.</param>
    /// <returns>The asynchronous disposable object that runs the given action upon disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dispose"/> is <c>null</c>.</exception>
    public static IAsyncDisposable Create(Func<ValueTask> dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        return new AnonymousAsyncDisposable(dispose);
    }

    /// <summary>
    /// Creates an asynchronous disposable object that invokes the specified asynchronous action when disposed, with a state parameter.
    /// </summary>
    /// <param name="state">The state to pass to the asynchronous action.</param>
    /// <param name="dispose">Asynchronous action to run during the first call to <see cref="IAsyncDisposable.DisposeAsync"/>. The action is guaranteed to be run at most once.</param>
    /// <returns>The asynchronous disposable object that runs the given action upon disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="dispose"/> is <c>null</c>.</exception>
    public static IAsyncDisposable Create<TState>(TState state, Func<TState, ValueTask> dispose)
    {
        ArgumentNullException.ThrowIfNull(dispose);
        return new AnonymousAsyncDisposable<TState>(state, dispose);
    }
}