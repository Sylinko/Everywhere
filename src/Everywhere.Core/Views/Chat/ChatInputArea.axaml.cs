using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Interop;
using Everywhere.Utilities;

namespace Everywhere.Views;

[TemplatePart("PART_ChatTextEditor", typeof(ChatTextEditor), IsRequired = true)]
[TemplatePart("PART_ChatAttachmentItemsControl", typeof(ChatAttachmentItemsControl), IsRequired = true)]
[TemplatePart("PART_AttachmentScrollViewer", typeof(ScrollViewer), IsRequired = true)]
public sealed class ChatInputArea : TemplatedControl
{
    public static KeyboardShortcut SendShortcut { get; } = new(Key.Enter, KeyModifiers.None);

    public static KeyboardShortcut NewLineShortcut { get; } = new(Key.Enter, KeyModifiers.Shift);

    public static KeyboardShortcut OppositeModeShortcut { get; } =
        new(Key.Enter, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ChatInputArea, string?>(nameof(Text));

    public static readonly StyledProperty<int> MaxLengthProperty =
        ChatTextEditor.MaxLengthProperty.AddOwner<ChatInputArea>();

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        ChatTextEditor.PlaceholderTextProperty.AddOwner<ChatInputArea>();

    public static readonly StyledProperty<CustomAssistant?> SelectedCustomAssistantProperty =
        AvaloniaProperty.Register<ChatInputArea, CustomAssistant?>(nameof(SelectedCustomAssistant));

    public static readonly StyledProperty<IReadOnlyList<CustomAssistant>?> CustomAssistantItemsSourceProperty =
        AvaloniaProperty.Register<ChatInputArea, IReadOnlyList<CustomAssistant>?>(nameof(CustomAssistantItemsSource));

    public static readonly StyledProperty<object?> ToolBarContentProperty =
        AvaloniaProperty.Register<ChatInputArea, object?>(nameof(ToolBarContent));

    public static readonly StyledProperty<object?> TrailingContentProperty =
        AvaloniaProperty.Register<ChatInputArea, object?>(nameof(TrailingContent));

    public static readonly StyledProperty<IRelayCommand<string>?> CommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<string>?>(nameof(Command));

    public static readonly StyledProperty<IRelayCommand<string>?> AlternateCommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<string>?>(nameof(AlternateCommand));

    public static readonly StyledProperty<ICollection<ChatAttachment>?> ChatAttachmentItemsSourceProperty =
        AvaloniaProperty.Register<ChatInputArea, ICollection<ChatAttachment>?>(nameof(ChatAttachmentItemsSource));

    public static readonly StyledProperty<IRelayCommand<ChatAttachment>?> RemoveAttachmentCommandProperty =
        AvaloniaProperty.Register<ChatInputArea, IRelayCommand<ChatAttachment>?>(nameof(RemoveAttachmentCommand));

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

    public string? PlaceholderText
    {
        get => GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public CustomAssistant? SelectedCustomAssistant
    {
        get => GetValue(SelectedCustomAssistantProperty);
        set => SetValue(SelectedCustomAssistantProperty, value);
    }

    public IReadOnlyList<CustomAssistant>? CustomAssistantItemsSource
    {
        get => GetValue(CustomAssistantItemsSourceProperty);
        set => SetValue(CustomAssistantItemsSourceProperty, value);
    }

    public object? ToolBarContent
    {
        get => GetValue(ToolBarContentProperty);
        set => SetValue(ToolBarContentProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    /// <summary>
    /// When the text is executed, the text will be passed as the parameter.
    /// </summary>
    public IRelayCommand<string>? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public IRelayCommand<string>? AlternateCommand
    {
        get => GetValue(AlternateCommandProperty);
        set => SetValue(AlternateCommandProperty, value);
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
    private IDisposable? _attachmentScrollViewerPointerWheelChangedSubscription;
    private IDisposable? _attachmentScrollViewerPointerPressedSubscription;
    private ChatAttachmentItemsControl? _chatAttachmentItemsControl;

    public long AttachmentInteractionVersion { get; private set; }

    public ChatInputArea()
    {
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel);
    }

    public bool TryGetAttachmentBoundsOnScreen(VisualElementAttachment attachment, out PixelRect bounds)
    {
        bounds = default;
        return _chatAttachmentItemsControl?.TryGetAttachmentBoundsOnScreen(attachment, out bounds) ?? false;
    }

    public bool RegisterPendingAttachment(VisualElementAttachment attachment)
    {
        if (_chatAttachmentItemsControl is null) ApplyTemplate();
        if (_chatAttachmentItemsControl is not { } control) return false;

        control.RegisterPendingAttachment(attachment);
        return true;
    }

    public bool BeginAttachmentAcceptance(VisualElementAttachment attachment)
    {
        return _chatAttachmentItemsControl?.BeginAttachmentAcceptance(attachment) ?? false;
    }

    public bool IsAttachmentReady(VisualElementAttachment attachment, out PixelRect bounds)
    {
        bounds = default;
        return _chatAttachmentItemsControl?.IsAttachmentReady(attachment, out bounds) ?? false;
    }

    public void CompleteAttachmentAcceptance(VisualElementAttachment attachment)
    {
        _chatAttachmentItemsControl?.CompleteAttachmentAcceptance(attachment);
    }

    public void CancelAttachmentAcceptance(VisualElementAttachment attachment)
    {
        _chatAttachmentItemsControl?.CancelAttachmentAcceptance(attachment);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        DisposeHelper.DisposeToDefault(ref _attachmentScrollViewerPointerWheelChangedSubscription);
        DisposeHelper.DisposeToDefault(ref _attachmentScrollViewerPointerPressedSubscription);

        _chatTextEditor = e.NameScope.Find<ChatTextEditor>("PART_ChatTextEditor").NotNull();
        _chatAttachmentItemsControl = e.NameScope.Find<ChatAttachmentItemsControl>("PART_ChatAttachmentItemsControl").NotNull();
        var attachmentScrollViewer = e.NameScope.Find<ScrollViewer>("PART_AttachmentScrollViewer").NotNull();
        _attachmentScrollViewerPointerWheelChangedSubscription = attachmentScrollViewer.AddDisposableHandler(
            PointerWheelChangedEvent,
            (_, _) => AttachmentInteractionVersion++,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        _attachmentScrollViewerPointerPressedSubscription = attachmentScrollViewer.AddDisposableHandler(
            PointerPressedEvent,
            (_, args) =>
            {
                if (args.Source is Visual source &&
                    (source is ScrollBar || source.GetVisualAncestors().OfType<ScrollBar>().Any()))
                {
                    AttachmentInteractionVersion++;
                }
            },
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    public void Focus() => _chatTextEditor?.Focus();

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

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.IsApplicationShortcutModifierOnly())
        {
            var index = e.Key switch
            {
                >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                Key.D0 => 9,
                _ => -1
            };

            if (index >= 0 && CustomAssistantItemsSource is { } assistants && index < assistants.Count)
            {
                SelectedCustomAssistant = assistants[index];
                e.Handled = true;
                return;
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
                if (e.KeyModifiers == KeyModifiers.None)
                {
                    if (Command?.CanExecute(Text) is not true) break;

                    Command.Execute(Text);
                    e.Handled = true;
                    break;
                }

                if (e.KeyModifiers.IsApplicationShortcutModifierOnly())
                {
                    if (AlternateCommand?.CanExecute(Text) is not true) break;

                    AlternateCommand.Execute(Text);
                    e.Handled = true;
                }
                break;
            }
        }
    }
}
