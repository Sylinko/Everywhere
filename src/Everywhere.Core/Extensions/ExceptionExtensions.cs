using System.Net;
using System.Net.Sockets;
using Everywhere.Common;
using Microsoft.SemanticKernel;
using ZLinq;

namespace Everywhere.Extensions;

public static class ExceptionExtensions
{
    /// <summary>
    /// Convert the exception to a friendly message resource key. It will try to find a specific message for the exception type, if not found, it will return the original message.
    /// For some common exceptions, it will also try to provide more user-friendly messages based on the exception details (e.g. HttpRequestException with status code).
    /// </summary>
    /// <param name="e"></param>
    /// <returns></returns>
    public static IDynamicLocaleKey GetFriendlyMessage(this Exception e)
    {
        switch (e)
        {
            case HttpRequestException hre:
            {
                return FormatHttpExceptionMessage(LocaleKey.FriendlyExceptionMessage_HttpRequest, hre.StatusCode);
            }
            case HttpOperationException hoe:
            {
                return FormatHttpExceptionMessage(LocaleKey.FriendlyExceptionMessage_HttpRequest, hoe.StatusCode);
            }
            case SocketException se:
            {
                return new FormattedDynamicLocaleKey(
                    LocaleKey.FriendlyExceptionMessage_Socket,
                    new DirectLocaleKey((int)se.SocketErrorCode),
                    new DynamicLocaleKey($"{LocaleKey.FriendlyExceptionMessage_Socket}_{se.SocketErrorCode.ToString()}"));
            }
            case AggregateException ae:
            {
                var innerMessages = ae
                    .InnerExceptions
                    .AsValueEnumerable()
                    .Select(IDynamicLocaleKey (i) => i.GetFriendlyMessage())
                    .Distinct()
                    .ToList();

                if (innerMessages.Count == 1) return innerMessages[0];

                return new FormattedDynamicLocaleKey(
                    LocaleKey.FriendlyExceptionMessage_Aggregate,
                    new AggregateDynamicLocaleKey(innerMessages, "\n"));
            }
            case HandledException he:
            {
                return he.FriendlyMessageKey;
            }
            default:
            {
                var exceptionName = e.GetType().Name;
                if (exceptionName.EndsWith("Exception")) exceptionName = exceptionName[..^"Exception".Length];

                var messageKey = $"FriendlyExceptionMessage_{exceptionName}";
                return DynamicLocaleKey.Exists(messageKey) ?
                    new AggregateDynamicLocaleKey(
                        [
                            new DynamicLocaleKey(messageKey),
                            new DirectLocaleKey(e.Message.Trim())
                        ],
                        "\n") :
                    new DirectLocaleKey(e.Message.Trim());
            }
        }
    }

    /// <summary>
    /// segregate the exception if it is an AggregateException
    /// </summary>
    /// <param name="e"></param>
    /// <returns></returns>
    public static IEnumerable<Exception> Segregate(this Exception? e)
    {
        switch (e)
        {
            case null:
            {
                yield break;
            }
            case AggregateException ae:
            {
                foreach (var inner in ae.InnerExceptions.SelectMany(ie => ie.Segregate()))
                {
                    yield return inner;
                }
                break;
            }
            default:
            {
                yield return e;
                break;
            }
        }
    }

    private static FormattedDynamicLocaleKey FormatHttpExceptionMessage(string baseKey, HttpStatusCode? statusCode)
    {
        var key = $"{baseKey}_{statusCode.ToString()}";
        if (!DynamicLocaleKey.Exists(key)) key = $"{baseKey}_0";
        return new FormattedDynamicLocaleKey(
            baseKey,
            new DirectLocaleKey(statusCode ?? 0),
            new DynamicLocaleKey(key));
    }
}