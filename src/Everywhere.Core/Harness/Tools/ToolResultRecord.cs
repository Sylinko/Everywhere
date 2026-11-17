namespace Everywhere.Harness;

/// <summary>
/// Permanent result discriminators in chat v2 storage.
/// </summary>
public enum ToolResultKind
{
    Text = 0,
    Attachment = 1,
    PromptNode = 2,
    StructuredResult = 3,
    Json = 4,
    ContentBlocks = 5
}

/// <summary>
/// Represents a recorded tool outcome, including structured documents and attachments
/// that survive reload without being flattened into text for a particular model.
/// </summary>
public sealed record ToolResultRecord(string? FunctionName, string? PluginName, string? CallId, object? Result)
{
    /// <summary>
    /// Gets optional metadata associated with this result.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// Gets stable zero-based descriptors for managed files referenced by this result.
    /// </summary>
    public ToolResultFile[] Files { get; init; } = [];
}
