using Everywhere.AI;

namespace Everywhere.Common;

/// <summary>A classified LLM failure, retaining its original cause and optional diagnostics.</summary>
/// <remarks>Concrete nested types define categories and default localized messages. Exceptions are runtime-only.</remarks>
public abstract partial class HandledChatException(
    Exception originalException,
    IDynamicLocaleKey? customFriendlyMessageKey = null
) : HandledException(originalException)
{
    /// <inheritdoc />
    public override bool IsExpected => true;

    /// <inheritdoc />
    public override IDynamicLocaleKey FriendlyMessageKey => field ??= customFriendlyMessageKey ?? new DynamicLocaleKey(DefaultFriendlyMessageKey);

    /// <summary>Gets the default localization key supplied by the concrete category.</summary>
    protected abstract string DefaultFriendlyMessageKey { get; }

    /// <summary>Advises the caller how this failure can be recovered; does not schedule or execute recovery.</summary>
    public virtual ChatExceptionRecovery Recovery => new ChatExceptionRecovery.Stop();

    /// <summary>Gets request evidence and the rules used to select this category.</summary>
    public ChatExceptionDiagnostics? Diagnostics { get; set; }
}