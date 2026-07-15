using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
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
        if (value is not Uri uri && !Uri.TryCreate(value?.ToString(), UriKind.Absolute, out uri!))
            return Task.FromResult(false);

        return App.Launcher.LaunchUriAsync(uri);
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
                    ChatContext = block.ChatContext,
                    SupportedModalities = SupportedModalities,
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