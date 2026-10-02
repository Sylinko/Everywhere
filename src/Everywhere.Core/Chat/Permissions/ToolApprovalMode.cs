using System.ComponentModel;
using System.Text.Json.Serialization;
using Everywhere.Configuration;

namespace Everywhere.Chat.Permissions;

/// <summary>Determines how required tool approvals are obtained.</summary>
[TypeConverter(typeof(FallbackEnumConverter))]
public enum ToolApprovalMode
{
    [JsonStringEnumMemberName("Ask")]
    Ask,

    [JsonStringEnumMemberName("Auto")]
    Auto,

    [JsonStringEnumMemberName("FullAccess")]
    FullAccess
}