using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace Everywhere.AI;

public enum ModelPricingUnit
{
    [DynamicLocaleKey(LocaleKey.ModelPricingUnit_MCreditPerMToken)]
    MCreditPerMToken,
    [DynamicLocaleKey(LocaleKey.ModelPricingUnit_UsdPerMToken)]
    UsdPerMToken
}

/// <summary>
/// Represents the pricing structure of an AI model, which may include multiple tiers based on usage thresholds.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class ModelPricing(IReadOnlyList<PricingTier> tiers, ModelPricingUnit unit)
{
    [Key(0)]
    private readonly IReadOnlyList<PricingTier> _tiers = tiers;

    [Key(1)]
    public ModelPricingUnit Unit { get; } = unit;

    /// <summary>
    /// Gets a I18N description for input
    /// </summary>
    /// <example>
    /// 12.00 美元
    /// </example>
    /// <example>
    /// 12.00 美元，Token 数 &lt;= 100,000,000
    /// 18.00 美元，100,000,000 &lt; Token 数 &lt;= 500,000,000
    /// 30.00 美元，Token 数 &gt; 500,000,000
    /// </example>
    [IgnoreMember]
    [field: AllowNull, MaybeNull]
    public IDynamicLocaleKey InputDescriptionKey => field ??= GetDescriptionKey(tp => tp.Input);

    [field: AllowNull, MaybeNull]
    public IDynamicLocaleKey OutputDescriptionKey => field ??= GetDescriptionKey(tp => tp.Output);

    private IDynamicLocaleKey GetDescriptionKey(Func<TokenPricing, double> selector)
    {
        switch (_tiers.Count)
        {
            case 0:
            {
                return DirectLocaleKey.Empty;
            }
            case 1:
            {
                return GetPricingKey(_tiers[0]);
            }
            default:
            {
                var keys = new FormattedDynamicLocaleKey[_tiers.Count];

                keys[0] = new FormattedDynamicLocaleKey(
                    LocaleKey.ModelPricing_NotGreaterThanTierDescription,
                    GetPricingKey(_tiers[0]),
                    GetThresholdKey(_tiers[1]));

                for (var i = 1; i < _tiers.Count - 1; i++)
                {
                    keys[i] = new FormattedDynamicLocaleKey(
                        LocaleKey.ModelPricing_BetweenTiersDescription,
                        GetPricingKey(_tiers[i]),
                        GetThresholdKey(_tiers[i]),
                        GetThresholdKey(_tiers[i + 1]));
                }

                keys[^1] = new FormattedDynamicLocaleKey(
                    LocaleKey.ModelPricing_GreaterThanTierDescription,
                    GetPricingKey(_tiers[^1]),
                    GetThresholdKey(_tiers[^1]));

                return new AggregateDynamicLocaleKey(keys, "\n");
            }
        }

        FormattedDynamicLocaleKey GetPricingKey(PricingTier tier) => new(
            Unit switch
            {
                ModelPricingUnit.MCreditPerMToken => LocaleKey.ModelPricingUnit_MCreditPerMToken,
                ModelPricingUnit.UsdPerMToken => LocaleKey.ModelPricingUnit_UsdPerMToken,
                _ => LocaleKey.Empty
            },
            new DirectLocaleKey(selector(tier.Pricing).ToString("N")));

        DirectLocaleKey GetThresholdKey(PricingTier tier) => new(tier.Threshold.ToString("N0"));
    }
}

/// <summary>
/// Represents a single pricing tier, which includes a usage threshold and the corresponding token pricing for that tier.
/// </summary>
/// <param name="Threshold"></param>
/// <param name="Pricing"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record PricingTier(
    [property: Key(0)] long Threshold,
    [property: Key(1)] TokenPricing Pricing
);

/// <summary>
/// Represents the token pricing for a specific tier, including the cost per token for input and output, as well as the cached input tokens.
/// Unit is USD per MTokens
/// </summary>
/// <param name="Input"></param>
/// <param name="Output"></param>
/// <param name="CachedInput"></param>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record TokenPricing(
    [property: Key(0)] double Input,
    [property: Key(1)] double Output,
    [property: Key(2)] double CachedInput
);