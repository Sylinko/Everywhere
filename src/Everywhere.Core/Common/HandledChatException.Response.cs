namespace Everywhere.Common;

public abstract partial class HandledChatException
{
    /// <summary>Generation reached a token limit; identical input is not automatically retried.</summary>
    public class GenerationLimitExceeded(Exception originalException) : HandledChatException(originalException)
    {
        /// <inheritdoc />
        protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_GenerationLimitExceeded;

        /// <summary>Generation exhausted the context window, rather than rejecting oversized input.</summary>
        public sealed class ContextWindowExceeded(Exception originalException) : GenerationLimitExceeded(originalException)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_GenerationLimitExceeded_ContextWindow;
        }
    }

    /// <summary>Represents InvalidResponse failures.</summary>
    public class InvalidResponse(
        Exception originalException,
        IDynamicLocaleKey? customFriendlyMessageKey = null
    ) : HandledChatException(originalException, customFriendlyMessageKey)
    {
        /// <inheritdoc />
        protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_InvalidResponse;

        /// <summary>The provider explicitly reported an interrupted or incomplete generation.</summary>
        public sealed class Incomplete(Exception originalException) : InvalidResponse(originalException)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_IncompleteResponse;
        }

        /// <summary>The provider requires a continuation protocol that this caller does not support.</summary>
        public sealed class ContinuationRequired(Exception originalException) : InvalidResponse(originalException)
        {
            /// <inheritdoc />
            protected override string DefaultFriendlyMessageKey => LocaleKey.HandledChatException_ContinuationRequired;
        }

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