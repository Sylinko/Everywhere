using Anthropic.Models.Messages;
using Everywhere.Common;
using Microsoft.SemanticKernel;
using OpenAI.Responses;

namespace Everywhere.AI;

public static partial class ChatExceptionNormalizer
{
    /// <summary>
    /// Classifies explicit unsuccessful termination evidence. Missing or unfamiliar finish
    /// fields alone remain compatible; an explicit failed/incomplete status does not.
    /// </summary>
    public static HandledChatException? FromCompletion(StreamingChatMessageContent content, KernelMixin mixin)
    {
        var reason = content.Metadata?.GetValueOrDefault("FinishReason")?.ToString();
        var providerReason = content.InnerContent switch
        {
            RawMessageStreamEvent { Value: RawMessageDeltaEvent delta } => delta.Delta.StopReason?.Raw(),
            RawMessageDeltaEvent delta => delta.Delta.StopReason?.Raw(),
            StreamingResponseIncompleteUpdate incomplete => incomplete.Response.IncompleteStatusDetails?.Reason.ToString(),
            _ => null
        };
        var hasExplicitFailure = content.InnerContent is StreamingResponseIncompleteUpdate or StreamingResponseFailedUpdate;
        var value = (providerReason ?? reason)?.Trim().ToLowerInvariant();
        if (value is null && !hasExplicitFailure) return null;

        var original = new InvalidOperationException($"Generation terminated with reason '{providerReason ?? reason ?? "unspecified"}'.");
        var evidence = new ChatExceptionEvidence(original) { FinishReason = reason, ProviderFinishReason = providerReason };
        return ClassifyCompletion(evidence, mixin, value, hasExplicitFailure);
    }

    private static HandledChatException? ClassifyCompletion(
        ChatExceptionEvidence evidence,
        KernelMixin? mixin,
        string? value,
        bool hasExplicitFailure)
    {
        var original = evidence.OriginalException;
        HandledChatException? error = value switch
        {
            "length" or "model_length" or "max_tokens" or "max_output_tokens" or "maxoutputtokens" =>
                new HandledChatException.GenerationLimitExceeded(original),
            "model_context_window_exceeded" =>
                new HandledChatException.GenerationLimitExceeded.ContextWindowExceeded(original),
            "content_filter" or "contentfilter" or "refusal" or "safety" or "prohibited_content" or
                "blocklist" or "spii" or "image_safety" or "image_prohibited_content" or "escalation" =>
                new HandledChatException.ContentBlocked(original),
            "recitation" or "image_recitation" =>
                new HandledChatException.ContentBlocked(original, new DynamicLocaleKey(LocaleKey.HandledChatException_ContentBlocked_Recitation)),
            "language" =>
                new HandledChatException.ContentBlocked(original, new DynamicLocaleKey(LocaleKey.HandledChatException_ContentBlocked_Language)),
            "pup_limited_disabled" =>
                new HandledChatException.PermissionDenied(original),
            "insufficient_system_resource" =>
                new HandledChatException.ServiceUnavailable(original),
            "pause_turn" =>
                new HandledChatException.InvalidResponse.ContinuationRequired(original),
            "malformed_function_call" or "unexpected_tool_call" or "too_many_tool_calls" or "malformed_response" =>
                new HandledChatException.InvalidResponse(original),
            "missing_thought_signature" =>
                new HandledChatException.InvalidRequest.InvalidThoughtSignature(original),
            "no_image" =>
                new HandledChatException.InvalidResponse.EmptyResponse(original),
            "other" or "image_other" when mixin?.Configuration.Schema == ModelProviderSchema.Google =>
                new HandledChatException.InvalidResponse.Incomplete(original),
            "aborted" =>
                new HandledChatException.InvalidResponse.Incomplete(original),
            "error" when mixin?.Configuration.Schema == ModelProviderSchema.Mistral =>
                new HandledChatException.InvalidResponse.Incomplete(original),
            _ => hasExplicitFailure ?
                new HandledChatException.InvalidResponse.Incomplete(original) :
                null
        };

        if (error is null) return null;
        error.Diagnostics = new ChatExceptionDiagnostics(evidence, "generation-termination", "completion")
        {
            ModelId = mixin?.Configuration.ModelId
        };
        return error;
    }
}