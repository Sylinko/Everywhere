using Everywhere.AI;

namespace Everywhere.Common;

public abstract partial class HandledChatException
{
    /// <summary>Represents InvalidRequest failures.</summary>
    public class InvalidRequest(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_InvalidRequest;

        /// <summary>Represents ContextLengthExceeded failures within InvalidRequest.</summary>
        public sealed class ContextLengthExceeded(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidRequest(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_ContextLengthExceeded;

            /// <inheritdoc />
            public override ChatExceptionRecovery Recovery => new ChatExceptionRecovery.RecoverContext();
        }

        /// <summary>Represents InvalidThoughtSignature failures within InvalidRequest.</summary>
        public sealed class InvalidThoughtSignature(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidRequest(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_InvalidThoughtSignature;
        }

        /// <summary>Represents InvalidReasoningContent failures within InvalidRequest.</summary>
        public sealed class InvalidReasoningContent(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : InvalidRequest(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_InvalidReasoningContent;
        }
    }

    /// <summary>Represents UnsupportedCapability failures.</summary>
    public class UnsupportedCapability(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_FeatureNotSupport;

        /// <summary>Represents Tools failures within UnsupportedCapability.</summary>
        public sealed class Tools(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : UnsupportedCapability(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_UnsupportedCapability_Tools;
        }

        /// <summary>Represents Images failures within UnsupportedCapability.</summary>
        public sealed class Images(
            Exception originalException,
            IDynamicLocaleKey? customFriendlyMessageKey = null
        ) : UnsupportedCapability(originalException, customFriendlyMessageKey)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_ImageNotSupport;
        }
    }
}