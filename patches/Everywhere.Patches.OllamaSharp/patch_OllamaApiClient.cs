// Copyright (c) 2024 Andreas Wäscher. Licensed under MIT; see LICENSE.txt.
// Complete stream readers copied from OllamaSharp 5.4.30, commit
// b1b408df43a2a13a29e8db9de3130fa6f44aacc9. Everywhere changes are marked below.
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MonoMod;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using OllamaSharp.Models.Exceptions;

namespace Everywhere.Patches.OllamaSharp;

/// <summary>
/// Corrects OllamaSharp's pending stream reads and error handling, which otherwise
/// ignore cancellation or turn protocol errors into apparently successful chat updates.
/// </summary>
/// <remarks>
/// Token-aware reads propagate cooperative cancellation instead of silently ending
/// enumeration. Error objects are recognized before normal chat/done deserialization.
/// Failed HTTP responses retain status, body, and selected retry/request-ID headers,
/// and are disposed even if body reading fails. Successful response ownership stays
/// with the SDK's original callers; this patch adds no retry or timeout policy.
/// See <see href="https://github.com/awaescher/OllamaSharp/blob/b1b408df43a2a13a29e8db9de3130fa6f44aacc9/src/OllamaSharp/OllamaApiClient.cs">the pinned upstream implementation</see>.
/// </remarks>
[MonoModPatch("OllamaSharp.OllamaApiClient")]
internal class patch_OllamaApiClient
{
    [MonoModIgnore]
    public extern JsonSerializerOptions IncomingJsonSerializerOptions { get; }

    [MonoModIgnore]
    public extern Dictionary<string, string> DefaultRequestHeaders { get; }

    [MonoModIgnore]
    private readonly HttpClient _client;

    [MonoModReplace]
    private async IAsyncEnumerable<ChatResponseStream?> ProcessStreamedChatResponseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Everywhere: cancel the pending read and propagate cancellation instead of normal EOF.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) break;

            // Everywhere: the chat reader needs the generic reader's protocol-error check.
            var error = JsonSerializer.Deserialize<ErrorResponse>(line, IncomingJsonSerializerOptions);
            if (error?.Message is { Length: > 0 } message) throw new ResponseError(message);

            var streamedResponse = JsonSerializer.Deserialize<ChatResponseStream>(line, IncomingJsonSerializerOptions);
            yield return streamedResponse?.Done ?? false
                ? JsonSerializer.Deserialize<ChatDoneResponseStream>(line, IncomingJsonSerializerOptions)
                : streamedResponse;
        }
    }

    [MonoModReplace]
    private async IAsyncEnumerable<GenerateResponseStream?> ProcessStreamedCompletionResponseAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Everywhere: cancel the pending read and propagate cancellation instead of normal EOF.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) break;

            // Everywhere: completion errors must not become empty content updates either.
            var error = JsonSerializer.Deserialize<ErrorResponse>(line, IncomingJsonSerializerOptions);
            if (error?.Message is { Length: > 0 } message) throw new ResponseError(message);

            var streamedResponse = JsonSerializer.Deserialize<GenerateResponseStream>(line, IncomingJsonSerializerOptions);
            yield return streamedResponse?.Done ?? false
                ? JsonSerializer.Deserialize<GenerateDoneResponseStream>(line, IncomingJsonSerializerOptions)
                : streamedResponse;
        }
    }

    [MonoModReplace]
    private async IAsyncEnumerable<TLine?> ProcessStreamedResponseAsync<TLine>(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Everywhere: retain the original generic deserialization/error path, but make reads cancellable.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null) break;
            var error = JsonSerializer.Deserialize<ErrorResponse>(line, IncomingJsonSerializerOptions);
            if (error?.Message is { Length: > 0 } message) throw new ResponseError(message);
            yield return JsonSerializer.Deserialize<TLine>(line, IncomingJsonSerializerOptions);
        }
    }

    /// <summary>Transfers successful response ownership; disposes every failed response.</summary>
    [MonoModReplace]
    protected virtual async Task<HttpResponseMessage> SendToOllamaAsync(HttpRequestMessage requestMessage, OllamaRequest? ollamaRequest, HttpCompletionOption completionOption, CancellationToken cancellationToken)
    {
        requestMessage.ApplyCustomHeaders(DefaultRequestHeaders, ollamaRequest);
        var response = await _client.SendAsync(requestMessage, completionOption, cancellationToken).ConfigureAwait(false);
        // Everywhere: inline the original failure check so body reads receive the caller's token,
        // selected HTTP evidence can be retained, and ownership is released on every failure path.
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var message = body;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                    message = error.GetString() ?? body;
            }
            catch (JsonException)
            {
                // Non-JSON proxy errors still carry useful status/body evidence.
            }

            var failure = response.StatusCode == HttpStatusCode.BadRequest
                ? message.Contains("does not support tools", StringComparison.Ordinal)
                    ? (Exception)new ModelDoesNotSupportToolsException(message)
                    : new OllamaException(message)
                : new HttpRequestException(message, null, response.StatusCode);
            failure.Data["Everywhere.Http.StatusCode"] = (int)response.StatusCode;
            failure.Data["Everywhere.Http.ResponseBody"] = body;
            foreach (var name in new[] { "Retry-After", "Retry-After-Ms", "request-id", "x-request-id" })
            {
                if (response.Headers.TryGetValues(name, out var values))
                    failure.Data["Everywhere.Http." + name] = string.Join(",", values);
            }
            throw failure;
        }
    }
}