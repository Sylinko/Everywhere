using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat.Permissions;

/// <summary>Contains the task evidence for one approval, independent of provider message formats.</summary>
public sealed record ToolApprovalInput
{
    [JsonPropertyName("ENVIRONMENT")]
    public required ToolApprovalEnvironment Environment { get; init; }

    [JsonPropertyName("EXECUTION_CONSTRAINTS")]
    public required string ExecutionConstraints { get; init; }

    [JsonPropertyName("EFFECTIVE_HISTORY")]
    public required IReadOnlyList<ToolApprovalHistoryEntry> EffectiveHistory { get; init; }

    [JsonPropertyName("PENDING_ACTION")]
    public required ToolApprovalAction PendingAction { get; init; }

    [JsonPropertyName("CONSENT_SCOPE")]
    public ToolApprovalScope? ConsentScope { get; init; }
}

/// <summary>Provides the directory used to resolve relative task resources.</summary>
public sealed record ToolApprovalEnvironment(string WorkingDirectory);

/// <summary>Represents a selected instruction, checkpoint, attachment fact, or completed call fact.</summary>
public sealed record ToolApprovalHistoryEntry
{
    public required string Role { get; init; }
    public string? Content { get; init; }
    public ToolApprovalAttachmentFact? Attachment { get; init; }
    public string? Tool { get; init; }
    public KernelArguments? Arguments { get; init; }
}

/// <summary>Provides existing textual attachment information without opening or ingesting attachments.</summary>
public sealed record ToolApprovalAttachmentFact
{
    public string? FilePath { get; init; }
    public string? MimeType { get; init; }
    public string? Description { get; init; }
    public string? Text { get; init; }
    public bool? IsTextIncomplete { get; init; }
    public string? Header { get; init; }
}

/// <summary>Provides the exact pending function definition and original arguments.</summary>
public sealed record ToolApprovalAction
{
    public required string PluginKey { get; init; }
    public required string ToolName { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<ToolApprovalParameter> Parameters { get; init; }
    public KernelArguments? Arguments { get; init; }
}

/// <summary>Describes one parameter of the pending function.</summary>
public sealed record ToolApprovalParameter(string Name, string? Description, bool IsRequired, JsonElement? Schema, JsonElement? DefaultValue);

/// <summary>Provides the concrete shell selected before command consent.</summary>
public sealed record ToolApprovalShellScope(string ShellPath, string ShellType, string Command, string Description);

/// <summary>Provides resolved filesystem targets before an internal consent gate.</summary>
public sealed record ToolApprovalFileScope(IReadOnlyList<string> Paths, bool RequiresExplicitApproval);

/// <summary>Provides the assistant's explanation for a pending UI action batch.</summary>
public sealed record ToolApprovalUiScope(string Description);

/// <summary>Serializes the explicit approval input contract and built-in consent scopes.</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolApprovalInput))]
[JsonSerializable(typeof(ToolApprovalShellScope))]
[JsonSerializable(typeof(ToolApprovalFileScope))]
[JsonSerializable(typeof(ToolApprovalUiScope))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(byte[]))]
public sealed partial class ToolApprovalInputJsonSerializerContext : JsonSerializerContext
{
    /// <summary>Writes prompt JSON without expanding ordinary Unicode text into escape sequences.</summary>
    public static ToolApprovalInputJsonSerializerContext ForPrompt { get; } = new(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
}