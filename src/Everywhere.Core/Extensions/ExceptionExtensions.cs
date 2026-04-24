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
    public static IDynamicResourceKey GetFriendlyMessage(this Exception e)
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
                return new FormattedDynamicResourceKey(
                    LocaleKey.FriendlyExceptionMessage_Socket,
                    new DirectResourceKey((int)se.SocketErrorCode),
                    new DynamicResourceKey($"{LocaleKey.FriendlyExceptionMessage_Socket}_{se.SocketErrorCode.ToString()}"));
            }
            case AggregateException ae:
            {
                var innerMessages = ae
                    .InnerExceptions
                    .AsValueEnumerable()
                    .Select(IDynamicResourceKey (i) => i.GetFriendlyMessage())
                    .Distinct()
                    .ToList();

                if (innerMessages.Count == 1) return innerMessages[0];

                return new FormattedDynamicResourceKey(
                    LocaleKey.FriendlyExceptionMessage_Aggregate,
                    new AggregateDynamicResourceKey(innerMessages, "\n"));
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
                return DynamicResourceKey.Exists(messageKey) ?
                    new AggregateDynamicResourceKey(
                        [
                            new DynamicResourceKey(messageKey),
                            new DirectResourceKey(e.Message.Trim())
                        ],
                        "\n") :
                    new DirectResourceKey(e.Message.Trim());
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

    private static FormattedDynamicResourceKey FormatHttpExceptionMessage(string baseKey, HttpStatusCode? statusCode)
    {
        var key = $"{baseKey}_{statusCode.ToString()}";
        if (!DynamicResourceKey.Exists(key)) key = $"{baseKey}_0";
        return new FormattedDynamicResourceKey(
            baseKey,
            new DirectResourceKey(statusCode ?? 0),
            new DynamicResourceKey(key));
    }
}