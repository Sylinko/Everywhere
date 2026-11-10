using System.Net.Sockets;
using System.Security.Authentication;
using Everywhere.Common;
using Microsoft.SemanticKernel;

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
            case HttpRequestException:
            case HttpOperationException:
            case HttpIOException:
            case SocketException:
            case AuthenticationException:
            case TimeoutException:
            case OperationCanceledException:
            {
                // Keep raw-exception presentation on the same HTTP/transport path as explicit normalization.
                return HandledSystemException.Handle(e).GetFriendlyMessage();
            }
            case AggregateException ae:
            {
                var innerMessages = ae
                    .InnerExceptions
                    .AsValueEnumerable()
                    .Select(IDynamicLocaleKey (i) => i.GetFriendlyMessage())
                    .Distinct()
                    .ToArray();

                if (innerMessages.Length == 1) return innerMessages[0];

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
                return HandledException.CreateDefaultFriendlyMessage(e);
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
}
