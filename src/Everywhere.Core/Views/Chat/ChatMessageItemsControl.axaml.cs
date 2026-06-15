using Avalonia.Controls;
using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Views;

public class ChatMessageItemsControl : ItemsControl
{
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
    /// Gets or sets the modalities supported by this control. This can be used to determine which types of content (e.g., text, images, videos) the control can display or interact with.
    /// </summary>
    public Modalities SupportedModalities
    {
        get => GetValue(SupportedModalitiesProperty);
        set => SetValue(SupportedModalitiesProperty, value);
    }

    private ChatMessageControl? _lastMessageControl;

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is ChatMessageNode or ChatMessage)
        {
            recycleKey = typeof(ChatMessageControl);
            return true;
        }

        return base.NeedsContainerOverride(item, index, out recycleKey);
    }

    protected override void ContainerIndexChangedOverride(Control container, int oldIndex, int newIndex)
    {
        base.ContainerIndexChangedOverride(container, oldIndex, newIndex);

        UpdateLastMessageControl(container as ChatMessageControl, newIndex);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return item switch
        {
            ChatMessageNode or ChatMessage => new ChatMessageControl(),
            _ => base.CreateContainerForItemOverride(item, index, recycleKey)
        };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        if (container is not ChatMessageControl chatMessageControl)
            return;

        switch (item)
        {
            case ChatMessageNode chatMessageNode:
                chatMessageControl.DataContext = chatMessageNode;
                chatMessageControl.Content = chatMessageNode.Message;
                break;
            case ChatMessage chatMessage:
                chatMessageControl.DataContext = chatMessage;
                chatMessageControl.Content = chatMessage;
                break;
        }

        UpdateLastMessageControl(chatMessageControl, index);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is ChatMessageControl chatMessageControl)
        {
            if (ReferenceEquals(_lastMessageControl, chatMessageControl))
            {
                _lastMessageControl = null;
            }

            chatMessageControl.IsLast = false;
            chatMessageControl.Content = null;
            chatMessageControl.DataContext = null;
        }

        base.ClearContainerForItemOverride(container);
    }

    private void UpdateLastMessageControl(ChatMessageControl? control, int index)
    {
        if (control is null)
            return;

        var isLast = index == Items.Count - 1;
        if (isLast)
        {
            _lastMessageControl?.IsLast = false;
            _lastMessageControl = control;
            _lastMessageControl.IsLast = true;
        }
        else
        {
            if (ReferenceEquals(_lastMessageControl, control))
            {
                _lastMessageControl = null;
            }

            control.IsLast = false;
        }
    }
}