using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Everywhere.Chat.Permissions;

/// <summary>Identifies why automatic approval could not produce a decision.</summary>
public enum ToolApprovalFailure
{
    DecisionMissing,
    ReadLimitExceeded,
    ContextLimitExceeded,
    InvalidResponse,
    AssistantUnavailable,
    ProviderError
}

/// <summary>Describes the operation resolved by a tool at an internal consent gate.</summary>
public sealed record ToolApprovalScope(string Operation, JsonElement? Details = null)
{
    public static ToolApprovalScope Create<T>(string operation, T details, JsonTypeInfo<T>? typeInfo = null)
    {
        var element = typeInfo is null ? JsonSerializer.SerializeToElement(details) : JsonSerializer.SerializeToElement(details, typeInfo);
        return new ToolApprovalScope(operation, element);
    }
}

/// <summary>Separates a model refusal from failure of the approval workflow.</summary>
public readonly record struct ToolApprovalResult(bool IsAllowed, string Reason, ToolApprovalFailure? Failure = null)
{
    public static ToolApprovalResult Allow(string reason) => new(true, reason);
    public static ToolApprovalResult Deny(string reason) => new(false, reason);
    public static ToolApprovalResult Fail(ToolApprovalFailure failure, string reason) => new(false, reason, failure);

    /// <summary>Formats a concise tool result without exposing the private review conversation.</summary>
    public string FormatReason() => Failure is { } failure ?
        $"Automatic approval failed ({failure}): {Reason}" :
        $"Automatic approval denied this operation: {Reason}";
}