using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Everywhere.Common;

/// <summary>
/// Collects .NET HTTP and transport facts without interpreting service errors or choosing recovery policy.
/// </summary>
public struct NetworkExceptionEvidence
{
    /// <summary>
    /// Gets the first HTTP status reported in the exception chain. A proxy rejection may also carry a status.
    /// </summary>
    public HttpStatusCode? StatusCode { get; private set; }

    /// <summary>
    /// Gets a concrete transport cause, rather than an exception raised solely for an unsuccessful HTTP response.
    /// </summary>
    public Exception? TransportException { get; private set; }

    /// <summary>
    /// Gets the actual socket error code, if a SocketException supplied one.
    /// </summary>
    public SocketError? SocketError { get; private set; }

    /// <summary>
    /// Gets whether the chain contains a TLS authentication failure.
    /// </summary>
    public bool HasTlsError { get; private set; }

    /// <summary>
    /// Gets whether an HTTP proxy rejected the tunnel, rather than the destination service rejecting the request.
    /// </summary>
    public bool HasProxyTunnelError { get; private set; }

    /// <summary>
    /// Gets whether the chain contains an explicit timeout. Cancellation provenance belongs to the request owner.
    /// </summary>
    public bool HasTimeoutCause { get; private set; }

    /// <summary>
    /// Includes one cause. Callers may interleave this with SDK-specific evidence extraction in chain order.
    /// </summary>
    public void Include(Exception exception)
    {
        switch (exception)
        {
            case HttpRequestException http:
                StatusCode ??= http.StatusCode;
                HasProxyTunnelError |= http.HttpRequestError == HttpRequestError.ProxyTunnelError;
                if (http.StatusCode is null)
                {
                    TransportException ??= http;
                }
                break;
            case SocketException socket:
                SocketError = socket.SocketErrorCode;
                TransportException = socket;
                break;
            case AuthenticationException authentication:
                HasTlsError = true;
                TransportException = authentication;
                break;
            case HttpIOException httpIo:
                TransportException ??= httpIo;
                break;
            case TimeoutException:
                HasTimeoutCause = true;
                break;
        }
    }

    /// <summary>
    /// Collects transport facts across the same bounded wrapper chain used by chat error handling.
    /// </summary>
    public static NetworkExceptionEvidence Extract(Exception exception)
    {
        var evidence = new NetworkExceptionEvidence();
        foreach (var cause in EnumerateCauses(exception))
        {
            evidence.Include(cause);
        }
        return evidence;
    }

    /// <summary>
    /// Enumerates a bounded single-cause chain. Aggregate branches are handled independently by each entry point.
    /// </summary>
    public static IEnumerable<Exception> EnumerateCauses(Exception exception)
    {
        for (var depth = 0; depth < 16; depth++)
        {
            yield return exception;
            if (exception.InnerException is not { } inner || ReferenceEquals(inner, exception))
            {
                yield break;
            }
            exception = inner;
        }
    }
}