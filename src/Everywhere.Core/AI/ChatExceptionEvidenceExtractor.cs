using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Anthropic.Exceptions;
using Everywhere.Cloud;
using Microsoft.SemanticKernel;
using OllamaSharp.Models.Exceptions;

namespace Everywhere.AI;

/// <summary>Reads bounded exception/HTTP evidence without classifying the service failure.</summary>
public static class ChatExceptionEvidenceExtractor
{
    /// <summary>Maximum retained response-body characters; raw exceptions remain available separately.</summary>
    public const int MaximumBodyLength = 32768;

    /// <summary>Collects common transport and request-context evidence across bounded wrapper chains.</summary>
    public static void ExtractCommon(ChatExceptionEvidence evidence, ChatRequestFailureContext context)
    {
        evidence.ErrorMessage = Bound(evidence.OriginalException.Message);
        foreach (var exception in EnumerateCauses(evidence.OriginalException))
        {
            ExtractHeaders(evidence, name => exception.Data["Everywhere.Http." + name]?.ToString());
            if (exception.Data["Everywhere.Http.StatusCode"] is int status and >= 100 and <= 599)
                evidence.StatusCode ??= (HttpStatusCode)status;

            if (exception.Data["Everywhere.Http.ResponseBody"] is string body)
                ExtractResponseBody(evidence, body);

            switch (exception)
            {
                case HttpOperationException operation:
                    evidence.StatusCode ??= operation.StatusCode;
                    ExtractResponseBody(evidence, operation.ResponseContent);
                    break;
                case HttpRequestException http:
                    evidence.StatusCode ??= http.StatusCode;
                    if (!http.StatusCode.HasValue) evidence.TransportException ??= http;
                    break;
                case SocketException socket:
                    evidence.SocketError = socket.SocketErrorCode;
                    evidence.TransportException = socket;
                    break;
                case AuthenticationException authentication:
                    evidence.TransportException = authentication;
                    break;
                case IOException io:
                    evidence.TransportException ??= io;
                    break;
                case TimeoutException:
                    evidence.TimeoutPhase = context.TimeoutPhase == ChatRequestTimeoutPhase.None ?
                        ChatRequestTimeoutPhase.Unknown :
                        context.TimeoutPhase;
                    break;
                case UserNotLoginException:
                    break;
                case OperationCanceledException:
                    if (context.TimeoutPhase != ChatRequestTimeoutPhase.None)
                    {
                        evidence.TimeoutPhase = context.TimeoutPhase;
                    }
                    else if (evidence.OriginalException is OperationCanceledException && context.CallerCancellationToken.IsCancellationRequested)
                    {
                        evidence.IsCallerCancellation = true;
                    }
                    break;
            }
        }

        // An SDK deadline with an inner TimeoutException is not ordinary caller cancellation.
        if (evidence.TimeoutPhase != ChatRequestTimeoutPhase.None) evidence.IsCallerCancellation = false;
    }

    /// <summary>Provides SDK-aware fallback when no mixin is available.</summary>
    public static void ExtractSdkEvidence(ChatExceptionEvidence evidence)
    {
        ExtractOpenAI(evidence);
        ExtractAnthropic(evidence);
        ExtractOllama(evidence);
    }

    /// <summary>Reads OpenAI/System.ClientModel status and already-buffered raw response evidence.</summary>
    public static void ExtractOpenAI(ChatExceptionEvidence evidence)
    {
        foreach (var exception in EnumerateCauses(evidence.OriginalException).OfType<ClientResultException>())
        {
            if (exception.Status is >= 100 and <= 599) evidence.StatusCode ??= (HttpStatusCode)exception.Status;
            if (exception.GetRawResponse() is not { } response) continue;
            ExtractResponseBody(evidence, response.Content.ToString());
            ExtractHeaders(evidence, name => response.Headers.TryGetValue(name, out var value) ? value : null);
        }
    }

    /// <summary>Reads Anthropic's typed response fields without treating status-derived subtypes as specific diagnoses.</summary>
    public static void ExtractAnthropic(ChatExceptionEvidence evidence)
    {
        foreach (var exception in EnumerateCauses(evidence.OriginalException).OfType<AnthropicApiException>())
        {
            evidence.StatusCode ??= exception.StatusCode;
            ExtractResponseBody(evidence, exception.ResponseBody);
        }
    }

    /// <summary>Reads Ollama protocol error messages; concrete capability failures are normalized separately.</summary>
    public static void ExtractOllama(ChatExceptionEvidence evidence)
    {
        foreach (var exception in EnumerateCauses(evidence.OriginalException).OfType<OllamaException>())
        {
            if (evidence.ResponseBody is null) evidence.ErrorMessage = Bound(exception.Message);
        }
    }

    /// <summary>Extracts only recognized error-envelope fields; arbitrary echoed request JSON is not scanned.</summary>
    public static void ExtractResponseBody(ChatExceptionEvidence evidence, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        var boundedBody = body.Length > MaximumBodyLength ? body[..MaximumBodyLength] : body;
        evidence.ResponseBody = boundedBody;
        evidence.ErrorCode = null;
        evidence.ErrorType = null;
        evidence.Parameter = null;

        var trimmed = boundedBody.AsSpan().TrimStart();
        if (trimmed.IsEmpty)
        {
            return;
        }

        if (trimmed[0] is not ('{' or '['))
        {
            evidence.ErrorMessage = evidence.ResponseBody;
            return;
        }

        // Do not fall back to matching a JSON body as prose when parsing fails or its shape is unknown.
        evidence.ErrorMessage = null;
        try
        {
            using var document = JsonDocument.Parse(boundedBody, new JsonDocumentOptions { MaxDepth = 32 });

            var error = document.RootElement;
            if (error.ValueKind != JsonValueKind.Object) return;

            if (error.TryGetProperty("error", out var nested)) error = nested;
            switch (error.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    evidence.ErrorCode = ReadString(error, "code");
                    evidence.ErrorType = ReadString(error, "type") ?? ReadString(error, "status");
                    evidence.Parameter = ReadString(error, "param");
                    evidence.ErrorMessage = ReadString(error, "message");
                    return;
                }
                case JsonValueKind.String:
                {
                    evidence.ErrorMessage = Bound(error.GetString());
                    return;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed gateway error bodies still retain their HTTP status and raw diagnostics.
        }
    }

    /// <summary>Enumerates a bounded single-cause chain; aggregates are handled independently at the entry point.</summary>
    public static IEnumerable<Exception> EnumerateCauses(Exception exception)
    {
        for (var depth = 0; depth < 16; depth++)
        {
            yield return exception;
            if (exception.InnerException is not { } inner || ReferenceEquals(inner, exception)) yield break;
            exception = inner;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Bound(value.GetString()) : null;

    private static string? Bound(string? value) => value is { Length: > MaximumBodyLength } ? value[..MaximumBodyLength] : value;

    private static void ExtractHeaders(ChatExceptionEvidence evidence, Func<string, string?> read)
    {
        evidence.RequestId ??= read("request-id") ?? read("x-request-id");
        var milliseconds = read("Retry-After-Ms");
        if (double.TryParse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) &&
            double.IsFinite(ms) &&
            ms >= 0 &&
            ms < TimeSpan.MaxValue.TotalMilliseconds)
        {
            KeepDelay(TimeSpan.FromMilliseconds(ms));
        }

        var retryAfter = read("Retry-After");
        if (double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            double.IsFinite(seconds) &&
            seconds >= 0 &&
            seconds < TimeSpan.MaxValue.TotalSeconds)
        {
            KeepDelay(TimeSpan.FromSeconds(seconds));
        }
        else if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var now = DateTimeOffset.UtcNow;
            KeepDelay(date > now ? date - now : TimeSpan.Zero);
        }

        void KeepDelay(TimeSpan delay)
        {
            if (evidence.RetryAfter is null || delay > evidence.RetryAfter) evidence.RetryAfter = delay;
        }
    }
}