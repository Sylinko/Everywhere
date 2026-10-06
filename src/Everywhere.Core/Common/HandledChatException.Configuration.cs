namespace Everywhere.Common;

public abstract partial class HandledChatException
{
    /// <summary>Represents InvalidConfiguration failures.</summary>
    public class InvalidConfiguration(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_InvalidConfiguration);

        /// <summary>Represents InvalidEndpoint failures within InvalidConfiguration.</summary>
        public sealed class InvalidEndpoint(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidConfiguration(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_InvalidEndpoint);
        }

        /// <summary>Represents ModelUnavailable failures within InvalidConfiguration.</summary>
        public sealed class ModelUnavailable(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidConfiguration(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_InvalidConfiguration_ModelUnavailable);
        }

        /// <summary>Represents InvalidTemperature failures within InvalidConfiguration.</summary>
        public sealed class InvalidTemperature(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidConfiguration(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_TemperatureNotSupport);
        }

        /// <summary>Represents InvalidTopP failures within InvalidConfiguration.</summary>
        public sealed class InvalidTopP(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidConfiguration(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override IDynamicLocaleKey DefaultFriendlyMessageKey => new DynamicLocaleKey(LocaleKey.HandledChatException_TopPNotSupport);
        }
    }
}