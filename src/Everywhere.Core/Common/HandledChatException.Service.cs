using Everywhere.AI;

namespace Everywhere.Common;

public abstract partial class HandledChatException
{
    /// <summary>Represents AuthenticationFailure failures.</summary>
    public class AuthenticationFailure(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_AuthenticationFailure);

        /// <summary>Represents InvalidApiKey failures within AuthenticationFailure.</summary>
        public sealed class InvalidApiKey(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : AuthenticationFailure(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey =>
                new DynamicLocaleKey(LocaleKey.HandledChatException_AuthenticationFailure_InvalidApiKey);
        }

        /// <summary>Represents LoginRequired failures within AuthenticationFailure.</summary>
        public sealed class LoginRequired(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : AuthenticationFailure(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledSystemException_UserNotLogin);
        }
    }

    /// <summary>Represents PermissionDenied failures.</summary>
    public class PermissionDenied(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_PermissionDenied);

        /// <summary>Represents RegionRestricted failures within PermissionDenied.</summary>
        public sealed class RegionRestricted(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : PermissionDenied(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_RegionNotSupport);
        }
    }

    /// <summary>Represents Timeout failures.</summary>
    public sealed class Timeout(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null,
        TimeSpan? retryAfter = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_Timeout);

        /// <inheritdoc />
        public override ChatExceptionRecovery Recovery { get; } = new ChatExceptionRecovery.Retry(retryAfter);
    }

    /// <summary>Represents ServiceUnavailable failures.</summary>
    public sealed class ServiceUnavailable(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null,
        TimeSpan? retryAfter = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_ServiceUnavailable);

        /// <inheritdoc />
        public override ChatExceptionRecovery Recovery { get; } = new ChatExceptionRecovery.Retry(retryAfter);
    }

    /// <summary>Represents RateLimited failures.</summary>
    public sealed class RateLimited(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null,
        TimeSpan? retryAfter = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey =>
            retryAfter is { Ticks: > 0 } delay ?
                new FormattedDynamicLocaleKey(
                    LocaleKey.HandledChatException_RateLimit_RetryAfter,
                    new DirectLocaleKey(Math.Ceiling(delay.TotalSeconds))) :
                new DynamicLocaleKey(LocaleKey.HandledChatException_RateLimit);

        /// <inheritdoc />
        public override ChatExceptionRecovery Recovery { get; } = new ChatExceptionRecovery.Retry(retryAfter);
    }

    /// <summary>Represents QuotaExceeded failures.</summary>
    public sealed class QuotaExceeded(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_QuotaExceeded);
    }

    /// <summary>Represents ContentBlocked failures.</summary>
    public sealed class ContentBlocked(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_ContentBlocked);
    }

    /// <summary>Represents Canceled failures.</summary>
    public class Canceled(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_OperationCanceled);

        /// <summary>Represents ByCaller failures within Canceled.</summary>
        public sealed class ByCaller(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : Canceled(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_OperationCanceled);

            /// <inheritdoc />
            public override ChatExceptionRecovery Recovery => new ChatExceptionRecovery.Canceled();
        }
    }

    /// <summary>Represents Unknown failures.</summary>
    public sealed class Unknown(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_Unknown);

        /// <inheritdoc />
        public override bool IsExpected => false;
    }
}