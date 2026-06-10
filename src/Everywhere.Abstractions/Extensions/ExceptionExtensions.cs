using Everywhere.Common;
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
        while (true)
        {
            switch (e)
            {
                case AggregateException ae:
                {
                    var innerMessages = ae.InnerExceptions.AsValueEnumerable()
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
                    e = HandledSystemException.Handle(e);
                    continue;
                }
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