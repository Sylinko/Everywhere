using System.Text.Json.Serialization;

namespace Everywhere.Cloud;

/// <summary>
/// A common subscription status enum that can be used for representing the status of various subscription services.
/// It is designed to be flexible and cover common scenarios such as active subscriptions, trial periods, cancellations, unpaid statuses, and expired subscriptions.
/// This enum can be used across different services to maintain consistency in how subscription statuses are represented and handled.
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>
    /// Active subscription, the user can use the subscription service normally.
    /// </summary>
    /// <remarks>
    /// Stripe: active with cancel_at_period_end=false
    /// </remarks>
    [JsonPropertyName("active")]
    Active = 0,

    /// <summary>
    /// In trial period, the user is enjoying the subscription service during the trial period.
    /// After the trial period ends, it will automatically transition to either Active or Canceled status.
    /// </summary>
    /// <remarks>
    /// Stripe: trialing
    /// </remarks>
    [JsonPropertyName("trialing")]
    Trialing = 1,

    /// <summary>
    /// The current subscription is not expired yet, but the user has canceled the subscription.
    /// The user can continue to use the subscription service until the current subscription expires.
    /// </summary>
    /// <remarks>
    /// Stripe: active with cancel_at_period_end=true
    /// </remarks>
    [JsonPropertyName("canceling")]
    Canceling = 2,

    /// <summary>
    /// Subscription is unpaid, the user needs to resolve payment issues, and cannot use the subscription service until the payment issue is resolved.
    /// </summary>
    /// <remarks>
    /// Stripe: incomplete, incomplete_expired, past_due, unpaid
    /// </remarks>
    [JsonPropertyName("unpaid")]
    Unpaid = 3,

    /// <summary>
    /// The user had a subscription before but currently does not have an active subscription, and cannot be trialed again.
    /// </summary>
    [JsonPropertyName("expired")]
    Expired = 4
}