using Everywhere.Common;

namespace Everywhere.Interop;

public enum NativeMessageBoxResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
    Retry,
    Ignore
}

public enum NativeMessageBoxButtons
{
    None,
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
    RetryCancel,
    AbortRetryIgnore
}

public enum NativeMessageBoxIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
    Stop,
    Hand,
    Asterisk
}

public delegate NativeMessageBoxResult NativeMessageBoxHandler(
    string title,
    string message,
    NativeMessageBoxButtons buttons,
    NativeMessageBoxIcon icon);

public static class NativeMessageBox
{
    private static NativeMessageBoxHandler? _handler;

    /// <summary>
    /// Gets an exception handler that shows exceptions in a native message box.
    /// </summary>
    public static IExceptionHandler ExceptionHandler { get; } = new ExceptionHandlerImpl();

    /// <summary>
    /// Registers the native message-box implementation supplied by the platform host.
    /// The handler must be registered before any startup code can report an error.
    /// </summary>
    public static void Register(NativeMessageBoxHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Interlocked.CompareExchange(ref _handler, handler, null) is not null)
        {
            throw new InvalidOperationException("The native message-box handler is already registered.");
        }
    }

    public static NativeMessageBoxResult Show(
        string title,
        string message,
        NativeMessageBoxButtons buttons = NativeMessageBoxButtons.Ok,
        NativeMessageBoxIcon icon = NativeMessageBoxIcon.None)
    {
        return Volatile.Read(ref _handler)?.Invoke(title, message, buttons, icon) ??
            throw new InvalidOperationException("The native message-box handler has not been registered.");
    }

    private sealed class ExceptionHandlerImpl : IExceptionHandler
    {
        public void HandleException(Exception exception, string? message = null, object? source = null, int lineNumber = 0)
        {
            Show(
                $"Error at [{source}:{lineNumber}]",
                $"{message ?? "An error occurred."}\n\n{exception.GetFriendlyMessage()}",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
        }
    }
}