using Everywhere.Common;
using Microsoft.Extensions.Logging;

namespace Everywhere.Extensions;

public static class LoggingExtensions
{
    public static AnonymousExceptionHandler ToExceptionHandler(this ILogger logger)
    {
        return new AnonymousExceptionHandler((exception, message, source, lineNumber) =>
        {
            logger.LogError(exception, "[{MemberName}: {LineNumber}] {Message}", source, lineNumber, message);
        });
    }

    public static AnonymousExceptionHandler<T> ToExceptionHandler<T>(this ILogger<T> logger)
    {
        return new AnonymousExceptionHandler<T>((exception, message, source, lineNumber) =>
        {
            logger.LogError(exception, "[{MemberName}:{LineNumber}] {Message}", source, lineNumber, message);
        });
    }
}