namespace Everywhere.AI;

/// <summary>Runtime diagnostic evidence accompanying a concrete classified exception.</summary>
public sealed class ChatExceptionDiagnostics(ChatExceptionEvidence evidence, string ruleId, string source)
{
    /// <summary>Gets the original request evidence.</summary>
    public ChatExceptionEvidence Evidence { get; } = evidence;

    /// <summary>Gets the rule that selected the final exception type.</summary>
    public string RuleId { get; } = ruleId;

    /// <summary>Gets the source of the selected evidence.</summary>
    public string Source { get; } = source;

    /// <summary>Gets the associated assistant's model identifier for diagnostics.</summary>
    public string? ModelId { get; internal set; }
}