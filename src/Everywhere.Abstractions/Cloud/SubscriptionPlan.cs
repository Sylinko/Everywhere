using System.Text.Json.Serialization;

namespace Everywhere.Cloud;

public enum SubscriptionPlan
{
    [JsonPropertyName("banned")]
    Banned = -1,

    [JsonPropertyName("free")]
    Free = 0,

    [JsonPropertyName("starter")]
    Starter = 1,

    [JsonPropertyName("plus")]
    Plus = 2,

    [JsonPropertyName("pro")]
    Pro = 3
}