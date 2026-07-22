using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat.Permissions;

namespace Everywhere.Chat.Plugins;

public abstract class ChatPluginUserInterfaceItem;

public abstract class ChatPluginUserInterfaceItem<TResult> : ChatPluginUserInterfaceItem
{
    public IRelayCommand<TResult> Command { get; }

    public Task<TResult> Task => _promise.Task;

    private readonly TaskCompletionSource<TResult> _promise = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected ChatPluginUserInterfaceItem(CancellationToken cancellationToken)
    {
        Command = new RelayCommand<TResult>(r =>
        {
            if (r is not null) _promise.TrySetResult(r);
        });
        cancellationToken.Register(() => _promise.TrySetCanceled());
    }
}

/// <summary>
/// Raised by <see cref="IChatPluginUserInterface"/> when a plugin needs to request the user's consent for a certain action, such as granting permissions or confirming a function call.
/// The message contains a <see cref="TaskCompletionSource{ConsentDecision}"/> that the UI can use to return the user's decision asynchronously.
/// The UI should also display the provided header and display block to inform the user about what they are consenting to.
/// The operation can be canceled using the provided <see cref="CancellationToken"/>.
/// </summary>
public sealed class ChatPluginUserInterfaceConsentRequestItem(
    IDynamicLocaleKey headerKey,
    ChatPluginDisplayBlock? displayBlock,
    RequestConsentRememberMasks rememberMasks,
    IReadOnlyList<RequestConsentCustomOption>? customOptions,
    CancellationToken cancellationToken
) : ChatPluginUserInterfaceItem<ConsentDecision>(cancellationToken)
{
    public IDynamicLocaleKey HeaderKey { get; } = headerKey;

    public ChatPluginDisplayBlock? DisplayBlock { get; } = displayBlock;

    public RequestConsentRememberMasks RememberMasks { get; } = rememberMasks;

    public IReadOnlyList<RequestConsentCustomOption>? CustomOptions { get; } = customOptions;
}

/// <summary>
/// Raised by <see cref="IChatPluginUserInterface"/> when a plugin needs to ask the user a question and get the answer asynchronously.
/// The message contains a Promise that the UI can use to return the user's answers asynchronously.
/// The UI should display the provided list of questions to the user and collect their answers.
/// The operation can be canceled using the provided <see cref="CancellationToken"/>.
/// </summary>
/// <param name="questions"></param>
/// <param name="cancellationToken"></param>
public sealed class ChatPluginUserInterfaceAskQuestionItem(
    IReadOnlyList<ChatPluginQuestion> questions,
    CancellationToken cancellationToken
) : ChatPluginUserInterfaceItem<IReadOnlyList<ChatPluginQuestionAnswer>>(cancellationToken)
{
    public IReadOnlyList<ChatPluginQuestion> Questions { get; } = questions;
}