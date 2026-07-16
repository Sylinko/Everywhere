using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using LiveMarkdown.Avalonia;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class ChatMessageItemsControl : ItemsControl
{
    /// <summary>
    /// Defines the <see cref="ChatContext"/> property.
    /// </summary>
    public static readonly StyledProperty<ChatContext?> ChatContextProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, ChatContext?>(nameof(ChatContext));

    /// <summary>
    /// Gets or sets the chat context whose selected branch is projected incrementally.
    /// </summary>
    public ChatContext? ChatContext
    {
        get => GetValue(ChatContextProperty);
        set => SetValue(ChatContextProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsReadonly"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsReadonlyProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, bool>(nameof(IsReadonly));

    /// <summary>
    /// Gets or sets a value indicating whether the control is in read-only mode.
    /// </summary>
    public bool IsReadonly
    {
        get => GetValue(IsReadonlyProperty);
        set => SetValue(IsReadonlyProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="SupportedModalities"/> property.
    /// </summary>
    public static readonly StyledProperty<Modalities> SupportedModalitiesProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, Modalities>(nameof(SupportedModalities));

    /// <summary>
    /// Gets or sets the modalities supported by this control. This can be used to determine which
    /// types of content (for example text, images, or videos) can be displayed or interacted with.
    /// </summary>
    public Modalities SupportedModalities
    {
        get => GetValue(SupportedModalitiesProperty);
        set => SetValue(SupportedModalitiesProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="CopyMessageCommand"/> property, which is a command that can be used to copy a chat message.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessage>?> CopyMessageCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessage>?>(nameof(CopyMessageCommand));

    /// <summary>
    /// Gets or sets the command that can be used to copy a chat message. This command can be bound to UI elements to provide functionality for copying messages.
    /// </summary>
    public IRelayCommand<ChatMessage>? CopyMessageCommand
    {
        get => GetValue(CopyMessageCommandProperty);
        set => SetValue(CopyMessageCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="EditMessageNodeCommand"/> property, which is a command that can be used to edit a chat message node.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessageNode>?> EditMessageNodeCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessageNode>?>(nameof(EditMessageNodeCommand));

    /// <summary>
    /// Gets or sets the command that can be used to edit a chat message node. This command can be bound to UI elements to provide functionality for editing message nodes.
    /// </summary>
    public IRelayCommand<ChatMessageNode>? EditMessageNodeCommand
    {
        get => GetValue(EditMessageNodeCommandProperty);
        set => SetValue(EditMessageNodeCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="RetryMessageNodeCommand"/> property, which is a command that can be used to retry a chat message node.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessageNode>?> RetryMessageNodeCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessageNode>?>(nameof(RetryMessageNodeCommand));

    /// <summary>
    /// Gets or sets the command that can be used to retry a chat message node. This command can be bound to UI elements to provide functionality for retrying message nodes.
    /// </summary>
    public IRelayCommand<ChatMessageNode>? RetryMessageNodeCommand
    {
        get => GetValue(RetryMessageNodeCommandProperty);
        set => SetValue(RetryMessageNodeCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="ContinueMessageNodeCommand"/> property, which is a command that can be used to continue a chat message node.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessageNode>?> ContinueMessageNodeCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessageNode>?>(nameof(ContinueMessageNodeCommand));

    /// <summary>
    /// Gets or sets the command that can be used to continue a chat message node. This command can be bound to UI elements to provide functionality for continuing message nodes.
    /// </summary>
    public IRelayCommand<ChatMessageNode>? ContinueMessageNodeCommand
    {
        get => GetValue(ContinueMessageNodeCommandProperty);
        set => SetValue(ContinueMessageNodeCommandProperty, value);
    }

    public static readonly StyledProperty<ChatMessageNode?> EditingMessageNodeProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, ChatMessageNode?>(nameof(EditingMessageNode));

    public ChatMessageNode? EditingMessageNode
    {
        get => GetValue(EditingMessageNodeProperty);
        set => SetValue(EditingMessageNodeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowStatisticsProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, bool>(nameof(ShowStatistics));

    public bool ShowStatistics
    {
        get => GetValue(ShowStatisticsProperty);
        set => SetValue(ShowStatisticsProperty, value);
    }

    static ChatMessageItemsControl()
    {
        ChatContextProperty.Changed.AddClassHandler<ChatMessageItemsControl>((control, _) => control.ResetItemsSource());
    }

    private void ResetItemsSource()
    {
        // ChatContext owns the projection companion. Detaching a view therefore releases only its
        // binding, not the rows' presentation state; attaching another view to the same context
        // receives the same IReadOnlyBindableList and stable row instances.
        SetCurrentValue(ItemsSourceProperty, ChatContext?.Presentation.Rows);
    }

    /// <summary>
    /// Opens a URL surfaced by either a lightweight activity preview or a detailed plugin display
    /// block. Both presentations belong to this chat root, so the command is intentionally hosted
    /// here instead of being duplicated by individual presenters.
    /// </summary>
    [RelayCommand]
    private static Task<bool> OpenUrlAsync(object? value)
    {
        var uri = value switch
        {
            Uri u => u,
            LinkClickedEventArgs e => e.HRef,
            _ when Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var u) => u,
            _ => null,
        };

        // TODO: schema?
        return uri is not { Scheme: "http" or "https" or "file" } ? Task.FromResult(false) : App.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>
    /// Opens a subagent conversation in its independent dialog. A nested subagent view creates its
    /// own <see cref="ChatMessageItemsControl"/>, so recursive subagent conversations retain the
    /// same command boundary without coupling either display-block presenter to dialog services.
    /// </summary>
    [RelayCommand]
    private void OpenSubagent(ChatPluginSubagentDisplayBlock? block)
    {
        if (block is null) return;

        DialogManager
            .CreateCustomDialog(
                new ChatSubagentView
                {
                    ChatContext = block.ChatContext
                })
            .Dismissible()
            .Show();
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is ChatPresentationRow)
        {
            recycleKey = typeof(ChatPresentationRowPresenter);
            return true;
        }

        // The projection is the only supported source for this control.  Let the base
        // ItemsControl handle an unexpected value rather than reviving the old raw-node
        // compatibility path.
        return base.NeedsContainerOverride(item, index, out recycleKey);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        item is ChatPresentationRow ? new ChatPresentationRowPresenter() : base.CreateContainerForItemOverride(item, index, recycleKey);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        if (container is ChatPresentationRowPresenter presentationControl && item is ChatPresentationRow row)
        {
            presentationControl.SetRow(row, row.TryMarkPresented());
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        switch (container)
        {
            case ChatPresentationRowPresenter presentationControl:
            {
                presentationControl.ClearRow();
                break;
            }
        }

        base.ClearContainerForItemOverride(container);
    }
}