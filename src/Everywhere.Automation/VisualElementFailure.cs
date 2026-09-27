using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Automation;

internal static class VisualElementFailure
{
    public static bool IsRecoverable(Exception exception) =>
        exception is UnauthorizedAccessException or TimeoutException or NotSupportedException or VisualElementProviderException;

    public static bool TryCreate(Exception exception, [NotNullWhen(true)] out VisualElementQueryFailure? failure)
    {
        if (!IsRecoverable(exception))
        {
            failure = null;
            return false;
        }

        var kind = exception switch
        {
            UnauthorizedAccessException => VisualElementQueryFailureKind.PermissionDenied,
            TimeoutException => VisualElementQueryFailureKind.Timeout,
            NotSupportedException => VisualElementQueryFailureKind.Unsupported,
            VisualElementProviderException providerException => providerException.Kind,
            _ => VisualElementQueryFailureKind.ProviderFailure,
        };
        failure = new VisualElementQueryFailure(
            kind,
            null,
            exception,
            kind == VisualElementQueryFailureKind.LimitReached ? exception.Message : null);
        return true;
    }
}