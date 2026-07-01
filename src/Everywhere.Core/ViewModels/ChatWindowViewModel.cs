using System.Diagnostics.Metrics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Common.Notification;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.ViewModels;

public sealed partial class ChatWindowViewModel :
    ReactiveViewModelBase,
    IRecipient<ActivateChatSessionMessage>,
    IObserver<TextSelectionData>
{
    public Settings Settings { get; }

    public PersistentState PersistentState { get; }

    public IChatContextManager ChatContextManager { get; }

    public bool IsOpened
    {
        get;
        set
        {
            if (field == value) return;
            field = value;

            _activeChatWindowsGauge.Record(value ? 1 : 0);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; private set; }

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    public partial IReadOnlyList<Strategy>? StrategiesSnapshot { get; private set; }

    /// <summary>
    /// Indicates whether the file picker is currently open.
    /// </summary>
    public bool IsPickingFiles { get; set; }

    public IReadOnlyBindableList<ChatAttachment> ChatAttachments { get; }

    public IReadOnlyBindableList<ChatPlugin> ChatPlugins { get; }

    public IReadOnlyBindableList<DynamicNotification> Notifications => _notificationService.Notifications;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    public partial ChatMessageNode? EditingUserMessageNode { get; private set; }

    public bool CanEdit => !IsBusy && EditingUserMessageNode is null;

    [ObservableProperty]
    public partial Strategy? SelectedStrategy { get; set; }

    public static int ChatInputAreaTextMaxLength => 100_000;

    /// <summary>
    /// The text in the chat input box.
    /// </summary>
    public string? ChatInputAreaText
    {
        get;
        set
        {
            value = value.SafeSubstring(0, ChatInputAreaTextMaxLength);
            if (!SetProperty(ref field, value)) return;
            if (EditingUserMessageNode is null) PersistentState.ChatInputAreaText = value;
        }
    }

    /// <summary>
    /// The resource key for the watermark text in the chat input box.
    /// Can be set to one of greetings or instructions based on the chat context, or a default value.
    /// </summary>
    [ObservableProperty]
    public partial IDynamicLocaleKey? ChatInputAreaWatermarkKey { get; private set; }

    public ISoftwareUpdater SoftwareUpdater { get; }

    private readonly IChatService _chatService;
    private readonly IVisualElementContext _visualElementContext;
    private readonly IBlobStorage _blobStorage;
    private readonly IStrategyEngine _strategyEngine;
    private readonly IGreetings _greetings;
    private readonly IChatWindowNotificationService _notificationService;
    private readonly ILogger<ChatWindowViewModel> _logger;

    private readonly DynamicLocaleKey _defaultWatermarkKey = new(LocaleKey.ChatInputArea_PlaceholderText);
    private readonly SourceList<ChatAttachment> _chatAttachmentsSource = new();

    private readonly Meter _meter = new(typeof(ChatWindowViewModel).FullName.NotNull(), App.Version);
    private readonly Gauge<int> _activeChatWindowsGauge;

    private ChatInputAreaSnapshot? _snapshotBeforeEdit;

    public ChatWindowViewModel(
        Settings settings,
        PersistentState persistentState,
        IChatContextManager chatContextManager,
        IChatPluginManager chatPluginManager,
        IChatWindowNotificationService notificationService,
        ISoftwareUpdater softwareUpdater,
        IChatService chatService,
        IVisualElementContext visualElementContext,
        IBlobStorage blobStorage,
        IStrategyEngine strategyEngine,
        IGreetings greetings,
        ILogger<ChatWindowViewModel> logger)
    {
        Settings = settings;
        PersistentState = persistentState;
        ChatContextManager = chatContextManager;
        SoftwareUpdater = softwareUpdater;

        _chatService = chatService;
        _visualElementContext = visualElementContext;
        _blobStorage = blobStorage;
        _strategyEngine = strategyEngine;
        _greetings = greetings;
        _notificationService = notificationService;
        _logger = logger;

        _activeChatWindowsGauge = _meter.CreateGauge<int>("app.active_chat_windows");

        // Initialize chat plugins from both built-in and MCP
        ChatPlugins = chatPluginManager.BuiltInPlugins
            .ToObservableChangeSet<IReadOnlyBindableList<BuiltInChatPlugin>, BuiltInChatPlugin>()
            .Transform(ChatPlugin (x) => x, transformOnRefresh: true)
            .Or(
                chatPluginManager.McpPlugins
                    .ToObservableChangeSet<IReadOnlyBindableList<McpChatPlugin>, McpChatPlugin>()
                    .Transform(ChatPlugin (x) => x, transformOnRefresh: true))
            .BindEx(LifetimeDisposables);

        // Initialize chat attachments
        ChatAttachments = _chatAttachmentsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(LifetimeDisposables);
        LifetimeDisposables.Add(_chatAttachmentsSource);

        // Initialize strategy commands
        LifetimeDisposables.Add(
            _chatAttachmentsSource
                .Connect()
                .ToCollection()
                .ThrottleWithLeadingEdge(TimeSpan.FromMilliseconds(250))
                .ObserveOn(TaskPoolScheduler.Default)
                .Subscribe(HandleChatAttachmentsChanged)
        );
        Task.Run(() => HandleChatAttachmentsChanged([])).Detach();

        // Load the saved input box text
        ChatInputAreaText = PersistentState.ChatInputAreaText;
        ChatInputAreaWatermarkKey = _defaultWatermarkKey;

        LifetimeDisposables.Add(
            ChatContextManager.WhenValueChanged(x => x.Current)
                .Select(context => context is null ? Observable.Return(false) : context.WhenValueChanged(x => x.IsBusy))
                .Switch()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(HandleCurrentChatContextIsBusyChanged)
        );

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }

        base.Dispose(disposing);
    }

    public void Receive(ActivateChatSessionMessage message)
    {
        HandleActivateChatSessionMessageCommand.Execute(message);
    }

    [RelayCommand]
    private async Task HandleActivateChatSessionMessageAsync(ActivateChatSessionMessage message)
    {
        try
        {
            // Avoid adding duplicate attachments
            var targetElement = message.TargetElement;
            if (_chatAttachmentsSource.Items.Any(a => a is VisualElementAttachment vea && Equals(vea.Element?.Target, targetElement)))
            {
                ShowChatWindow();
                return;
            }

            if (targetElement == null)
            {
                _chatAttachmentsSource.Edit(list =>
                {
                    if (list is [VisualElementAttachment { IsPrimary: true }, ..])
                    {
                        list.RemoveAt(0);
                    }
                });

                ShowChatWindow();
                return;
            }

            var createElement = Settings.ChatWindow.AutomaticallyAddElement;
            var chatAttachment = await Task
                .Run(() => createElement ? VisualElementAttachment.FromVisualElement(targetElement) : null)
                .WaitAsync(TimeSpan.FromSeconds(1));

            if (chatAttachment is not null)
            {
                VisualElementEffect? visualElementEffect = null;
                if (Settings.ChatWindow.EnableVisualElementPickAnimation)
                {
                    visualElementEffect = ServiceLocator.Resolve<VisualElementEffect>();
                    visualElementEffect.ArrangeEffectWindows();
                }

                ShowChatWindow();

                _chatAttachmentsSource.Edit(list =>
                {
                    list.RemoveWhere(a => a is VisualElementAttachment { IsPrimary: true });
                    list.Insert(
                        0,
                        chatAttachment.With(a =>
                        {
                            a.IsPrimary = true;
                            a.Opacity = visualElementEffect is not null ? 0d : 1d;
                        }));
                });

                if (visualElementEffect is not null)
                {
                    await visualElementEffect.CreatePickEffect(targetElement, chatAttachment);
                }
            }
            else
            {
                ShowChatWindow();
            }
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process ActivateChatSessionMessage");
        }

        void ShowChatWindow()
        {
            if (!IsOpened)
            {
                if (Settings.ChatWindow.AlwaysStartNewChat && ChatContextManager.CreateNewCommand.CanExecute(null))
                {
                    ChatContextManager.CreateNewCommand.Execute(null);
                }
            }

            WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
        }
    }

    [RelayCommand]
    private async Task PickVisualElementAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            // Hide the chat window to avoid picking itself
            var isOpened = IsOpened;
            if (isOpened) WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true));

            var element = await _visualElementContext.PickVisualElementAsync(null);
            if (element is null)
            {
                if (isOpened) WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
                return;
            }

            if (_chatAttachmentsSource.Items.OfType<VisualElementAttachment>().Any(a => Equals(a.Element?.Target, element)))
            {
                WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
                return;
            }

            var chatAttachment = await Task.Run(
                () => VisualElementAttachment.FromVisualElement(element),
                cancellationToken
            ).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);

            if (Settings.ChatWindow.EnableVisualElementPickAnimation)
            {
                var visualElementEffect = ServiceLocator.Resolve<VisualElementEffect>();
                visualElementEffect.ArrangeEffectWindows();

                WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
                _chatAttachmentsSource.Add(chatAttachment.With(x => x.Opacity = 0d));

                await visualElementEffect.CreatePickEffect(element, chatAttachment);
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
                _chatAttachmentsSource.Add(chatAttachment);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to pick visual element");
            ToastExceptionHandler.HandleException(ex);
        }
    }

    [RelayCommand]
    private async Task TakeScreenshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            // Hide the chat window to avoid picking itself
            var isOpened = IsOpened;
            if (isOpened) WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true));
            var bitmap = await _visualElementContext.TakeScreenshotAsync(null);
            if (isOpened || bitmap is not null) WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
            if (bitmap is null) return;
            _chatAttachmentsSource.Add(await Task.Run(() => CreateFromBitmapAsync(bitmap, cancellationToken), cancellationToken));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to take screenshot");
            ToastExceptionHandler.HandleException(HandledSystemException.Handle(ex));
        }
    }

    [RelayCommand]
    private async Task AddClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            var formats = await Clipboard.GetDataFormatsAsync();
            if (formats.Count == 0)
            {
                _logger.LogWarning("Clipboard is empty.");
                return;
            }

            if (formats.Contains(DataFormat.File))
            {
                var files = await Clipboard.TryGetFilesAsync();
                if (files != null)
                {
                    foreach (var storageItem in files)
                    {
                        var uri = storageItem.Path;
                        if (!uri.IsFile) break;
                        await AddFileUncheckAsync(uri.LocalPath, "from clipboard, temporary filepath", cancellationToken);
                        if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                    }
                }
            }
            else if (formats.Contains(DataFormat.Bitmap) && await Clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                _chatAttachmentsSource.Add(await Task.Run(() => CreateFromBitmapAsync(bitmap, cancellationToken), cancellationToken));
            }

            // TODO: add as text attachment when text is too long
            // else if (formats.Contains(DataFormats.Text))
            // {
            //     var text = await Clipboard.GetTextAsync();
            //     if (text.IsNullOrEmpty()) return;
            //
            //     chatAttachments.Add(new ChatTextAttachment(new DirectLocaleKey(text.SafeSubstring(0, 10)), text));
            // }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add attachment from clipboard");
            ToastExceptionHandler.HandleException(HandledSystemException.Handle(ex));
        }
    }

    [RelayCommand]
    private async Task AddFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            IReadOnlyList<IStorageFile> files;
            IsPickingFiles = true;
            try
            {
                files = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        AllowMultiple = true,
                        FileTypeFilter =
                        [
                            new FilePickerFileType(LocaleResolver.FilePickerFileType_SupportedFiles)
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                    .AsValueEnumerable()
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document))
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                    .Select(x => '*' + x)
                                    .ToList()
                            },
                            new FilePickerFileType(LocaleResolver.ChatWindowViewModel_AddFile_FilePickerFileType_Images)
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                    .AsValueEnumerable()
                                    .Select(x => '*' + x)
                                    .ToList()
                            },
                            new FilePickerFileType(LocaleResolver.ChatWindowViewModel_AddFile_FilePickerFileType_Documents)
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document)
                                    .AsValueEnumerable()
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                    .Select(x => '*' + x)
                                    .ToList()
                            },
                            new FilePickerFileType(LocaleResolver.FilePickerFileType_AllFiles)
                            {
                                Patterns = ["*"]
                            }
                        ]
                    });
            }
            finally
            {
                IsPickingFiles = false;
            }

            if (files.Count <= 0) return;
            if (files[0].TryGetLocalPath() is not { } filePath)
            {
                _logger.LogWarning("File path is not available.");
                return;
            }

            await AddFileUncheckAsync(filePath, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add file");
            ToastExceptionHandler.HandleException(HandledSystemException.Handle(ex));
        }
    }

    /// <summary>
    /// Add a file to the chat attachments without checking the attachment count limit.
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="description"></param>
    /// <param name="cancellationToken"></param>
    private async ValueTask AddFileUncheckAsync(string filePath, string? description = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        FileAttachment attachment;
        try
        {
            attachment = await FileAttachment.CreateAsync(
                filePath,
                description: description,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to create file attachment for {FilePath}", filePath);

            ToastHost
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        _chatAttachmentsSource.Add(attachment);
    }

    /// <summary>
    /// Add a file to the chat attachments from drag and drop.
    /// Checks the attachment count limit.
    /// </summary>
    /// <param name="filePath">The file path to add.</param>
    /// <param name="cancellationToken"></param>
    public async Task AddFileFromDragDropAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            await AddFileUncheckAsync(filePath, "from drag&drop", cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add file from drag&drop");
            ToastExceptionHandler.HandleException(HandledSystemException.Handle(ex));
        }
    }

    private async Task<FileAttachment> CreateFromBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, 100);

        var blob = await _blobStorage.StorageBlobAsync(memoryStream, "image/png", cancellationToken: cancellationToken);
        return new FileAttachment(
            new DynamicLocaleKey(string.Empty),
            blob.LocalPath,
            blob.Sha256,
            blob.MimeType);
    }

    [RelayCommand]
    private void RemoveAttachment(ChatAttachment attachment)
    {
        _chatAttachmentsSource.Remove(attachment);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void SendMessage(string? message)
    {
        if (message is null) return;
        message = message.Trim();

        if (message.Length == 0 && SelectedStrategy is null) return;

        ChatAttachment[]? attachments = null;
        _chatAttachmentsSource.Edit(list =>
        {
            attachments = [..list];
            list.Clear();
        });

        UserChatMessage userMessage;
        if (SelectedStrategy is { } selectedStrategy)
        {
            userMessage = new UserStrategyChatMessage(message, attachments!, selectedStrategy);
            SelectedStrategy = null;
        }
        else
        {
            userMessage = new UserChatMessage(message, attachments!);
        }

        if (EditingUserMessageNode is { } oldNode)
        {
            CancelEditing();
            _chatService.Edit(oldNode, userMessage);
        }
        else
        {
            _chatService.SendMessage(userMessage);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit(ChatMessageNode userChatMessageNode)
    {
        if (userChatMessageNode is not { Message: UserChatMessage userChatMessage }) return;

        var textBeforeEdit = ChatInputAreaText;
        var strategyCommandBeforeEdit = SelectedStrategy;

        EditingUserMessageNode = userChatMessageNode;
        ChatInputAreaText = userChatMessage.Content;
        SelectedStrategy = userChatMessage.As<UserStrategyChatMessage>()?.Strategy;

        _chatAttachmentsSource.Edit(list =>
        {
            _snapshotBeforeEdit = new ChatInputAreaSnapshot(
                textBeforeEdit,
                list.Count == 0 ? null : list.ToList(),
                strategyCommandBeforeEdit);

            list.Reset(userChatMessage.Attachments.Where(a => a is not VisualElementAttachment { IsElementValid: false }));
        });
    }

    [RelayCommand]
    public void CancelEditing()
    {
        if (EditingUserMessageNode is null) return;

        EditingUserMessageNode = null;
        _chatAttachmentsSource.Edit(list =>
        {
            list.Clear();
            if (_snapshotBeforeEdit is { Attachments: { } chatAttachmentsBeforeEditing })
            {
                list.AddRange(chatAttachmentsBeforeEditing);
            }
        });

        ChatInputAreaText = _snapshotBeforeEdit?.Text;
        SelectedStrategy = _snapshotBeforeEdit?.Strategy;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void Retry(ChatMessageNode chatMessageNode)
    {
        _chatService.Retry(chatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void Continue(ChatMessageNode chatMessageNode)
    {
        _chatService.Continue(chatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        ChatContextManager.Current.Cancel();
    }

    [RelayCommand]
    private Task CopyAsync(ChatMessage chatMessage)
    {
        return Clipboard.SetTextAsync(chatMessage.ToString());
    }

    [RelayCommand]
    private static void OpenPluginSettings()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(new ShowWindowMessage(ShowWindowMessage.MainWindow, "ChatPluginPage"));
    }

    [RelayCommand]
    private static void OpenSettings()
    {
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(new ShowWindowMessage(ShowWindowMessage.MainWindow, "SettingsPage"));
    }

    [RelayCommand]
    private async Task ExportMarkdownAsync(ChatContextMetadata metadata, CancellationToken cancellationToken)
    {
        var chatContext = await ChatContextManager.LoadChatContextAsync(metadata, cancellationToken);
        if (chatContext is null)
        {
            ToastHost
                .CreateToast(LocaleResolver.Common_Error)
                .WithContent(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FailedToLoadChatContext)
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTopicName = string.Join("_", (metadata.Topic ?? "chat").Split(Path.GetInvalidFileNameChars()));
        var suggestedFileName = $"{safeTopicName}_{timestamp}.md";
        var exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), suggestedFileName);

        IsPickingFiles = true;
        IStorageFile? storageFile;
        try
        {
            storageFile = await StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    SuggestedFileName = suggestedFileName,
                    FileTypeChoices =
                    [
                        new FilePickerFileType(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FilePickerFileType_Markdown)
                        {
                            Patterns = ["*.md"]
                        },
                        new FilePickerFileType(LocaleResolver.FilePickerFileType_AllFiles)
                        {
                            Patterns = ["*"]
                        }
                    ],
                    DefaultExtension = ".md",
                    SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Documents)
                });
        }
        finally
        {
            IsPickingFiles = false;
        }

        if (storageFile is null) return;
        try
        {
            await using var stream = await storageFile.OpenWriteAsync();
            await ChatContextExporter.ExportAsMarkdown(chatContext, stream, cancellationToken);
            exportPath = storageFile.TryGetLocalPath() ?? exportPath;
        }
        catch (Exception e)
        {
            e = HandledSystemException.Handle(e);

            _logger.LogError(e, "Failed to export chat context to markdown file.");

            ToastHost
                .CreateToast(LocaleResolver.ChatWindowViewModel_ExportMarkdown_FailedToSaveFile)
                .WithContent(e.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        // Show success toast and open file
        ToastHost
            .CreateToast(LocaleResolver.ChatWindowViewModel_ExportMarkdown_ExportSuccess)
            .WithContent(exportPath)
            .DismissOnClick()
            .OnBottomRight()
            .ShowSuccess();

        await Launcher.LaunchFileInfoAsync(new FileInfo(exportPath));
    }

    [RelayCommand]
    private static void Close()
    {
        WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true));
    }

    private void HandleCurrentChatContextIsBusyChanged(bool isBusy)
    {
        IsBusy = isBusy;
    }

    partial void OnIsBusyChanged(bool value)
    {
        SendMessageCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ContinueCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();

        UpdateWatermark(value, SelectedStrategy);
    }

    partial void OnSelectedStrategyChanged(Strategy? value)
    {
        UpdateWatermark(IsBusy, value);
    }

    private void UpdateWatermark(bool isBusy, Strategy? selectedStrategy)
    {
        if (isBusy)
        {
            ChatInputAreaWatermarkKey = _greetings.GetRandomTip();
        }
        else if (selectedStrategy is not null)
        {
            ChatInputAreaWatermarkKey = selectedStrategy.ArgumentHintKey is null ?
                selectedStrategy.DescriptionKey :
                new FormattedDynamicLocaleKey(
                    LocaleKey.ChatInputArea_PlaceholderText_StrategyArgumentHint,
                    selectedStrategy.ArgumentHintKey);
        }
        else
        {
            ChatInputAreaWatermarkKey = _defaultWatermarkKey;
        }
    }

    #region IObserver<TextSelectionData> Implementation

    void IObserver<TextSelectionData>.OnCompleted() { }

    void IObserver<TextSelectionData>.OnError(Exception error) { }

    void IObserver<TextSelectionData>.OnNext(TextSelectionData data)
    {
        if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;
        if (data.Element?.ProcessId == Environment.ProcessId) return; // Ignore selections from this app

        _chatAttachmentsSource.Edit(list =>
        {
            // Remove existing text selection attachment
            list.RemoveWhere(a => a is TextSelectionAttachment);

            // Insert the new attachment at the beginning if it has text
            if (!data.Text.IsNullOrEmpty()) list.Insert(0, new TextSelectionAttachment(data.Text, data.Element));
        });
    }

    #endregion

    #region Strategy Engine

    private void HandleChatAttachmentsChanged(IReadOnlyCollection<ChatAttachment> attachments)
    {
        try
        {
            var context = StrategyContext.FromAttachments(attachments.ToList());
            StrategiesSnapshot = _strategyEngine.GetStrategies(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get strategies for current attachments.");
        }
    }

    [RelayCommand]
    private void SelectStrategy(Strategy strategy)
    {
        SelectedStrategy = strategy;
    }

    #endregion

    private sealed record ChatInputAreaSnapshot(string? Text, IReadOnlyList<ChatAttachment>? Attachments, Strategy? Strategy);
}
