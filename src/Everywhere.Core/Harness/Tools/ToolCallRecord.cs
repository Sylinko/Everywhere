namespace Everywhere.Harness;

/// <summary>
/// Represents a durable tool invocation in a conversation.
/// Older conversations can lack a call ID or plugin name; keep those values absent
/// instead of manufacturing an identity during deserialization.
/// </summary>
public sealed record ToolCallRecord(
    string FunctionName,
    string? PluginName,
    string? Id,
    IReadOnlyDictionary<string, object?>? Arguments)
{
    /// <summary>
    /// Gets provider or dialect fields associated with this call.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
