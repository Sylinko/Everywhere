using System.Diagnostics.CodeAnalysis;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AttachedProperties;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Utilities;
using Lucide.Avalonia;
using Serilog;

namespace Everywhere.Views;

public partial class ChatWindow :
    ReactiveShadWindow<ChatWindowViewModel>,
    IRecipient<CloakChatWindowMessage>,
    IRecipient<FlashChatWindowMessage>,
    IRecipient<ApplicationMessage>,
    IVisualElementAnimationTarget
{
    /// <summary>
    /// Defines the <see cref="IsWindowPinned"/> property.
    /// </summary>
    public static readonly StyledProperty<bool?> IsWindowPinnedProperty =
        AvaloniaProperty.Register<ChatWindow, bool?>(nameof(IsWindowPinned));

    /// <summary>
    /// Gets or sets a value indicating whether the window is pinned.
    /// true: pinned and on top
    /// null: pinned but not on top
    /// false: not pinned, on top. hidden when unfocused
    /// </summary>
    public bool? IsWindowPinned
    {
        get => GetValue(IsWindowPinnedProperty);
        set => SetValue(IsWindowPinnedProperty, value);
    }

    private static Size DefaultSize => new(400d, 300d);

    private readonly IWindowHelper _windowHelper;
    private readonly INativeHelper _nativeHelper;
    private readonly Settings _settings;
    private readonly PersistentState _persistentState;

    /// <summary>
    /// Indicates whether the window has been resized by the user.
    /// </summary>
    private bool _isUserResized;

    /// <summary>
    /// Indicates whether the window can be closed.
    /// </summary>
    private bool _canCloseWindow;

    public ChatWindow(
        IServiceProvider serviceProvider,
        IWindowHelper windowHelper,
        INativeHelper nativeHelper,
        Settings settings,
        PersistentState persistentState) : base(serviceProvider, disposeOnUnloaded: false)
    {
        _windowHelper = windowHelper;
        _nativeHelper = nativeHelper;
        _settings = settings;
        _persistentState = persistentState;

        InitializeComponent();
        AddHandler(KeyDownEvent, HandleKeyDown, RoutingStrategies.Tunnel, true);

        ChatInputArea.AddDisposableHandler(TextBox.TextChangedEvent, HandleChatInputAreaTextChanged);
        ChatInputArea.AddDisposableHandler(TextBox.PastingFromClipboardEvent, HandleChatInputAreaPastingFromClipboard);

        SetupDragDropHandlers();

        // Don't use RegisterAll because base class has Register one, call to RegisterAll will cause exception.
        WeakReferenceMessenger.Default.Register<CloakChatWindowMessage>(this);
        WeakReferenceMessenger.Default.Register<FlashChatWindowMessage>(this);
        WeakReferenceMessenger.Default.Register<ApplicationMessage>(this);
    }

    private void SetupDragDropHandlers()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, HandleDragEnter);
        AddHandler(DragDrop.DragOverEvent, HandleDragOver);
        AddHandler(DragDrop.DragLeaveEvent, HandleDragLeave);
        AddHandler(DragDrop.DropEvent, HandleDrop);
    }

    /// <summary>
    /// Initializes the chat window.
    /// </summary>
    public void Initialize()
    {
        EnsureInitialized();
        ApplyStyling();
        ApplyTemplate();

        _windowHelper.InitializeWindow(this);
        _windowHelper.SetCloaked(this, true);

        // Setup window placement saving after initialization
        this[SaveWindowPlacementAssist.KeyProperty] = nameof(ChatWindow);
        _isUserResized = SizeToContent != SizeToContent.WidthAndHeight;
    }

    private void HandleKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e)
        {
            case { Key: Key.Escape }:
            {
                if (ViewModel.EditingMessageNode is not null)
                {
                    ViewModel.CancelEditing();
                }
                else
                {
                    SetCloaked(true);
                }

                e.Handled = true;
                break;
            }
            case { Key: Key.H, KeyModifiers: KeyModifiers.Control }:
            {
                _persistentState.IsChatWindowHistoryOpened = !_persistentState.IsChatWindowHistoryOpened;
                e.Handled = true;
                break;
            }
            case { Key: Key.T, KeyModifiers: KeyModifiers.Control }:
            {
                _persistentState.IsToolCallEnabled = !_persistentState.IsToolCallEnabled;
                e.Handled = true;
                break;
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (ReferenceEquals(change.Property, IsVisibleProperty))
        {
            ViewModel.IsOpened = change.NewValue is true;
        }
        else if (ReferenceEquals(change.Property, IsWindowPinnedProperty))
        {
            var value = change.NewValue as bool?;
            _persistentState.IsChatWindowPinned = value;
            Topmost = value is not null; // false: topmost, null: normal, true: topmost
            _windowHelper.SetCloaked(this, false); // Uncloak when pinned state changes to ensure visibility
        }
    }

    protected override void OnResized(WindowResizedEventArgs e)
    {
        base.OnResized(e);

        if (e.Reason != WindowResizeReason.User) return;

        if (e.ClientSize.NearlyEquals(new Size(MinWidth, MinHeight)))
        {
            _isUserResized = false;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            _isUserResized = true;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_isUserResized)
        {
            availableSize = DefaultSize;
        }

        double width = 0;
        double height = 0;

        {
            var visualCount = VisualChildren.Count;
            for (var i = 0; i < visualCount; i++)
            {
                var visual = VisualChildren[i];
                if (visual is not Layoutable layoutable) continue;

                layoutable.Measure(availableSize);
                var childSize = layoutable.DesiredSize;
                if (childSize.Width > width) width = childSize.Width;
                if (childSize.Height > height) height = childSize.Height;
            }
        }

        if (_isUserResized)
        {
            var clientSize = ClientSize;

            if (!double.IsInfinity(availableSize.Width))
            {
                width = availableSize.Width;
            }
            else
            {
                width = clientSize.Width;
            }

            if (!double.IsInfinity(availableSize.Height))
            {
                height = availableSize.Height;
            }
            else
            {
                height = clientSize.Height;
            }
        }

        return new Size(width, height);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);

        if (!ViewModel.IsPickingFiles && !IsActive && IsWindowPinned is false && !_windowHelper.AnyModelDialogOpened(this))
        {
            SetCloaked(true);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        return new NoneAutomationPeer(this); // Disable automation peer to avoid being detected by self
    }

    void IRecipient<CloakChatWindowMessage>.Receive(CloakChatWindowMessage message)
    {
        Dispatcher.UIThread.Invoke(() => SetCloaked(message.IsCloaked));
    }

    void IRecipient<FlashChatWindowMessage>.Receive(FlashChatWindowMessage message)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            if (!IsFocused) _windowHelper.RequestUserAttention(this);

            if (!_windowHelper.GetEffectiveVisible(this) && message.Prompt is { Length: > 0 } prompt)
            {
                _nativeHelper.ShowDesktopNotificationAsync(prompt).ContinueWith(t =>
                {
                    if (t is { IsCompletedSuccessfully: true, Result: true })
                    {
                        Dispatcher.UIThread.Invoke(() => SetCloaked(false));
                    }
                });
            }
        });
    }

    void IRecipient<ApplicationMessage>.Receive(ApplicationMessage message)
    {
        if (message is ShowWindowMessage { Name: ShowWindowMessage.ChatWindow })
        {
            Dispatcher.UIThread.Invoke(() => SetCloaked(false));
        }
    }

    private void SetCloaked(bool value)
    {
        if (value)
        {
            _windowHelper.SetCloaked(this, true);
        }
        else
        {
            if (!IsVisible)
            {
                switch (_settings.ChatWindow.WindowPinMode)
                {
                    case ChatWindowPinMode.RememberLast:
                    {
                        IsWindowPinned = _persistentState.IsChatWindowPinned;
                        break;
                    }
                    case ChatWindowPinMode.AlwaysTopmost:
                    {
                        IsWindowPinned = true;
                        break;
                    }
                    case ChatWindowPinMode.AlwaysPinned:
                    {
                        IsWindowPinned = null;
                        break;
                    }
                    case ChatWindowPinMode.AlwaysUnpinned:
                    case ChatWindowPinMode.PinOnInput:
                    {
                        IsWindowPinned = false;
                        break;
                    }
                }
            }

            _windowHelper.SetCloaked(this, false);
            ChatInputArea.Focus();
        }
    }

    private void HandleChatInputAreaTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (IsWindowPinned == false)
        {
            IsWindowPinned = _settings.ChatWindow.WindowPinMode switch
            {
                ChatWindowPinMode.PinOnInput => null,
                ChatWindowPinMode.TopmostOnInput => true,
                _ => IsWindowPinned
            };
        }
    }

    private void HandleChatInputAreaPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.AddClipboardCommand.CanExecute(null)) return;

        ViewModel.AddClipboardCommand.Execute(null);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // allow closing only on application or OS shutdown
        // otherwise, Windows will say "Everywhere is preventing shutdown"
        if (e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
        {
            _canCloseWindow = true;
            base.OnClosing(e);
            return;
        }

        // do not allow closing, just hide the window
        e.Cancel = true;
        SetCloaked(true);

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        if (!_canCloseWindow)
            Log.ForContext<ChatWindow>().Error("Chat window was closed unexpectedly. This should not happen.");
    }

    private void HandleDragEnter(object? sender, DragEventArgs e)
    {
        UpdateDragVisuals(e);
        e.Handled = true;
    }

    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        UpdateDragVisuals(e);
        e.Handled = true;
    }

    private void UpdateDragVisuals(DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.Contains(DataFormat.File);
        var hasText = e.DataTransfer.Contains(DataFormat.Text);

        if (!hasFiles && !hasText)
        {
            e.DragEffects = DragDropEffects.None;
            DragDropOverlay.IsVisible = false;
            return;
        }

        // Check file support
        if (hasFiles)
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files != null)
            {
                var hasSupportedFile = false;
                var hasUnsupportedFile = false;
                string? firstMimeType = null;

                foreach (var item in files)
                {
                    if (IsSupportedFile(item, out _, out var mimeType))
                    {
                        hasSupportedFile = true;
                        firstMimeType ??= mimeType;
                    }
                    else
                    {
                        hasUnsupportedFile = true;
                    }
                }

                if (hasUnsupportedFile)
                {
                    e.DragEffects = DragDropEffects.None;
                    DragDropIcon.Kind = LucideIconKind.FileX;
                    DragDropText.Text = LocaleResolver.ChatWindow_DragDrop_Overlay_Unsupported;
                    DragDropOverlay.IsVisible = true;
                    return;
                }

                if (hasSupportedFile)
                {
                    if (ViewModel.ChatAttachments.Count >= _persistentState.MaxChatAttachmentCount)
                    {
                        e.DragEffects = DragDropEffects.None;
                        DragDropOverlay.IsVisible = false;
                        return;
                    }

                    e.DragEffects = DragDropEffects.Copy;
                    DragDropIcon.Kind = firstMimeType != null && FileUtilities.IsOfCategory(firstMimeType, FileTypeCategory.Image) ?
                        LucideIconKind.Image :
                        LucideIconKind.FileUp;
                    DragDropText.Text = LocaleResolver.ChatWindow_DragDrop_Overlay_DropFilesHere;
                    DragDropOverlay.IsVisible = true;
                    return;
                }
            }
        }

        // Text only
        if (hasText)
        {
            e.DragEffects = DragDropEffects.Copy;
            DragDropIcon.Kind = LucideIconKind.TextCursorInput;
            DragDropText.Text = LocaleResolver.ChatWindow_DragDrop_Overlay_DropTextHere;
            DragDropOverlay.IsVisible = true;
            return;
        }

        e.DragEffects = DragDropEffects.None;
        DragDropOverlay.IsVisible = false;
    }

    private void HandleDragLeave(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;
        e.Handled = true;
    }

    private void HandleDrop(object? sender, DragEventArgs e)
    {
        DragDropOverlay.IsVisible = false;
        e.Handled = true;

        HandleDropAsync().Detach(PART_ToastHost.ToExceptionHandler());

        async Task HandleDropAsync()
        {
            // Handle file drops
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                var files = e.DataTransfer.TryGetFiles();
                if (files is null) return;

                foreach (var item in files)
                {
                    if (!IsSupportedFile(item, out var localPath, out _))
                        continue;

                    try
                    {
                        await ViewModel.AddFileFromDragDropAsync(localPath, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.ForContext<ChatWindow>().Error(ex, "Failed to add dropped file: {FilePath}", localPath);
                    }

                    if (ViewModel.ChatAttachments.Count >= _persistentState.MaxChatAttachmentCount) break;
                }
            }


            // Handle text drops
            if (e.DataTransfer.Contains(DataFormat.Text))
            {
                var text = e.DataTransfer.TryGetText();
                if (string.IsNullOrWhiteSpace(text)) return;

                ChatInputArea.SelectedText = text;
            }
        }
    }

    private static bool IsSupportedFile(IStorageItem storageItem, [NotNullWhen(true)] out string? localPath, [NotNullWhen(true)] out string? mimeType)
    {
        localPath = null;
        mimeType = null;

        if (!storageItem.Path.IsFile || storageItem.TryGetLocalPath() is not { } path)
            return false;

        localPath = path;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (FileUtilities.KnownMimeTypes.TryGetValue(extension, out var mime) &&
            FileUtilities.KnownFileTypes.TryGetValue(mime, out var fileType) &&
            fileType is FileTypeCategory.Image or FileTypeCategory.Audio or FileTypeCategory.Document or FileTypeCategory.Script)
        {
            mimeType = mime;
            return true;
        }

        return false;
    }

    public bool TryGetAttachmentCenterOnScreen(ChatAttachment attachment, out PixelPoint center)
    {
        return ChatInputArea.TryGetAttachmentCenterOnScreen(attachment, out center);
    }
}