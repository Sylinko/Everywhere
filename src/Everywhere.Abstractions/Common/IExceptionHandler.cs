namespace Everywhere.Common;

public interface IExceptionHandler
{
    void HandleException(
        Exception exception,
        string? message = null,
        [CallerMemberName] object? source = null,
        [CallerLineNumber] int lineNumber = 0);

    static IExceptionHandler DangerouslyIgnoreAllException { get; } = new AnonymousExceptionHandler(static delegate { });
}

/// <summary>
/// Generic interface for exception handlers that holds a type parameter.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IExceptionHandler<out T> : IExceptionHandler;

public readonly struct AnonymousExceptionHandler(Action<Exception, string?, object?, int> handler) : IExceptionHandler
{
    public void HandleException(
        Exception exception,
        string? message = null,
        [CallerMemberName] object? source = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        handler.Invoke(exception, message, source, lineNumber);
    }
}

public readonly struct AnonymousExceptionHandler<T>(Action<Exception, string?, object?, int> handler) : IExceptionHandler<T>
{
    public void HandleException(
        Exception exception,
        string? message = null,
        [CallerMemberName] object? source = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        handler.Invoke(exception, message, $"{typeof(T).Name}.{source}", lineNumber);
    }
}

public readonly ref struct AnonymousExceptionHandlerSlim(Action<Exception, string?, object?, int> handler) : IExceptionHandler
{
    public void HandleException(
        Exception exception,
        string? message = null,
        [CallerMemberName] object? source = null,
        [CallerLineNumber] int lineNumber = 0)
    {
        handler.Invoke(exception, message, source, lineNumber);
    }
}