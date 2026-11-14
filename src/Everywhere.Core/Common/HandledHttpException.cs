using System.Net;

namespace Everywhere.Common;

/// <summary>
/// Represents an unsuccessful HTTP response, preserving its status without assigning service-specific meaning.
/// </summary>
public sealed class HandledHttpException(
    Exception originalException,
    HttpStatusCode? statusCode,
    bool isExpected = true
) : HandledException(originalException, CreateFriendlyMessage(statusCode), isExpected)
{
    /// <summary>
    /// Gets the response status when the original HTTP or SDK exception exposes one.
    /// </summary>
    public HttpStatusCode? StatusCode { get; } = statusCode;

    private static IDynamicLocaleKey CreateFriendlyMessage(HttpStatusCode? statusCode)
    {
        var key = $"{LocaleKey.FriendlyExceptionMessage_HttpRequest}_{statusCode}";
        if (!DynamicLocaleKey.Exists(key))
        {
            key = LocaleKey.FriendlyExceptionMessage_HttpRequest_0;
        }

        return new FormattedDynamicLocaleKey(
            LocaleKey.FriendlyExceptionMessage_HttpRequest,
            new DirectLocaleKey(statusCode is { } status ? (int)status : 0),
            new DynamicLocaleKey(key));
    }
}