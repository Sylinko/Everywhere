using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Everywhere.Cloud;
using Everywhere.Common;
using OllamaSharp.Models.Exceptions;

namespace Everywhere.AI;

/// <summary>Normalizes original failures into concrete exception categories while retaining diagnostics.</summary>
public static class ChatExceptionNormalizer
{
    /// <summary>Collects common, SDK and connection evidence once; already-handled exceptions retain identity.</summary>
    public static Exception Handle(Exception exception, KernelMixin? kernelMixin, ChatRequestFailureContext context = default)
    {
        if (exception is HandledException)
        {
            return exception;
        }

        if (exception is AggregateException aggregate)
        {
            return new AggregateException(aggregate.Segregate().Select(cause => Handle(cause, kernelMixin, context)));
        }

        var evidence = new ChatExceptionEvidence(exception);
        ChatExceptionEvidenceExtractor.ExtractCommon(evidence, context);
        if (kernelMixin is null)
        {
            ChatExceptionEvidenceExtractor.ExtractSdkEvidence(evidence);
        }
        else
        {
            kernelMixin.ExtractExceptionEvidence(evidence);
            kernelMixin.EnrichConnectionExceptionEvidence(evidence);
        }

        var error = NormalizeKnownException(evidence, kernelMixin) ?? ClassifyResponse(evidence);
        if (error.Diagnostics is { } diagnostics) diagnostics.ModelId = kernelMixin?.Configuration.ModelId;
        return error;
    }

    /// <summary>Normalizes an explicit streaming service code through the response rules.</summary>
    public static HandledChatException FromErrorCode(Exception exception, string code, KernelMixin? kernelMixin = null)
    {
        var evidence = new ChatExceptionEvidence(exception);
        ChatExceptionEvidenceExtractor.ExtractCommon(evidence, default);
        evidence.ErrorCode = code;
        kernelMixin?.EnrichConnectionExceptionEvidence(evidence);
        var error = NormalizeKnownException(evidence, kernelMixin) ?? ClassifyResponse(evidence);
        if (error.Diagnostics is { } diagnostics) diagnostics.ModelId = kernelMixin?.Configuration.ModelId;
        return error;
    }

    /// <summary>Returns the final concrete cause of the known logical-request wrapper.</summary>
    public static HandledChatException? GetFinalFailure(Exception exception) => exception switch
    {
        ChatRequestException request => request.FinalFailure,
        HandledChatException chat => chat,
        _ => null
    };

    /// <summary>Recognizes SDK-specific concrete causes when no mixin is available.</summary>
    public static HandledChatException? NormalizeKnownSdkException(ChatExceptionEvidence evidence) =>
        ChatExceptionEvidenceExtractor.EnumerateCauses(evidence.OriginalException).Any(cause => cause is ModelDoesNotSupportToolsException) ?
            new HandledChatException.UnsupportedCapability.Tools(evidence.OriginalException, evidence.FriendlyMessageKey) :
            null;

    private static HandledChatException? NormalizeKnownException(ChatExceptionEvidence evidence, KernelMixin? kernelMixin)
    {
        var original = evidence.OriginalException;
        var message = evidence.FriendlyMessageKey;
        if (evidence.IsCallerCancellation)
        {
            return Result(new HandledChatException.Canceled.ByCaller(original, message), evidence, "caller-cancellation", "context");
        }

        if (evidence.TimeoutPhase != ChatRequestTimeoutPhase.None)
        {
            return Result(new HandledChatException.Timeout(original, message, evidence.RetryAfter), evidence, "request-timeout", "context");
        }

        var causes = ChatExceptionEvidenceExtractor.EnumerateCauses(original).ToArray();
        if (causes.AsValueEnumerable().Any(cause => cause is AuthenticationException))
        {
            return Result(new HandledChatException.NetworkError.TlsError(original, message), evidence, "tls-failure", "transport");
        }
        if (causes.AsValueEnumerable().Any(cause => cause is HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError }))
        {
            return Result(
                new HandledChatException.NetworkError.ProxyTunnelRejected(original, message),
                evidence,
                "proxy-tunnel-rejected",
                "transport");
        }

        if (evidence.SocketError is { } socket)
        {
            HandledChatException error = socket switch
            {
                SocketError.HostNotFound or SocketError.TryAgain =>
                    new HandledChatException.NetworkError.HostNotFound(original, message, evidence.RetryAfter),
                SocketError.ConnectionRefused =>
                    new HandledChatException.NetworkError.ConnectionRefused(original, message, evidence.RetryAfter),
                _ => new HandledChatException.NetworkError(original, message, evidence.RetryAfter)
            };
            return Result(error, evidence, "socket-failure", "transport");
        }

        if (evidence.TransportException is not null)
        {
            return Result(new HandledChatException.NetworkError(original, message, evidence.RetryAfter), evidence, "transport-failure", "transport");
        }

        var sdkError = kernelMixin is null ? NormalizeKnownSdkException(evidence) : kernelMixin.NormalizeKnownException(evidence);
        if (sdkError is not null)
        {
            return Result(sdkError, evidence, "sdk-exception", "sdk");
        }

        foreach (var cause in causes)
        {
            // Authentication login failures derive from cancellation, so recognize them first.
            var error = cause switch
            {
                UserNotLoginException => (HandledChatException)new HandledChatException.AuthenticationFailure.LoginRequired(original, message),
                OperationCanceledException => new HandledChatException.Canceled(original, message),
                UriFormatException => new HandledChatException.InvalidConfiguration.InvalidEndpoint(original, message),
                JsonException => new HandledChatException.InvalidResponse.MalformedJson(original, message),
                ArgumentException argument when IsHeaderRejection(argument.Message) => new HandledChatException.InvalidRequest(original, message),
                ArgumentOutOfRangeException argument when
                    argument.Message.Contains("Unknown ChatFinishReason value", StringComparison.Ordinal) ||
                    argument.Message.Contains("Unknown ReasoningStatus value", StringComparison.Ordinal) =>
                    new HandledChatException.InvalidResponse.UnsupportedFormat(original, message),
                _ => null
            };
            if (error is not null)
            {
                return Result(error, evidence, "exception-type", "local");
            }
        }

        return null;
    }

    private static HandledChatException ClassifyResponse(ChatExceptionEvidence evidence)
    {
        var baseline = FromStatus(evidence);
        var baselineSource = evidence.StatusCode is null ? "fallback" : "http";
        if (baseline is HandledChatException.Unknown &&
            (FromGenericCode(evidence, evidence.ErrorCode) ?? FromGenericCode(evidence, evidence.ErrorType)) is { } codeBaseline)
        {
            baseline = codeBaseline;
            baselineSource = "code";
        }

        // Only the owning connection interprets gateway-specific codes.
        if (evidence.ConnectionErrorCode is { } connectionCode && FromConnectionCode(evidence, connectionCode) is { } connectionError)
        {
            return Result(connectionError, evidence, connectionCode, "connection");
        }

        return FromCode(evidence, evidence.ErrorCode, evidence.Parameter) ??
            FromCode(evidence, evidence.ErrorType, evidence.Parameter) ??
            FromMessage(evidence, evidence.ErrorMessage, evidence.Parameter) ??
            Result(
                baseline,
                evidence,
                baselineSource switch { "http" => "http-status", "code" => "generic-code", _ => "unknown" },
                baselineSource);
    }

    private static HandledChatException Result(HandledChatException error, ChatExceptionEvidence evidence, string rule, string source)
    {
        error.Diagnostics = new ChatExceptionDiagnostics(evidence, rule, source);
        return error;
    }

    private static bool IsHeaderRejection(string message) =>
        message.Contains("header", StringComparison.OrdinalIgnoreCase) &&
        (message.Contains("ASCII", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("NUL", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("New-line", StringComparison.OrdinalIgnoreCase));

    private static HandledChatException FromStatus(ChatExceptionEvidence evidence) => (int?)evidence.StatusCode switch
    {
        400 or 405 or 406 or 409 or 411 or 413 or 415 or 422 => new HandledChatException.InvalidRequest(evidence.OriginalException, evidence.FriendlyMessageKey),
        401 => new HandledChatException.AuthenticationFailure.InvalidApiKey(evidence.OriginalException, evidence.FriendlyMessageKey),
        402 => new HandledChatException.QuotaExceeded(evidence.OriginalException, evidence.FriendlyMessageKey),
        403 or 451 => new HandledChatException.PermissionDenied(evidence.OriginalException, evidence.FriendlyMessageKey),
        404 or 410 => new HandledChatException.InvalidConfiguration(evidence.OriginalException, evidence.FriendlyMessageKey),
        408 or 504 or 524 => new HandledChatException.Timeout(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        414 => new HandledChatException.InvalidConfiguration.InvalidEndpoint(evidence.OriginalException, evidence.FriendlyMessageKey),
        429 => new HandledChatException.RateLimited(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        501 or 505 => new HandledChatException.UnsupportedCapability(evidence.OriginalException, evidence.FriendlyMessageKey),
        526 => new HandledChatException.NetworkError.TlsError(evidence.OriginalException, evidence.FriendlyMessageKey),
        >= 500 and <= 599 => new HandledChatException.ServiceUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        _ => new HandledChatException.Unknown(evidence.OriginalException, evidence.FriendlyMessageKey)
    };

    private static HandledChatException? FromConnectionCode(ChatExceptionEvidence evidence, string code) => code.ToLowerInvariant() switch
    {
        "validation_error" or "invalid_request" or "request_entity_too_large" =>
            new HandledChatException.InvalidRequest(evidence.OriginalException, evidence.FriendlyMessageKey),
        "invalid_model" or "model_not_found" or "not_found" =>
            new HandledChatException.InvalidConfiguration(evidence.OriginalException, evidence.FriendlyMessageKey),
        "auth_missing" or "auth_invalid" or "auth_expired" =>
            new HandledChatException.AuthenticationFailure.InvalidApiKey(evidence.OriginalException, evidence.FriendlyMessageKey),
        "auth_insufficient_scope" or "billing_user_banned" =>
            new HandledChatException.PermissionDenied(evidence.OriginalException, evidence.FriendlyMessageKey),
        "billing_insufficient_credits" or "billing_insufficient_tier" or "billing_user_not_found" or "user_not_found" =>
            new HandledChatException.QuotaExceeded(evidence.OriginalException, evidence.FriendlyMessageKey),
        "upstream_timeout" =>
            new HandledChatException.Timeout(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        "upstream_unavailable" or "internal_error" or "config_error" or "deduct_credits" =>
            new HandledChatException.ServiceUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        "rate_limit_api" or "rate_limit_llm_burst" or "rate_limit_llm_2h" or "rate_limit_llm_24h" =>
            new HandledChatException.RateLimited(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        // These named windows are plan quotas rather than short transient API limits.
        "rate_limit_expensive_model_5h" or "rate_limit_expensive_model_7d" =>
            new HandledChatException.QuotaExceeded(evidence.OriginalException, evidence.FriendlyMessageKey),
        _ => null
    };

    private static HandledChatException? FromCode(ChatExceptionEvidence evidence, string? code, string? parameter)
    {
        if (code is null) return null;
        var error = code.ToLowerInvariant() switch
        {
            "invalid_api_key" or "authentication_error" or "unauthorized" or "unauthenticated" =>
                new HandledChatException.AuthenticationFailure.InvalidApiKey(evidence.OriginalException, evidence.FriendlyMessageKey),
            "permission_error" or "permission_denied" =>
                new HandledChatException.PermissionDenied(evidence.OriginalException, evidence.FriendlyMessageKey),
            "insufficient_quota" or "billing_error" or "insufficient_balance" =>
                new HandledChatException.QuotaExceeded(evidence.OriginalException, evidence.FriendlyMessageKey),
            "rate_limit_exceeded" or "rate_limit_error" =>
                new HandledChatException.RateLimited(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
            "context_length_exceeded" =>
                new HandledChatException.InvalidRequest.ContextLengthExceeded(evidence.OriginalException, evidence.FriendlyMessageKey),
            "model_not_found" =>
                new HandledChatException.InvalidConfiguration.ModelUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey),
            "unsupported_parameter" or "invalid_parameter" =>
                parameter is null ?
                    FromMessage(evidence, evidence.ErrorMessage, null) ?? ParameterFailure(evidence, null) :
                    ParameterFailure(evidence, parameter),
            "request_too_large" =>
                new HandledChatException.InvalidRequest(evidence.OriginalException, evidence.FriendlyMessageKey),
            "overloaded_error" or "unavailable" =>
                new HandledChatException.ServiceUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
            "timeout_error" or "deadline_exceeded" =>
                new HandledChatException.Timeout(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
            "content_filter" or "content_policy_violation" =>
                new HandledChatException.ContentBlocked(evidence.OriginalException, evidence.FriendlyMessageKey),
            _ => null
        };
        return error is null ? null : Result(error, evidence, code.ToLowerInvariant(), "code");
    }

    private static HandledChatException? FromGenericCode(ChatExceptionEvidence evidence, string? code) => code?.ToLowerInvariant() switch
    {
        "server_error" or "api_error" => new HandledChatException.ServiceUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        "resource_exhausted" => new HandledChatException.RateLimited(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter),
        "invalid_request_error" => new HandledChatException.InvalidRequest(evidence.OriginalException, evidence.FriendlyMessageKey),
        _ => null
    };

    private static HandledChatException ParameterFailure(ChatExceptionEvidence evidence, string? parameter) => parameter?.ToLowerInvariant() switch
    {
        "temperature" => new HandledChatException.InvalidConfiguration.InvalidTemperature(evidence.OriginalException, evidence.FriendlyMessageKey),
        "top_p" or "topp" => new HandledChatException.InvalidConfiguration.InvalidTopP(evidence.OriginalException, evidence.FriendlyMessageKey),
        _ => new HandledChatException.InvalidRequest(evidence.OriginalException, evidence.FriendlyMessageKey)
    };

    private static HandledChatException? FromMessage(ChatExceptionEvidence evidence, string? message, string? parameter)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var text = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        bool Has(string value) => text.Contains(value, StringComparison.Ordinal);

        // Recognizable diagnostic phrases are useful when a gateway flattened the cause.
        // Generic "I/O exception" or "request failed" messages provide no such evidence.
        // 秦始皇复活了都气死了，看这些代码没气笑的可以确诊抑郁症了，我真没招了
        if (Has("(responseended)") ||
            Has("net_http_invalid_response_premature_eof") ||
            Has("unexpected eof or 0 bytes from the transport stream"))
            return Result(new HandledChatException.NetworkError(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter), evidence, "response-interrupted", "message");

        if (Has("no such host is known"))
            return Result(new HandledChatException.NetworkError.HostNotFound(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter), evidence, "host-not-found", "message");

        if (Has("ssl connection could not be established"))
            return Result(new HandledChatException.NetworkError.TlsError(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "tls-handshake-failed", "message");

        if (Has("unknown chatfinishreason value") ||
            Has("unknown reasoningstatus value"))
            return Result(new HandledChatException.InvalidResponse.UnsupportedFormat(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "unknown-sdk-enum", "message");

        if (Has("header") &&
            (Has("only ascii characters") || Has("new-line or nul characters")))
            return Result(new HandledChatException.InvalidRequest(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "invalid-header", "message");

        if (Has("invalid api key") ||
            Has("incorrect api key") ||
            Has("api key is invalid") ||
            Has("api key has expired") ||
            Has("invalid authentication") ||
            Has("invalid signature for authentication"))
            return Result(new HandledChatException.AuthenticationFailure.InvalidApiKey(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "invalid-credential", "message");

        if (Has("credit balance is too low") ||
            Has("no credits remaining") ||
            Has("insufficient account balance") ||
            Has("insufficient balance") ||
            Has("insufficient quota") ||
            Has("billing quota") ||
            Has("exceeded your current quota"))
            return Result(new HandledChatException.QuotaExceeded(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "billing-exhausted", "message");

        if (Has("rate limit") ||
            Has("too many requests") ||
            Has("per-minute rate") ||
            Has("tokens per minute") ||
            Has("requests per minute"))
            return Result(new HandledChatException.RateLimited(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter), evidence, "rate-limited", "message");

        if (Has("maximum context length") ||
            Has("exceeds context size") ||
            Has("exceeds the available context size") ||
            Has("context length exceeded") ||
            Has("context window exceeded"))
            return Result(new HandledChatException.InvalidRequest.ContextLengthExceeded(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "context-overflow", "message");

        if (Has("does not support tools") ||
            Has("tools are not supported") ||
            Has("tool calling is not supported"))
            return Result(new HandledChatException.UnsupportedCapability.Tools(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "tools-unsupported", "message");

        if (Has("does not support image") ||
            Has("images are not supported"))
            return Result(new HandledChatException.UnsupportedCapability.Images(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "images-unsupported", "message");

        if (Has("invalid model name") ||
            Has("model not found") ||
            Has("model does not exist"))
            return Result(new HandledChatException.InvalidConfiguration.ModelUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "model-unavailable", "message");

        if (Has("unsupported region") ||
            Has("region is not supported") ||
            Has("country is not supported"))
            return Result(new HandledChatException.PermissionDenied.RegionRestricted(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "region-restricted", "message");

        if (Has("permission denied") ||
            Has("does not have permission"))
            return Result(new HandledChatException.PermissionDenied(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "permission-denied", "message");

        if (Has("temporarily unavailable") ||
            Has("currently overloaded") ||
            Has("model is overloaded") || text == "overloaded")
            return Result(new HandledChatException.ServiceUnavailable(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter), evidence, "service-overloaded", "message");

        if (Has("request timed out") ||
            Has("deadline expired"))
            return Result(new HandledChatException.Timeout(evidence.OriginalException, evidence.FriendlyMessageKey, evidence.RetryAfter), evidence, "service-timeout", "message");

        if (Has("invalid thought signature") ||
            Has("missing thought signature") ||
            Has("thinking signature is invalid"))
            return Result(new HandledChatException.InvalidRequest.InvalidThoughtSignature(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "thought-signature", "message");

        if (Has("reasoning_content") &&
            (Has("is required") || Has("is missing") || Has("must be provided")))
            return Result(new HandledChatException.InvalidRequest.InvalidReasoningContent(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "reasoning-required", "message");

        if (Has("blocked due to") &&
            Has("safety") ||
            Has("content policy violation"))
            return Result(new HandledChatException.ContentBlocked(evidence.OriginalException, evidence.FriendlyMessageKey), evidence, "content-blocked", "message");

        var hasTemperatureRejection =
            Has("unsupported parameter: temperature") ||
            Has("unsupported parameter: 'temperature'") ||
            Has("temperature is not supported") ||
            Has("temperature' is not supported");

        var hasTopPRejection =
            Has("unsupported parameter: top_p") ||
            Has("unsupported parameter: 'top_p'") ||
            Has("top_p is not supported") ||
            Has("top_p' is not supported");

        if (Has("unsupported parameter") ||
            hasTemperatureRejection ||
            hasTopPRejection ||
            Has("must be less than") &&
            (Has("max_tokens") || Has("max_completion_tokens")) || Has("image_url must be a valid"))
            return Result(ParameterFailure(evidence, hasTemperatureRejection ? "temperature" : hasTopPRejection ? "top_p" : parameter), evidence, "parameter-rejected", "message");

        return null;
    }
}