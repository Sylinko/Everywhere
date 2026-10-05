using Everywhere.Common;
﻿using Everywhere.AI.Prompts;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public abstract partial class KernelMixin(AssistantConfiguration configuration, ModelConnection connection) : IDisposable
{
    /// <summary>
    /// Gets the model configuration snapshot used for this mixin's complete lifetime.
    /// </summary>
    public AssistantConfiguration Configuration => configuration;

    /// <summary>
    /// Convenience accessor for the resolved endpoint (already normalized, never null).
    /// </summary>
    protected string Endpoint => connection.Endpoint;

    /// <summary>
    /// Convenience accessor for the resolved API key (null means no key needed / handled by HttpClient).
    /// </summary>
    protected string? ApiKey => connection.ApiKey;

    public abstract IChatCompletionService ChatCompletionService { get; }

    /// <summary>
    /// Gets the immutable retry budget captured with this connection.
    /// </summary>
    public int RequestMaxRetries => connection.RequestMaxRetries;

    public virtual bool IsPersistentMessageMetadataKey(string key) => false;

    public virtual bool IsPersistentSpanMetadataKey(string key) => false;

    /// <summary>
    /// Default implementation includes temperature and top_p from the custom assistant.
    /// </summary>
    /// <param name="functionChoiceBehavior"></param>
    /// <returns></returns>
    public virtual PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null) => new()
    {
        FunctionChoiceBehavior = functionChoiceBehavior
    };

    public async Task CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var streamRequest = StreamRequestAsync(
            [
                new ChatMessageContent(AuthorRole.System, "You're a helpful assistant."),
                new ChatMessageContent(AuthorRole.User, DefaultPrompts.TestPrompt)
            ],
            GetPromptExecutionSettings(),
            options: new ChatRequestOptions(0),
            cancellationToken: cancellationToken);
        await foreach (var update in streamRequest)
        {
            // Leaving await foreach disposes the SDK stream, without a second request.
            if (update is ChatRequestUpdate.Content) return;
        }
    }

    /// <summary>
    /// Extracts SDK-specific error evidence without selecting classification or retry policy.
    /// Overrides can use typed SDK fields or known protocol semantics. Connection evidence
    /// is enriched separately so an API-compatible service is not mistaken for the SDK vendor.
    /// </summary>
    /// <param name="evidence">The request-local evidence being collected.</param>
    public virtual void ExtractExceptionEvidence(ChatExceptionEvidence evidence)
    {
        ChatExceptionEvidenceExtractor.ExtractSdkEvidence(evidence);
    }

    /// <summary>Recognizes a concrete SDK failure without re-encoding its cause as a category identifier.</summary>
    /// <remarks>Return null when response evidence must be interpreted by the common normalizer.</remarks>
    public virtual HandledChatException? NormalizeKnownException(ChatExceptionEvidence evidence) =>
        ChatExceptionNormalizer.NormalizeKnownSdkException(evidence);

    /// <summary>
    /// Enriches connection-specific evidence after SDK extraction.
    /// </summary>
    public void EnrichConnectionExceptionEvidence(ChatExceptionEvidence evidence)
    {
        connection.ExceptionEvidenceEnricher?.Invoke(evidence);
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        connection.Dispose();
    }
}