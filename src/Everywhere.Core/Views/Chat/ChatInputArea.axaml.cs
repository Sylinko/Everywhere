using System.Collections.Specialized;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Utilities;

namespace Everywhere.Views;

[TemplatePart("PART_ChatTextEditor", typeof(ChatTextEditor), IsRequired = true)]
[TemplatePart("PART_SendButton", typeof(Button), IsRequired = true)]
[TemplatePart("PART_ChatAttachmentItemsControl", typeof(ChatAttachmentItemsControl), IsRequired = true)]
[TemplatePart("PART_AssistantSelectionMenuItem", typeof(MenuItem), IsRequired = true)]
public sealed partial class ChatInputArea : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ChatInputArea, string?>(nameof(Text));

    public static readonly StyledProperty<int> MaxLengthProperty =
        ChatTextEditor.MaxLengthProperty.AddOwner<ChatInputArea>();

    public static readonly StyledProperty<string?> WatermarkProperty =
        ChatTextEditor.WatermarkProperty.AddOwner<ChatInputArea>();

    public static readonly StyledProperty<bool> PressCtrlEnterToSendProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(PressCtrlEnterToSend));

    public static readonly StyledProperty<IRelayCommand<string>?> CommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<string>?>(nameof(Command));

    public static readonly StyledProperty<IRelayCommand?> CancelCommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand?>(nameof(CancelCommand));

    public static readonly StyledProperty<ICollection<ChatAttachment>?> ChatAttachmentItemsSourceProperty =
        AvaloniaProperty.Register<ChatInputArea, ICollection<ChatAttachment>?>(nameof(ChatAttachmentItemsSource));

    public static readonly StyledProperty<IRelayCommand<ChatAttachment>?> RemoveAttachmentCommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<ChatAttachment>?>(nameof(RemoveAttachmentCommand));

    public static readonly StyledProperty<int> MaxChatAttachmentCountProperty =
        AvaloniaProperty.Register<ChatInputArea, int>(nameof(MaxChatAttachmentCount));

    public static readonly StyledProperty<IEnumerable<CustomAssistant>?> CustomAssistantItemsSourceProperty =
        AvaloniaProperty.Register<ChatInputArea, IEnumerable<CustomAssistant>?>(nameof(CustomAssistantItemsSource));

    public static readonly StyledProperty<CustomAssistant?> SelectedCustomAssistantProperty =
        AvaloniaProperty.Register<ChatInputArea, CustomAssistant?>(nameof(SelectedCustomAssistant));

    public static readonly DirectProperty<ChatInputArea, IEnumerable?> AddChatAttachmentMenuItemsProperty =
        AvaloniaProperty.RegisterDirect<ChatInputArea, IEnumerable?>(
            nameof(AddChatAttachmentMenuItems),
            o => o.AddChatAttachmentMenuItems);

    public static readonly StyledProperty<bool> IsToolCallSupportedProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsToolCallSupported));

    public static readonly StyledProperty<bool> IsToolCallEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsToolCallEnabled));

    public static readonly StyledProperty<bool> IsWebSearchEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsWebSearchEnabled));

    public static readonly StyledProperty<Flyout?> ToolCallButtonFlyoutProperty =
        AvaloniaProperty.Register<ChatInputArea, Flyout?>(nameof(ToolCallButtonFlyout));

    public static readonly DirectProperty<ChatInputArea, IEnumerable?> SettingsMenuItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<ChatInputArea, IEnumerable?>(
            nameof(SettingsMenuItemsSource),
            o => o.SettingsMenuItemsSource);

    public static readonly StyledProperty<ISoftwareUpdater?> SoftwareUpdaterProperty =
        AvaloniaProperty.Register<ChatInputArea, ISoftwareUpdater?>(nameof(SoftwareUpdater));

    public static readonly StyledProperty<bool> IsSendButtonEnabledProperty =
        AvaloniaProperty.Register<ChatInputArea, bool>(nameof(IsSendButtonEnabled), true);

    public static readonly StyledProperty<object?> LeadingContentProperty =
        ChatTextEditor.LeadingContentProperty.AddOwner<ChatInputArea>();

    public static readonly StyledProperty<IDataTemplate?> LeadingContentTemplateProperty =
        ChatTextEditor.LeadingContentTemplateProperty.AddOwner<ChatInputArea>();

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string SelectedText
    {
        set => _chatTextEditor?.SelectedText = value;
    }

    public int MaxLength
    {
        get => GetValue(MaxLengthProperty);
        set => SetValue(MaxLengthProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    /// <summary>
    /// If true, pressing Ctrl+Enter will send the message, Enter will break the line.
    /// </summary>
    public bool PressCtrlEnterToSend
    {
        get => GetValue(PressCtrlEnterToSendProperty);
        set => SetValue(PressCtrlEnterToSendProperty, value);
    }

    /// <summary>
    /// When the text is executed, the text will be passed as the parameter.
    /// </summary>
    public IRelayCommand<string>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public IRelayCommand? CancelCommand
    {
        get => GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }

    public ICollection<ChatAttachment>? ChatAttachmentItemsSource
    {
        get => GetValue(ChatAttachmentItemsSourceProperty);
        set => SetValue(ChatAttachmentItemsSourceProperty, value);
    }

    public IRelayCommand<ChatAttachment>? RemoveAttachmentCommand
    {
        get => GetValue(RemoveAttachmentCommandProperty);
        set => SetValue(RemoveAttachmentCommandProperty, value);
    }

    public int MaxChatAttachmentCount
    {
        get => GetValue(MaxChatAttachmentCountProperty);
        set => SetValue(MaxChatAttachmentCountProperty, value);
    }

    public CustomAssistant? SelectedCustomAssistant
    {
        get => GetValue(SelectedCustomAssistantProperty);
        set => SetValue(SelectedCustomAssistantProperty, value);
    }

    public IEnumerable<CustomAssistant>? CustomAssistantItemsSource
    {
        get => GetValue(CustomAssistantItemsSourceProperty);
        set => SetValue(CustomAssistantItemsSourceProperty, value);
    }

    public IEnumerable? AddChatAttachmentMenuItems
    {
        get;
        set => SetAndRaise(AddChatAttachmentMenuItemsProperty, ref field, value);
    } = new AvaloniaList<MenuItem>();

    public bool IsToolCallSupported
    {
        get => GetValue(IsToolCallSupportedProperty);
        set => SetValue(IsToolCallSupportedProperty, value);
    }

    public bool IsToolCallEnabled
    {
        get => GetValue(IsToolCallEnabledProperty);
        set => SetValue(IsToolCallEnabledProperty, value);
    }

    public bool IsWebSearchEnabled
    {
        get => GetValue(IsWebSearchEnabledProperty);
        set => SetValue(IsWebSearchEnabledProperty, value);
    }

    public Flyout? ToolCallButtonFlyout
    {
        get => GetValue(ToolCallButtonFlyoutProperty);
        set => SetValue(ToolCallButtonFlyoutProperty, value);
    }

    public IEnumerable? SettingsMenuItemsSource
    {
        get;
        set => SetAndRaise(SettingsMenuItemsSourceProperty, ref field, value);
    } = new AvaloniaList<object>();

    public ISoftwareUpdater? SoftwareUpdater
    {
        get => GetValue(SoftwareUpdaterProperty);
        set => SetValue(SoftwareUpdaterProperty, value);
    }

    public bool IsSendButtonEnabled
    {
        get => GetValue(IsSendButtonEnabledProperty);
        set => SetValue(IsSendButtonEnabledProperty, value);
    }

    public object? LeadingContent
    {
        get => GetValue(LeadingContentProperty);
        set => SetValue(LeadingContentProperty, value);
    }

    public IDataTemplate? LeadingContentTemplate
    {
        get => GetValue(LeadingContentTemplateProperty);
        set => SetValue(LeadingContentTemplateProperty, value);
    }

    private ChatTextEditor? _chatTextEditor;
    private IDisposable? _sendButtonClickSubscription;
    private IDisposable? _textPresenterSizeChangedSubscription;
    private IDisposable? _chatAttachmentItemsControlPointerMovedSubscription;
    private IDisposable? _chatAttachmentItemsControlPointerExitedSubscription;
    private IDisposable? _assistantSelectionMenuItemPointerWheelChangedSubscription;
    private ChatAttachmentItemsControl? _chatAttachmentItemsControl;

    private readonly VisualElementOverlayWindow _visualElementAttachmentOverlayWindow = new()
    {
        Content = new Border
        {
            Background = Brushes.DodgerBlue,
            Opacity = 0.2
        },
    };

    static ChatInputArea()
    {
        LostFocusEvent.AddClassHandler<ChatInputArea>(HandleLostFocus, handledEventsToo: true);
    }

    private static void HandleLostFocus(ChatInputArea sender, RoutedEventArgs args)
    {
        sender._visualElementAttachmentOverlayWindow.UpdateForVisualElement(null);
    }

    public ChatInputArea()
    {
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
    }

    public bool TryGetAttachmentCenterOnScreen(ChatAttachment attachment, out PixelPoint center)
    {
        center = default;
        return _chatAttachmentItemsControl?.TryGetAttachmentCenterOnScreen(attachment, out center) ?? false;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeHelper.DisposeToDefault(ref _sendButtonClickSubscription);
        DisposeHelper.DisposeToDefault(ref _textPresenterSizeChangedSubscription);
        DisposeHelper.DisposeToDefault(ref _chatAttachmentItemsControlPointerMovedSubscription);
        DisposeHelper.DisposeToDefault(ref _chatAttachmentItemsControlPointerExitedSubscription);
        DisposeHelper.DisposeToDefault(ref _assistantSelectionMenuItemPointerWheelChangedSubscription);

        _chatTextEditor = e.NameScope.Find<ChatTextEditor>("PART_ChatTextEditor").NotNull();

        // We handle the click event of the SendButton here instead of using Command binding,
        // because we need to clear the text after sending the message.
        var sendButton = e.NameScope.Find<Button>("PART_SendButton").NotNull();
        _sendButtonClickSubscription = sendButton.AddDisposableHandler(
            Button.ClickEvent,
            (_, args) =>
            {
                if (Command?.CanExecute(Text) is not true) return;
                Command.Execute(Text);
                Text = string.Empty;
                args.Handled = true;
            },
            handledEventsToo: true);

        _chatAttachmentItemsControl = e.NameScope.Find<ChatAttachmentItemsControl>("PART_ChatAttachmentItemsControl").NotNull();
        _chatAttachmentItemsControlPointerMovedSubscription = _chatAttachmentItemsControl.AddDisposableHandler(
            PointerMovedEvent,
            (_, args) =>
            {
                var element = args.Source as StyledElement;
                while (element != null)
                {
                    element = element.Parent;
                    if (element is not { DataContext: VisualElementAttachment attachment }) continue;
                    _visualElementAttachmentOverlayWindow.UpdateForVisualElement(attachment.Element?.Target);
                    return;
                }
                _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null);
            },
            handledEventsToo: true);
        _chatAttachmentItemsControlPointerExitedSubscription = _chatAttachmentItemsControl.AddDisposableHandler(
            PointerExitedEvent,
            (_, _) => _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null),
            handledEventsToo: true);

        var assistantSelectionMenuItem = e.NameScope.Find<MenuItem>("PART_AssistantSelectionMenuItem");
        if (assistantSelectionMenuItem != null)
        {
            _assistantSelectionMenuItemPointerWheelChangedSubscription = assistantSelectionMenuItem.AddDisposableHandler(
                PointerWheelChangedEvent,
                HandleAssistantSelectionPointerWheelChanged,
                RoutingStrategies.Bubble,
                handledEventsToo: true);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChatAttachmentItemsSourceProperty)
        {
            if (change.OldValue is INotifyCollectionChanged oldValue)
            {
                oldValue.CollectionChanged -= HandleChatAttachmentItemsSourceChanged;
            }
            if (change.NewValue is INotifyCollectionChanged newValue)
            {
                newValue.CollectionChanged += HandleChatAttachmentItemsSourceChanged;
            }
        }
    }

    private void HandleChatAttachmentItemsSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null); // Hide the overlay window when the attachment list changes.
    }

    public void Focus() => _chatTextEditor?.Focus();

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);

        _visualElementAttachmentOverlayWindow.UpdateForVisualElement(null); // Hide the overlay window when the control is unloaded.
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Because this control is inherited from TextBox, it will receive pointer events and broke the MenuItem's pointer events.
        // We need to ignore pointer events if the source is a StyledElement that is inside a MenuItem.
        if (e.Source is StyledElement element && element.FindLogicalAncestorOfType<MenuItem>() != null)
        {
            return;
        }

        base.OnPointerPressed(e);
    }

    [RelayCommand]
    private void SetSelectedCustomAssistant(MenuItem? sender)
    {
        SelectedCustomAssistant = sender?.DataContext as CustomAssistant;
    }

    private void HandleAssistantSelectionPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var assistants = CustomAssistantItemsSource?.ToList();
        if (assistants is null || assistants.Count <= 1) return;

        var currentIndex = SelectedCustomAssistant is not null ? assistants.IndexOf(SelectedCustomAssistant) : -1;
        if (currentIndex == -1)
        {
            SelectedCustomAssistant = assistants[0];
            e.Handled = true;
            return;
        }

        currentIndex = e.Delta.Y switch
        {
            > 0 => Math.Max(currentIndex - 1, 0),
            < 0 => Math.Min(currentIndex + 1, assistants.Count - 1),
            _ => currentIndex
        };

        SelectedCustomAssistant = assistants[currentIndex];
        e.Handled = true;
    }

    [RelayCommand]
    private Task PerformUpdateAsync() => SoftwareUpdater?.PerformUpdateAsync() ?? Task.CompletedTask;

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control)
        {
            var index = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                Key.D0 => 9,
                _ => -1
            };

            if (index >= 0 && CustomAssistantItemsSource != null)
            {
                var assistant = CustomAssistantItemsSource.ElementAtOrDefault(index);
                if (assistant != null)
                {
                    SelectedCustomAssistant = assistant;
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.V)
            {
                var pastingEvent = new RoutedEventArgs(TextBox.PastingFromClipboardEvent, this);
                RaiseEvent(pastingEvent);
                e.Handled = pastingEvent.Handled;
            }
        }

        switch (e.Key)
        {
            case Key.Enter:
            {
                if ((!PressCtrlEnterToSend || e.KeyModifiers != KeyModifiers.Control) &&
                    (PressCtrlEnterToSend || e.KeyModifiers != KeyModifiers.None)) return;

                if (Command?.CanExecute(Text) is not true) break;

                Command.Execute(Text);
                Text = string.Empty;
                e.Handled = true;
                break;
            }
        }
    }
}