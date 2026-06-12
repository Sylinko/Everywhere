using System.Text.Json.Serialization;
using Everywhere.Serialization;

namespace Everywhere.Cloud;

public sealed record SubscriptionInformation(
    [property: JsonPropertyName("plan"), JsonConverter(typeof(JsonStringEnumConverter<SubscriptionPlan>))]
    SubscriptionPlan Plan,
    [property: JsonPropertyName("planCredits")] long RemainingPlanCredits,
    [property: JsonPropertyName("totalPlanCredits")] long TotalPlanCredits,
    [property: JsonPropertyName("bonusCredits")] long BonusCredits,
    [property: JsonPropertyName("periodStart"), JsonConverter(typeof(JsonISOStringDateTimeOffsetFormatter))]
    DateTimeOffset? PeriodStart,
    [property: JsonPropertyName("periodEnd"), JsonConverter(typeof(JsonISOStringDateTimeOffsetFormatter))]
    DateTimeOffset? PeriodEnd,
    [property: JsonPropertyName("status"), JsonConverter(typeof(JsonStringEnumConverter<SubscriptionStatus>))]
    SubscriptionStatus? Status,
    [property: JsonPropertyName("freeWebSearchCount")] int UsedFreeWebSearchCount,
    [property: JsonPropertyName("totalFreeWebSearchCount")] int TotalFreeWebSearchCount,
    [property: JsonPropertyName("quotaLimit")] QuotaLimitInformation? QuotaLimit
)
{
    [JsonIgnore]
    public double RemainingPlanCreditsRatio => TotalPlanCredits > 0 ? (double)RemainingPlanCredits / TotalPlanCredits : 0;

    [JsonIgnore]
    public int RemainingFreeWebSearchCount => TotalFreeWebSearchCount - UsedFreeWebSearchCount;

    [JsonIgnore]
    public double RemainingFreeWebSearchRatio => TotalFreeWebSearchCount > 0 ? 1d - (double)UsedFreeWebSearchCount / TotalFreeWebSearchCount : 0;

    [JsonIgnore]
    public int ExpiredDaysAgo
    {
        get
        {
            if (!PeriodEnd.HasValue) return 0;
            var expiredDays = (DateTimeOffset.UtcNow - PeriodEnd.Value).TotalDays;
            return expiredDays > 0 ? (int)expiredDays : 0;
        }
    }
}

public sealed record QuotaLimitInformation(
    [property: JsonPropertyName("fiveHour")] QuotaLimitWindowSummary FiveHour,
    [property: JsonPropertyName("sevenDay")] QuotaLimitWindowSummary SevenDay
);

public sealed record QuotaLimitWindowSummary(
    [property: JsonPropertyName("remainingPercent")] double RemainingPercent,
    [property: JsonPropertyName("resetAt"), JsonConverter(typeof(JsonISOStringDateTimeOffsetFormatter))]
    DateTimeOffset? ResetAt
);

[JsonSerializable(typeof(ApiPayload<SubscriptionInformation>))]
public sealed partial class SubscriptionInformationJsonSerializerContext : JsonSerializerContext;