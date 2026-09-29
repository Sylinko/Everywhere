using System.Diagnostics.Metrics;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.ViewModels;

internal readonly record struct VisualElementCaptureRequest(
    VisualElementLocator? Locator,
    VisualElementResolution Resolution = VisualElementResolution.Direct
)
{
    public static VisualElementCaptureRequest Interactive => new(null);
}

public sealed partial class ChatWindowViewModel :
    ReactiveViewModelBase,
    IRecipient<ActivateChatSessionMessage>,
    IObserver<TextSelectionData>
{
    public Settings Settings { get; }

    public PersistentState PersistentState { get; }

    public IChatContextManager ChatContextManager { get; }

    public ChatTextSearchViewModel TextSearch { get; }

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

    internal bool CanAddAttachment => _chatAttachmentsSource.Count < PersistentState.MaxChatAttachmentCount;

    internal Func<VisualElementCaptureRequest, CancellationToken, Task>? VisualElementCaptureRequested { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditMessageNodeCommand))]
    public partial ChatMessageNode? EditingMessageNode { get; private set; }

    public bool CanEdit => !IsBusy && EditingMessageNode is null;

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
            if (EditingMessageNode is null) PersistentState.ChatInputAreaText = value;
        }
    }

    /// <summary>
    /// The resource key for the watermark text in the chat input box.
    /// Can be set to one of greetings or instructions based on the chat context, or a default value.
    /// </summary>
    [ObservableProperty]
    public partial IDynamicLocaleKey ChatInputAreaWatermarkKey { get; private set; }

    public ISoftwareUpdater SoftwareUpdater { get; }

    private readonly IChatService _chatService;
    private readonly IScreenSelectionService _screenSelectionService;
    private readonly IBlobStorage _blobStorage;
    private readonly IStrategyEngine _strategyEngine;
    private readonly IGreetings _greetings;
    private readonly ChatVisualService _visualService;
    private readonly ILogger<ChatWindowViewModel> _logger;
    private readonly DynamicLocaleKey _defaultWatermarkKey = new(LocaleKey.ChatInputArea_PlaceholderText);
    private readonly SourceList<ChatAttachment> _chatAttachmentsSource = new();

    private readonly Meter _meter = new(typeof(ChatWindowViewModel).FullName.NotNull(), App.Version);
    private readonly Gauge<int> _activeChatWindowsGauge;

    private ChatInputAreaSnapshot? _snapshotBeforeEdit;
    private int _textSelectionVersion;

    public ChatWindowViewModel(
        Settings settings,
        PersistentState persistentState,
        IChatContextManager chatContextManager,
        ISoftwareUpdater softwareUpdater,
        IChatService chatService,
        IScreenSelectionService screenSelectionService,
        IBlobStorage blobStorage,
        IStrategyEngine strategyEngine,
        IGreetings greetings,
        ChatVisualService visualService,
        ILogger<ChatWindowViewModel> logger)
    {
        Settings = settings;
        PersistentState = persistentState;
        ChatContextManager = chatContextManager;
        TextSearch = new ChatTextSearchViewModel(chatContextManager);
        LifetimeDisposables.Add(TextSearch);
        SoftwareUpdater = softwareUpdater;

        _chatService = chatService;
        _screenSelectionService = screenSelectionService;
        _blobStorage = blobStorage;
        _strategyEngine = strategyEngine;
        _greetings = greetings;
        _visualService = visualService;
        _logger = logger;

        _activeChatWindowsGauge = _meter.CreateGauge<int>("app.active_chat_windows");

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
        LifetimeDisposables.Add(
            Settings.Model.WhenValueChanged(x => x.SelectedCustomAssistant)
                .Select(assistant => assistant is null ?
                    Observable.Return(0) :
                    assistant.WhenValueChanged(x => x.Configuration.ModelId).Select(_ => 0)
                        .Merge(assistant.WhenValueChanged(x => x.Configuration.ContextLimit).Select(_ => 0)))
                .Switch()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(_ => UpdateCurrentContextUsageModel())
        );
        LifetimeDisposables.Add(
            ChatContextManager.WhenValueChanged(x => x.Current)
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(_ => UpdateCurrentContextUsageModel())
        );

        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            foreach (var attachment in _chatAttachmentsSource.Items.OfType<IDisposable>()) attachment.Dispose();
        }

        base.Dispose(disposing);
    }

    public void Receive(ActivateChatSessionMessage message)
    {
        HandleActivateChatSessionMessageCommand.Execute(message);
    }

    public bool CanAddVisualElement(RemoteVisualAnchor anchor) =>
        _chatAttachmentsSource.Count < PersistentState.MaxChatAttachmentCount &&
        !_chatAttachmentsSource.Items
            .AsValueEnumerable()
            .OfType<VisualElementAttachment>()
            .Any(attachment => attachment is { IsElementValid: true, Anchor: { } existingAnchor } &&
                ReferenceEquals(existingAnchor.Context, anchor.Context) &&
                string.Equals(attachment.InitialSnapshot?.Id, anchor.Snapshot.Id, StringComparison.Ordinal));

    public bool TryAddVisualElementAttachment(VisualElementAttachment attachment)
    {
        if (attachment.Anchor is not { } anchor || !CanAddVisualElement(anchor)) return false;

        _chatAttachmentsSource.Add(attachment);
        return true;
    }

    public bool ContainsAttachment(VisualElementAttachment attachment) =>
        _chatAttachmentsSource.Items.Any(item => ReferenceEquals(item, attachment));

    public void PrepareChatWindowActivation()
    {
        if (!IsOpened && Settings.ChatWindow.AlwaysStartNewChat && ChatContextManager.CreateNewCommand.CanExecute(null))
            ChatContextManager.CreateNewCommand.Execute(null);
    }

    public void ActivateChatWindow()
    {
        PrepareChatWindowActivation();
        WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
    }

    [RelayCommand]
    private async Task HandleActivateChatSessionMessageAsync(ActivateChatSessionMessage message)
    {
        try
        {
            if (message.TargetLocator is not { } targetLocator)
            {
                ActivateChatWindow();
                return;
            }

            if (!Settings.ChatWindow.AutomaticallyAddElement)
            {
                ActivateChatWindow();
                return;
            }

            if (VisualElementCaptureRequested is { } captureRequested)
            {
                await captureRequested(
                    new VisualElementCaptureRequest(targetLocator, message.TargetResolution),
                    CancellationToken.None);
                return;
            }

            ActivateChatWindow();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process ActivateChatSessionMessage");
        }
    }

    [RelayCommand]
    private async Task PickVisualElementAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (VisualElementCaptureRequested is { } captureRequested)
                await captureRequested(VisualElementCaptureRequest.Interactive, cancellationToken);
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
            var bitmap = await _screenSelectionService.TakeScreenshotAsync(_visualService.AcquisitionContext, null, cancellationToken);
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

            var addedFiles = false;
            if (formats.Contains(DataFormat.File))
            {
                foreach (var storageItem in await Clipboard.TryGetFilesAsync() ?? [])
                {
                    var uri = storageItem.Path;
                    if (!uri.IsFile) continue;
                    addedFiles |= await AddFileUncheckAsync(uri.LocalPath, "from clipboard, temporary filepath", cancellationToken);
                    if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                }
            }

            // A copied image file offers a file URL, while a screenshot or an image copied from an app
            // usually offers bitmap data only. Use the bitmap when no file could be added.
            if (!addedFiles && formats.Contains(DataFormat.Bitmap) && await Clipboard.TryGetBitmapAsync() is { } bitmap)
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
                                    .ToArray()
                            },
                            new FilePickerFileType(LocaleResolver.ChatWindowViewModel_AddFile_FilePickerFileType_Images)
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                    .AsValueEnumerable()
                                    .Select(x => '*' + x)
                                    .ToArray()
                            },
                            new FilePickerFileType(LocaleResolver.ChatWindowViewModel_AddFile_FilePickerFileType_Documents)
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document)
                                    .AsValueEnumerable()
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                    .Select(x => '*' + x)
                                    .ToArray()
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

            foreach (var file in files)
            {
                if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                if (file.TryGetLocalPath() is not { } filePath)
                {
                    _logger.LogWarning("File path is not available for {File}.", file.Name);
                    continue;
                }

                await AddFileUncheckAsync(filePath, cancellationToken: cancellationToken);
            }
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
    private async ValueTask<bool> AddFileUncheckAsync(string filePath, string? description = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

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
            return false;
        }

        _chatAttachmentsSource.Add(attachment);
        return true;
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
        bitmap.Save(memoryStream, PngBitmapEncoderOptions.Default);
        if (memoryStream.Length > FileAttachment.MaximumInlineContentSizeInBytes)
        {
            throw new HandledException(
                new NotSupportedException("The image is too large to store as a chat attachment."),
                new FormattedDynamicLocaleKey(
                    LocaleKey.FileAttachment_Create_FileTooLarge,
                    new DirectLocaleKey(Humanizer.HumanizeBytes(memoryStream.Length)),
                    new DirectLocaleKey(Humanizer.HumanizeBytes(FileAttachment.MaximumInlineContentSizeInBytes))),
                showDetails: false);
        }

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
        (attachment as IDisposable)?.Dispose();
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private async Task SendMessage(string? message)
    {
        message = message?.Trim() ?? string.Empty;

        if (message.Length == 0 && SelectedStrategy is null && _chatAttachmentsSource.Count == 0) return;

        var editingMessageNode = EditingMessageNode;
        var submittedStrategy = SelectedStrategy;
        var destinationContext = editingMessageNode?.Context ?? ChatContextManager.Current;
        var submittedAttachments = _chatAttachmentsSource.Items.ToArray();
        try
        {
            foreach (var attachment in submittedAttachments.OfType<VisualElementAttachment>())
            {
                if (attachment.Anchor is not { } sourceAnchor) continue;
                var destinationAnchor = await _visualService.MoveAnchorAsync(destinationContext.VisualState, sourceAnchor);
                if (!ReferenceEquals(sourceAnchor, destinationAnchor)) attachment.ReplaceAnchor(sourceAnchor, destinationAnchor);
            }
        }
        catch (Exception exception)
        {
            if (ChatInputAreaText.IsNullOrEmpty()) ChatInputAreaText = message;
            _logger.LogError(exception, "Failed to move draft visual attachments into the destination chat");
            ToastExceptionHandler.HandleException(HandledSystemException.Handle(exception));
            return;
        }

        UserChatMessage userMessage;
        if (submittedStrategy is not null)
        {
            userMessage = new UserStrategyChatMessage(message, submittedAttachments, submittedStrategy);
        }
        else
        {
            userMessage = new UserChatMessage(message, submittedAttachments);
        }

        var isAccepted = editingMessageNode is not null ?
            _chatService.Edit(editingMessageNode, userMessage) :
            _chatService.SendMessage(destinationContext, userMessage);

        if (!isAccepted)
        {
            if (ChatInputAreaText.IsNullOrEmpty()) ChatInputAreaText = message;
            return;
        }

        _chatAttachmentsSource.Edit(list =>
        {
            foreach (var attachment in submittedAttachments) list.Remove(attachment);
        });
        if (editingMessageNode is not null)
        {
            CompleteEditingSubmission(editingMessageNode, submittedStrategy);
        }
        else if (submittedStrategy is not null && ReferenceEquals(SelectedStrategy, submittedStrategy))
        {
            SelectedStrategy = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void EditMessageNode(ChatMessageNode userChatMessageNode)
    {
        if (userChatMessageNode is not { Message: UserChatMessage userChatMessage }) return;

        var textBeforeEdit = ChatInputAreaText;
        var strategyCommandBeforeEdit = SelectedStrategy;

        EditingMessageNode = userChatMessageNode;
        ChatInputAreaText = userChatMessage.Content;
        SelectedStrategy = userChatMessage.As<UserStrategyChatMessage>()?.Strategy;

        _chatAttachmentsSource.Edit(list =>
        {
            _snapshotBeforeEdit = new ChatInputAreaSnapshot(
                textBeforeEdit,
                list.Count == 0 ? null : list.ToArray(),
                strategyCommandBeforeEdit);

            list.Reset(userChatMessage.Attachments.Where(a => a is not VisualElementAttachment { IsElementValid: false }));
        });
    }

    [RelayCommand]
    public void CancelEditing()
    {
        if (EditingMessageNode is null) return;

        var snapshot = _snapshotBeforeEdit;
        _snapshotBeforeEdit = null;
        EditingMessageNode = null;
        ChatAttachment[] discardedAttachments = [];
        _chatAttachmentsSource.Edit(list =>
        {
            var preservedAttachments = snapshot?.Attachments ?? [];
            var preservedSet = new HashSet<ChatAttachment>(preservedAttachments, ReferenceEqualityComparer.Instance);
            discardedAttachments = list.Where(attachment => !preservedSet.Contains(attachment)).ToArray();
            list.Reset(preservedAttachments);
        });
        foreach (var attachment in discardedAttachments.OfType<IDisposable>()) attachment.Dispose();

        ChatInputAreaText = snapshot?.Text;
        SelectedStrategy = snapshot?.Strategy;
    }

    private void CompleteEditingSubmission(ChatMessageNode editingMessageNode, Strategy? submittedStrategy)
    {
        if (!ReferenceEquals(EditingMessageNode, editingMessageNode)) return;

        var snapshot = _snapshotBeforeEdit;
        _snapshotBeforeEdit = null;
        EditingMessageNode = null;
        _chatAttachmentsSource.Edit(list =>
        {
            var attachmentsAddedWhileSubmitting = list.ToArray();
            list.Reset(snapshot?.Attachments ?? []);
            foreach (var attachment in attachmentsAddedWhileSubmitting)
            {
                if (!list.Contains(attachment)) list.Add(attachment);
            }
        });

        if (ChatInputAreaText.IsNullOrEmpty()) ChatInputAreaText = snapshot?.Text;
        if (ReferenceEquals(SelectedStrategy, submittedStrategy)) SelectedStrategy = snapshot?.Strategy;
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void RetryMessageNode(ChatMessageNode chatMessageNode)
    {
        _chatService.Retry(chatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void ContinueMessageNode(ChatMessageNode chatMessageNode)
    {
        _chatService.Continue(chatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private void CompactContext() => _chatService.CompactContext();

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        ChatContextManager.Current.Cancel();
    }

    [RelayCommand]
    private Task CopyMessageAsync(ChatMessage chatMessage)
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
    private Task PerformUpdateAsync() => SoftwareUpdater.PerformUpdateAsync();

    [RelayCommand]
    private static void Close()
    {
        WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true));
    }

    private void HandleCurrentChatContextIsBusyChanged(bool isBusy)
    {
        IsBusy = isBusy;
    }

    private void UpdateCurrentContextUsageModel()
    {
        var assistant = Settings.Model.SelectedCustomAssistant;
        ChatContextManager.Current.ContextUsage.UpdateModel(
            assistant?.Configuration.ModelId,
            assistant?.Configuration.ContextLimit ?? 0);
    }

    partial void OnIsBusyChanged(bool value)
    {
        SendMessageCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();
        EditMessageNodeCommand.NotifyCanExecuteChanged();
        RetryMessageNodeCommand.NotifyCanExecuteChanged();
        ContinueMessageNodeCommand.NotifyCanExecuteChanged();
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
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            var version = Interlocked.Increment(ref _textSelectionVersion);
            HandleTextSelectionAsync(data, version).Detach(_logger.ToExceptionHandler());
        });
    }

    private async Task HandleTextSelectionAsync(TextSelectionData data, int version)
    {
        RemoteVisualAnchor? anchor = null;
        TextSelectionAttachment? pendingAttachment = null;
        try
        {
            using (data)
            {
                if (!data.IsCurrent || string.IsNullOrEmpty(data.Text)) return;

                if (_chatAttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount &&
                    !_chatAttachmentsSource.Items.AsValueEnumerable().OfType<TextSelectionAttachment>().Any())
                {
                    return;
                }

                anchor = data.TakeSource();
                if (anchor is null && data.Locator is { } locator)
                {
                    anchor = await _visualService.AcquireAnchorAsync(locator, data.Resolution);
                    if (anchor?.Snapshot.ProcessId == Environment.ProcessId) return;
                }

                if (!data.IsCurrent) return;
                if (anchor is not null)
                {
                    pendingAttachment = new TextSelectionAttachment(data.Text, data.IsTextIncomplete, anchor);
                    anchor = null;
                }
                else
                {
                    pendingAttachment = new TextSelectionAttachment(data.Text, data.IsTextIncomplete);
                }
            }

            if (version != Volatile.Read(ref _textSelectionVersion) || !data.IsCurrent) return;

            _chatAttachmentsSource.Edit(list =>
            {
                // Remove existing text selection attachment
                foreach (var attachment in list.OfType<TextSelectionAttachment>().ToArray())
                {
                    list.Remove(attachment);
                    attachment.Dispose();
                }

                // ReSharper disable AccessToDisposedClosure
                // ReSharper disable AccessToModifiedClosure
                list.Insert(0, pendingAttachment);
                // ReSharper restore AccessToDisposedClosure
                // ReSharper restore AccessToModifiedClosure
            });
            pendingAttachment = null;
        }
        finally
        {
            pendingAttachment?.Dispose();
            anchor?.Dispose();
        }
    }

    #endregion

    #region Strategy Engine

    private void HandleChatAttachmentsChanged(IReadOnlyCollection<ChatAttachment> attachments)
    {
        try
        {
            var context = StrategyContext.FromAttachments([.. attachments]);
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