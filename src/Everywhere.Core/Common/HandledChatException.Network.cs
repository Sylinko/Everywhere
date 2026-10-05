using Everywhere.AI;

namespace Everywhere.Common;

public abstract partial class HandledChatException
{
    /// <summary>Represents NetworkError failures.</summary>
    public class NetworkError(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null,
        TimeSpan? retryAfter = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_NetworkError;

        /// <inheritdoc />
        public override ChatExceptionRecovery Recovery { get; } = new ChatExceptionRecovery.Retry(retryAfter);

        /// <summary>Represents HostNotFound failures within NetworkError.</summary>
        public sealed class HostNotFound(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null,
            TimeSpan? retryAfter = null
        ) : NetworkError(originalException, customFriendlyMessageKey, retryAfter)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledSystemException_HostNotFound;
        }

        /// <summary>Represents ConnectionRefused failures within NetworkError.</summary>
        public sealed class ConnectionRefused(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null,
            TimeSpan? retryAfter = null
        ) : NetworkError(originalException, customFriendlyMessageKey, retryAfter)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledSystemException_ConnectionRefused;
        }

        /// <summary>Represents TlsError failures within NetworkError.</summary>
        public sealed class TlsError(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : NetworkError(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledSystemException_SSLConnectionError;

            /// <inheritdoc />
            public override ChatExceptionRecovery Recovery => new ChatExceptionRecovery.Stop();
        }

        /// <summary>Represents ProxyTunnelRejected failures within NetworkError.</summary>
        public sealed class ProxyTunnelRejected(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : NetworkError(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_NetworkError_ProxyTunnelRejected;

            /// <inheritdoc />
            public override ChatExceptionRecovery Recovery => new ChatExceptionRecovery.Stop();
        }
    }
}