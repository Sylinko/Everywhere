using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anthropic.Models.Messages;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.Google.Core;
using OpenAI.Chat;
using OpenAI.Responses;
using ZLinq;

namespace Everywhere.Chat;

partial class ChatService
{
    /// <summary>
    /// A builder class for creating <see cref="FunctionCallContent"/> objects from incremental function call updates represented by <see cref="StreamingFunctionCallUpdateContent"/>.
    /// </summary>
    public sealed class FunctionCallContentBuilder
    {
        public int Count => Math.Max(
            Math.Max(_functionCallIdsByIndex?.Count ?? 0, _functionNamesByIndex?.Count ?? 0),
            Math.Max(_functionArgumentBuildersByIndex?.Count ?? 0, _functionMetadataByIndex?.Count ?? 0));

        private Dictionary<string, string>? _functionCallIdsByIndex;
        private Dictionary<string, string>? _functionNamesByIndex;
        private Dictionary<string, StringBuilder>? _functionArgumentBuildersByIndex;
        private Dictionary<string, IReadOnlyDictionary<string, object?>>? _functionMetadataByIndex;
        private Dictionary<string, string>? _functionCallIndexesById;

        private static readonly JsonSerializerOptions JsonSerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = FunctionCallContentBuilderJsonSerializerContext.Default,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        /// <summary>
        /// Extracts function call updates from the content and track them for later building.
        /// </summary>
        /// <param name="content">The content to extract function call updates from.</param>
        /// <returns><see langword="true"/> if the content contains function call updates.</returns>
        public bool Append(StreamingChatMessageContent content)
        {
            var hasFunctionCallUpdates = content.Items.AsValueEnumerable().OfType<StreamingFunctionCallUpdateContent>().Aggregate(
                false,
                (current, update) => current | TrackStreamingFunctionCallUpdate(update));

            if (!hasFunctionCallUpdates)
            {
                // check inner content because SK doesn't handle them
                hasFunctionCallUpdates = content.InnerContent switch
                {
                    // OpenAI Chat Completions
                    StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 } => true,
                    // OpenAI Responses
                    StreamingResponseOutputItemAddedUpdate { Item: FunctionCallResponseItem } => true,
                    // Anthropic Messages
                    RawMessageStreamEvent
                    {
                        Value:
                        RawContentBlockStartEvent { ContentBlock.Value: ToolUseBlock } or
                        RawContentBlockDeltaEvent { Delta.Value: ToolUseBlock }
                    } => true,
                    // Gemini (Seems not working?)
                    GeminiResponse { Candidates.Count: > 0 } geminiResponse => geminiResponse.Candidates?.AsValueEnumerable().Any(candidate =>
                        candidate.Content?.Parts?.AsValueEnumerable().Any(part => part.FunctionCall is not null) is true) is true,
                    GeminiPart { FunctionCall: not null } => true,
                    _ => hasFunctionCallUpdates
                };
            }

            return hasFunctionCallUpdates;
        }

        /// <summary>
        /// Builds a list of <see cref="FunctionCallContent"/> out of function call updates tracked by the <see cref="Append"/> method.
        /// </summary>
        /// <returns>A list of <see cref="FunctionCallContent"/> objects.</returns>
        public IReadOnlyList<FunctionCallContent> Build()
        {
            FunctionCallContent[]? functionCalls = null;

            if (_functionCallIdsByIndex is not { Count: > 0 }) return functionCalls ?? [];
            functionCalls = new FunctionCallContent[_functionCallIdsByIndex.Count];

            for (var i = 0; i < _functionCallIdsByIndex.Count; i++)
            {
                var functionCallIndexAndId = _functionCallIdsByIndex.ElementAt(i);
                var functionName = string.Empty;

                if (_functionNamesByIndex?.TryGetValue(functionCallIndexAndId.Key, out var fqn) ?? false)
                {
                    functionName = fqn;
                }

                var (arguments, exception) = GetFunctionArguments(functionCallIndexAndId.Key);

                IReadOnlyDictionary<string, object?>? metadata = null;
                _functionMetadataByIndex?.TryGetValue(functionCallIndexAndId.Key, out metadata);

                functionCalls[i] = new FunctionCallContent(
                    functionName: functionName,
                    pluginName: null,
                    id: functionCallIndexAndId.Value,
                    arguments)
                {
                    Exception = exception,
                    Metadata = metadata
                };
            }

            return functionCalls;
        }

        /// <summary>
        /// Gets function arguments for a given function call index.
        /// </summary>
        /// <param name="functionCallIndex">The function call index to get the function arguments for.</param>
        /// <returns>A tuple containing the KernelArguments and an Exception if any.</returns>
        private (KernelArguments? Arguments, Exception? Exception) GetFunctionArguments(string functionCallIndex)
        {
            if (_functionArgumentBuildersByIndex is null ||
                !_functionArgumentBuildersByIndex.TryGetValue(functionCallIndex, out var functionArgumentsBuilder))
            {
                return (null, null);
            }

            var argumentsJson = functionArgumentsBuilder.ToString();
            if (string.IsNullOrEmpty(argumentsJson))
            {
                return (null, null);
            }

            Exception? exception = null;
            KernelArguments? arguments = null;
            try
            {
                arguments = JsonSerializer.Deserialize<KernelArguments>(argumentsJson, JsonSerializerOptions);
            }
            catch (JsonException ex)
            {
                exception = new KernelException("Error: Function call arguments were invalid JSON.", ex);
            }

            return (arguments, exception);
        }

        /// <summary>
        /// Tracks streaming function call update contents.
        /// </summary>
        /// <param name="update">The streaming function call update content to track.</param>
        /// <returns><see langword="true"/> if the update contains function call data.</returns>
        private bool TrackStreamingFunctionCallUpdate(StreamingFunctionCallUpdateContent? update)
        {
            if (update is null)
            {
                // Nothing to track.
                return false;
            }

            var hasFunctionCallUpdate =
                update.CallId is { Length: > 0 } ||
                update.Name is { Length: > 0 } ||
                update.Arguments is not null ||
                update.Metadata is not null;

            if (!hasFunctionCallUpdate)
            {
                return false;
            }

            // Create index that is stable across streaming chunks. CallId is often only present in
            // one chunk, so the request/function-call indexes are the primary join key.
            var functionCallIndex = GetFunctionCallIndex(update);

            // If we have a call id, ensure the index is being tracked. Even if it's not a function update,
            // we want to keep track of it so we can send back an error.
            if (update.CallId is { Length: > 0 } id)
            {
                (_functionCallIdsByIndex ??= [])[functionCallIndex] = id;
            }

            // Ensure we're tracking the function's name.
            if (update.Name is { Length: > 0 } name)
            {
                (_functionNamesByIndex ??= [])[functionCallIndex] = name;
            }

            // Track metadata
            if (update.Metadata is not null)
            {
                (_functionMetadataByIndex ??= [])[functionCallIndex] = update.Metadata;
            }

            // Ensure we're tracking the function's arguments.
            if (update.Arguments is not { } argumentsUpdate)
            {
                return true;
            }

            if (!(_functionArgumentBuildersByIndex ??= []).TryGetValue(functionCallIndex, out var arguments))
            {
                _functionArgumentBuildersByIndex[functionCallIndex] = arguments = new StringBuilder();
            }

            arguments.Append(argumentsUpdate);
            return true;
        }

        private string GetFunctionCallIndex(StreamingFunctionCallUpdateContent update)
        {
            var streamingIndex = $"{update.RequestIndex}-{update.FunctionCallIndex}";

            if (update.CallId is not { Length: > 0 } callId)
            {
                return streamingIndex;
            }

            if (_functionCallIndexesById?.TryGetValue(callId, out var existingIndex) == true)
            {
                return existingIndex;
            }

            if (_functionCallIdsByIndex?.TryGetValue(streamingIndex, out var existingCallId) == true &&
                existingCallId != callId)
            {
                // Some non-incremental adapters do not provide a distinct FunctionCallIndex.
                // Preserve those simultaneous full tool calls by falling back to CallId.
                var callIdIndex = $"id:{callId}";
                (_functionCallIndexesById ??= [])[callId] = callIdIndex;
                return callIdIndex;
            }

            (_functionCallIndexesById ??= [])[callId] = streamingIndex;
            return streamingIndex;
        }
    }

    [JsonSerializable(typeof(KernelArguments))]
    private sealed partial class FunctionCallContentBuilderJsonSerializerContext : JsonSerializerContext;
}