// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.SemanticKernel.Connectors.MistralAI.Client;

/// <summary>
/// Chat message for MistralAI.
/// </summary>
internal sealed class MistralChatMessage
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Content { get; set; }

    internal string? GetTextContent() => GetContent(this.Content).Text;

    internal string? GetReasoningContent()
    {
        var (_, reasoning) = GetContent(this.Content);
        return string.IsNullOrWhiteSpace(reasoning) ? this.ReasoningContent : reasoning;
    }

    internal static (string? Text, string? Reasoning) GetContent(object? content)
    {
        if (content is not JsonElement element)
        {
            return (content?.ToString(), null);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return (element.GetString(), null);
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        var text = (StringBuilder?)null;
        var reasoning = (StringBuilder?)null;
        foreach (var item in element.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out var typeProperty))
            {
                continue;
            }

            if (typeProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (typeProperty.GetString())
            {
                case "text" when item.TryGetProperty("text", out var textProperty) && textProperty.ValueKind == JsonValueKind.String:
                    (text ??= new StringBuilder()).Append(textProperty.GetString());
                    break;
                case "thinking" when item.TryGetProperty("thinking", out var thinkingProperty) && thinkingProperty.ValueKind == JsonValueKind.Array:
                    foreach (var thinkingItem in thinkingProperty.EnumerateArray())
                    {
                        if (thinkingItem.TryGetProperty("type", out var thinkingTypeProperty) &&
                            thinkingTypeProperty.ValueKind == JsonValueKind.String &&
                            thinkingTypeProperty.GetString() == "text" &&
                            thinkingItem.TryGetProperty("text", out var reasoningTextProperty) &&
                            reasoningTextProperty.ValueKind == JsonValueKind.String)
                        {
                            (reasoning ??= new StringBuilder()).Append(reasoningTextProperty.GetString());
                        }
                    }
                    break;
            }
        }

        return (text?.ToString(), reasoning?.ToString());
    }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<MistralToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningContent { get; set; }

    /// <summary>
    /// Construct an instance of <see cref="MistralChatMessage"/>.
    /// </summary>
    /// <param name="role">If provided must be one of: system, user, assistant</param>
    /// <param name="content">Content of the chat message</param>
    [JsonConstructor]
    internal MistralChatMessage(string? role, object? content)
    {
        if (role is not null and not "system" and not "user" and not "assistant" and not "tool")
        {
            throw new System.ArgumentException($"Role must be one of: system, user, assistant or tool. {role} is an invalid role.", nameof(role));
        }

        this.Role = role;
        this.Content = content;
    }
}
