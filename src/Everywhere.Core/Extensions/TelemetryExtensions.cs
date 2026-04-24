using System.Diagnostics;
using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Extensions;

public static class TelemetryExtensions
{
    /// <summary>
    /// Starts a chat activity for telemetry.
    /// </summary>
    /// <param name="activitySource"></param>
    /// <param name="operationName"></param>
    /// <param name="modelDefinition"></param>
    /// <param name="displayName"></param>
    /// <returns></returns>
    public static Activity? StartChatActivity(
        this ActivitySource activitySource,
        string operationName,
        IModelDefinition? modelDefinition,
        [CallerMemberName] string displayName = "")
    {
        IEnumerable<KeyValuePair<string, object?>> tags = modelDefinition is null ?
            [
                new KeyValuePair<string, object?>("gen_ai.operation.name", operationName)
            ] :
            [
                new KeyValuePair<string, object?>("gen_ai.operation.name", operationName),
                new KeyValuePair<string, object?>("gen_ai.request.model", modelDefinition.ModelId),
                new KeyValuePair<string, object?>("gen_ai.request.supports_image", modelDefinition.InputModalities.SupportsImage),
                new KeyValuePair<string, object?>("gen_ai.request.supports_reasoning", modelDefinition.SupportsReasoning),
                new KeyValuePair<string, object?>("gen_ai.request.supports_tool", modelDefinition.SupportsToolCall),
                new KeyValuePair<string, object?>("gen_ai.request.context_limit", modelDefinition.ContextLimit),
            ];
        return activitySource.StartActivity(
            $"gen_ai.{operationName}",
            ActivityKind.Client,
            null,
            tags).With(x => x?.DisplayName = displayName);
    }

    /// <summary>
    /// Sets chat usage tags to the activity.
    /// </summary>
    /// <param name="activity"></param>
    /// <param name="usage"></param>
    public static void SetChatUsageTags(this Activity? activity, ChatUsageDetails usage)
    {
        activity?.SetTag("gen_ai.usage.input_tokens", usage.InputTokenCount);
        activity?.SetTag("gen_ai.usage.cached_input_tokens", usage.CachedInputTokenCount);
        activity?.SetTag("gen_ai.usage.output_tokens", usage.OutputTokenCount);
        activity?.SetTag("gen_ai.usage.reasoning_tokens", usage.ReasoningTokenCount);
        activity?.SetTag("gen_ai.usage.total_tokens", usage.TotalTokenCount);
    }
}