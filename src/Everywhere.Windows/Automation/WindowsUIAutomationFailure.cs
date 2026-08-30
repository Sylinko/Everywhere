using System.Runtime.InteropServices;
using Everywhere.Automation;

namespace Everywhere.Windows.Automation;

/// <summary>
/// Normalizes UI Automation HRESULTs at the platform boundary.
/// </summary>
internal static class WindowsUIAutomationFailure
{
    // Values are defined by the Windows SDK UIAutomationCoreApi.h boundary.
    private enum UiaError
    {
        ElementNotAvailable = unchecked((int)0x80040201),
        NotSupported = unchecked((int)0x80040204),
        Timeout = unchecked((int)0x80131505),
    }

    /// <summary>
    /// Determines whether an exception originated at the UI Automation provider boundary.
    /// </summary>
    public static bool IsProviderException(Exception exception) =>
        exception is COMException or TimeoutException;

    /// <summary>
    /// Determines whether a COM failure reports an unsupported UI Automation operation.
    /// </summary>
    public static bool IsUnsupported(COMException exception) => exception.HResult == (int)UiaError.NotSupported;

    /// <summary>
    /// Converts a provider exception into the platform-independent query failure contract.
    /// </summary>
    public static VisualElementQueryFailure CreateFailure(Exception exception)
    {
        var kind = GetFailureKind(exception);
        var normalizedException = kind == VisualElementQueryFailureKind.Timeout && exception is not TimeoutException ?
            new TimeoutException("The Windows UI Automation provider request timed out.", exception) :
            exception;
        return new VisualElementQueryFailure(kind, null, normalizedException);
    }

    /// <summary>
    /// Converts a provider exception into the closest standard .NET exception.
    /// </summary>
    public static Exception CreateException(Exception exception)
    {
        var kind = GetFailureKind(exception);
        return kind switch
        {
            VisualElementQueryFailureKind.Timeout when exception is TimeoutException => exception,
            VisualElementQueryFailureKind.Timeout => new TimeoutException(
                "The Windows UI Automation provider request timed out.",
                exception),
            VisualElementQueryFailureKind.ElementUnavailable => new InvalidOperationException(
                "The Windows UI Automation element is no longer available.",
                exception),
            VisualElementQueryFailureKind.Unsupported => new NotSupportedException(
                "The Windows UI Automation provider does not support this request.",
                exception),
            _ => new InvalidOperationException(
                "The Windows UI Automation provider request failed.",
                exception),
        };
    }

    private static VisualElementQueryFailureKind GetFailureKind(Exception exception) =>
        exception switch
        {
            TimeoutException or COMException { HResult: (int)UiaError.Timeout } => VisualElementQueryFailureKind.Timeout,
            COMException { HResult: (int)UiaError.ElementNotAvailable } => VisualElementQueryFailureKind.ElementUnavailable,
            COMException { HResult: (int)UiaError.NotSupported } => VisualElementQueryFailureKind.Unsupported,
            _ => VisualElementQueryFailureKind.ProviderFailure,
        };
}