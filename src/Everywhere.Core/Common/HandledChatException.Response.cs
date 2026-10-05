namespace Everywhere.Common;

public abstract partial class HandledChatException
{
    /// <summary>Represents InvalidResponse failures.</summary>
    public class InvalidResponse(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_InvalidResponse;

        /// <summary>Represents EmptyResponse failures within InvalidResponse.</summary>
        public sealed class EmptyResponse(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidResponse(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_EmptyResponse;
        }

        /// <summary>Represents MalformedJson failures within InvalidResponse.</summary>
        public sealed class MalformedJson(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidResponse(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_JsonError;
        }

        /// <summary>Represents UnsupportedFormat failures within InvalidResponse.</summary>
        public sealed class UnsupportedFormat(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidResponse(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_ResponseCompatibility;
        }
    }
}