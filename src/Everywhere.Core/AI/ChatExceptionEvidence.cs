using System.Net;

namespace Everywhere.AI;

/// <summary>Mutable, request-local evidence collected before an LLM failure is classified.</summary>
/// <remarks>
/// SDK and connection adapters enrich this object without choosing retry policy. The
/// original exception remains intact for diagnostics; response text is bounded by the extractor.
/// </remarks>
public sealed class ChatExceptionEvidence(Exception originalException)
{
    /// <summary>
    /// Gets the original cause, including SDK wrappers and their inner exceptions.
    /// </summary>
    public Exception OriginalException { get; } = originalException;

    /// <summary>
    /// Gets or sets the effective response status (upstream status for a gateway envelope).
    /// </summary>
    public HttpStatusCode? StatusCode { get; set; }

    /// <summary>
    /// Gets or sets the outer gateway status when upstream evidence replaces it.
    /// </summary>
    public HttpStatusCode? GatewayStatusCode { get; set; }

    /// <summary>
    /// Gets or sets the structured service error code.
    /// </summary>
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Gets or sets the structured service error type.
    /// </summary>
    public string? ErrorType { get; set; }

    /// <summary>
    /// Gets or sets the rejected request parameter, if reported.
    /// </summary>
    public string? Parameter { get; set; }

    /// <summary>
    /// Gets or sets the extracted error message, separate from raw response JSON.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets a bounded response body for diagnostics.
    /// </summary>
    public string? ResponseBody { get; set; }

    /// <summary>
    /// Gets or sets the service request identifier.
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// Gets or sets a valid server-requested retry delay.
    /// </summary>
    public TimeSpan? RetryAfter { get; set; }

    /// <summary>
    /// Gets or sets the concrete transport cause rather than its SDK wrapper.
    /// </summary>
    public Exception? TransportException { get; set; }

    /// <summary>
    /// Gets or sets the actual socket error code when available.
    /// </summary>
    public System.Net.Sockets.SocketError? SocketError { get; set; }

    /// <summary>
    /// Gets or sets whether caller cancellation is established by request-context evidence.
    /// </summary>
    public bool IsCallerCancellation { get; set; }

    /// <summary>
    /// Gets or sets the known timeout phase; None means no timeout was established.
    /// </summary>
    public ChatRequestTimeoutPhase TimeoutPhase { get; set; }

    /// <summary>
    /// Gets or sets a connection-specific code, independent of the SDK protocol.
    /// </summary>
    public string? ConnectionErrorCode { get; set; }

    /// <summary>
    /// Gets or sets an authoritative connection-specific friendly message.
    /// </summary>
    public IDynamicLocaleKey? FriendlyMessageKey { get; set; }
}

/// <summary>Identifies which request wait expired without imposing a whole-generation deadline.</summary>
public enum ChatRequestTimeoutPhase
{
    /// <summary>
    /// No timeout evidence.
    /// </summary>
    None,

    /// <summary>
    /// Waiting for response headers.
    /// </summary>
    ResponseWait,

    /// <summary>
    /// Waiting for response-body data.
    /// </summary>
    ReadIdle,

    /// <summary>
    /// An SDK reports a timeout without exposing its phase.
    /// </summary>
    Unknown
}

/// <summary>Supplies cancellation provenance from the operation that owns the request.</summary>
public readonly record struct ChatRequestFailureContext(
    CancellationToken CallerCancellationToken,
    ChatRequestTimeoutPhase TimeoutPhase = ChatRequestTimeoutPhase.None
);