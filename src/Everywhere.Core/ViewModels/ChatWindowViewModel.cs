using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Globalization;
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
using Everywhere.AI;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Cloud;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Harness;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.ViewModels;

public readonly record struct VisualElementCaptureRequest(
    VisualElementLocator? Locator,
    VisualElementResolution Resolution = VisualElementResolution.Direct
)
{
    public static VisualElementCaptureRequest Interactive => new(null);
}

public enum ChatComposerPrimaryMode
{
    Send,
    Resume,
    Queue,
    Steer,
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
    [NotifyPropertyChangedFor(nameof(HasDraftInput))]
    [NotifyPropertyChangedFor(nameof(CanSendDraft))]
    [NotifyPropertyChangedFor(nameof(CanResumeConversation))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryButtonEnabled))]
    public partial bool IsBusy { get; private set; }

    public bool IsNotBusy => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTurnInactive))]
    [NotifyPropertyChangedFor(nameof(CanSendMessage))]
    [NotifyPropertyChangedFor(nameof(CanSendOppositeMode))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanResumeConversation))]
    [NotifyPropertyChangedFor(nameof(PrimaryComposerMode))]
    [NotifyPropertyChangedFor(nameof(IsPrimaryButtonEnabled))]
    public partial bool IsTurnActive { get; private set; }

    public bool IsTurnInactive => !IsTurnActive;

    [ObservableProperty]
    public partial IReadOnlyList<Strategy>? StrategiesSnapshot { get; private set; }

    /// <summary>
    /// Indicates whether the file picker is currently open.
    /// </summary>
    public bool IsPickingFiles { get; private set; }

    public IReadOnlyBindableList<ChatAttachment> ChatAttachments => Input.ActiveDraft.Attachments;

    internal bool CanAddAttachment => ChatAttachmentsSource.Count < PersistentState.MaxChatAttachmentCount;

    internal Func<VisualElementCaptureRequest, CancellationToken, Task>? VisualElementCaptureRequested { get; set; }

    /// <summary>
    /// Gets the edit target in the currently displayed conversation, never in a hidden chat.
    /// </summary>
    public ChatMessageNode? EditingMessageNode => Input.Edit is { } edit ? ChatContextManager.Current.Data.FindNode(edit.NodeId) : null;

    public bool CanEdit => !IsTurnActive && EditingMessageNode is null && CanChangeDraft;

    public bool CanSendMessage => !IsBusy || EditingMessageNode is null;

    public bool HasDraftInput =>
        !string.IsNullOrWhiteSpace(ChatInputAreaText) ||
        SelectedStrategy is not null ||
        ChatAttachmentsSource.Count > 0;

    public bool CanSendDraft => HasDraftInput && CanSendMessage && CanChangeDraft;

    public bool CanSendOppositeMode => IsTurnActive && CanSendDraft;

    public bool CanResumeConversation =>
        !IsTurnActive &&
        !HasDraftInput &&
        EditingMessageNode is null &&
        _chatService.CanResume(ChatContextManager.Current);

    public ChatComposerPrimaryMode PrimaryComposerMode =>
        IsTurnActive ? Settings.ChatWindow.BusySubmissionMode == ChatInputDeliveryMode.Steer
            ? ChatComposerPrimaryMode.Steer
            : ChatComposerPrimaryMode.Queue :
        CanResumeConversation ? ChatComposerPrimaryMode.Resume :
        ChatComposerPrimaryMode.Send;

    public bool IsPrimaryButtonVisible => !IsBusy || CanSendDraft;

    public bool IsPrimaryButtonEnabled => PrimaryComposerMode == ChatComposerPrimaryMode.Resume || CanSendDraft;

    public Strategy? SelectedStrategy
    {
        get => Input.ActiveDraft.SelectedStrategy;
        set => Input.ActiveDraft.SelectedStrategy = value;
    }

    public static int ChatInputAreaTextMaxLength => 100_000;

    /// <summary>
    /// The text in the chat input box.
    /// </summary>
    public string? ChatInputAreaText
    {
        get => Input.ActiveDraft.Text;
        set => Input.ActiveDraft.Text = value.SafeSubstring(0, ChatInputAreaTextMaxLength);
    }

    /// <summary>
    /// The resource key for the watermark text in the chat input box.
    /// Can be set to one of greetings or instructions based on the chat context, or a default value.
    /// </summary>
    [ObservableProperty]
    public partial IDynamicLocaleKey ChatInputAreaWatermarkKey { get; private set; }

    /// <summary>
    /// Gets the localized or literal title displayed in the chat window title bar.
    /// </summary>
    [ObservableProperty]
    public partial IDynamicLocaleKey CurrentTitleKey { get; private set; }

    public ISoftwareUpdater SoftwareUpdater { get; }

    private ChatInputState Input => ChatContextManager.Current.Input;
    private bool CanChangeDraft => !_submittingDrafts.Contains(Input.ActiveDraft);
    private SourceList<ChatAttachment> ChatAttachmentsSource => Input.ActiveDraft.AttachmentsSource;

    private static readonly DynamicLocaleKey NewChatTitleKey = new(LocaleKey.ChatContext_Metadata_Topic_New);
    private static readonly DynamicLocaleKey NewTemporaryChatTitleKey = new(LocaleKey.ChatContext_Temporary_New);
    private static readonly DynamicLocaleKey DefaultChatTitleKey = new(LocaleKey.ChatContext_Metadata_Topic_Default);
    private static readonly DynamicLocaleKey TemporaryChatTitleKey = new(LocaleKey.ChatContext_Temporary);

    private readonly IChatService _chatService;
    private readonly IScreenSelectionService _screenSelectionService;
    private readonly IBlobStorage _blobStorage;
    private readonly IStrategyEngine _strategyEngine;
    private readonly ChatVisualService _visualService;
    private readonly TextSelectionToolbarService _toolbarService;
    private readonly ILogger<ChatWindowViewModel> _logger;
    private readonly DynamicLocaleKey _defaultWatermarkKey = new(LocaleKey.ChatInputArea_PlaceholderText);
    private readonly SerialDisposable _inputSubscriptions = new();
    private readonly SerialDisposable _contextSubscriptions = new();

    // Protect drafts submitting or lending attachments until publication. Standalone toolbar
    // selections do not involve a draft; navigation must not redirect an actual handoff.
    private readonly HashSet<ChatInputDraft> _submittingDrafts = [];

    private readonly Meter _meter = new(typeof(ChatWindowViewModel).FullName.NotNull(), App.Version);
    private readonly Gauge<int> _activeChatWindowsGauge;

    private ChatInputState? _observedInput;

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
        ChatVisualService visualService,
        TextSelectionToolbarService toolbarService,
        ILogger<ChatWindowViewModel> logger,
        IChatSyncService? syncService = null
    )
    {
        Settings = settings;
        PersistentState = persistentState;
        ChatContextManager = chatContextManager;
        SyncService = syncService;
        TextSearch = new ChatTextSearchViewModel(chatContextManager);
        LifetimeDisposables.Add(TextSearch);
        SoftwareUpdater = softwareUpdater;

        _chatService = chatService;
        _screenSelectionService = screenSelectionService;
        _blobStorage = blobStorage;
        _strategyEngine = strategyEngine;
        _visualService = visualService;
        _toolbarService = toolbarService;
        _logger = logger;

        _activeChatWindowsGauge = _meter.CreateGauge<int>("app.active_chat_windows");

        ChatInputAreaWatermarkKey = _defaultWatermarkKey;
        CurrentTitleKey = ResolveTitleKey(ChatContextManager.Current);
        LifetimeDisposables.Add(_inputSubscriptions);
        LifetimeDisposables.Add(_contextSubscriptions);
        LifetimeDisposables.Add(
            ChatContextManager.WhenValueChanged(manager => manager.Current)
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(context =>
                {
                    if (context is not null)
                    {
                        ObserveInput(context.Input);
                        ObserveContextState(context);
                    }
                }));

        LifetimeDisposables.Add(
            ChatContextManager.WhenValueChanged(x => x.Current)
                .Select(context => context is { } runtime ? runtime.WhenValueChanged(x => x.IsBusy) : Observable.Return(false))
                .Switch()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(HandleCurrentChatContextIsBusyChanged)
        );
        LifetimeDisposables.Add(
            ChatContextManager.WhenValueChanged(x => x.Current)
                .Select(context => context is { } runtime ? runtime.WhenValueChanged(x => x.IsTurnActive) : Observable.Return(false))
                .Switch()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(HandleCurrentChatContextIsTurnActiveChanged)
        );
        LifetimeDisposables.Add(
            Settings.Model.WhenValueChanged(x => x.SelectedCustomAssistant)
                .Select(assistant => assistant is null
                    ? Observable.Return(0)
                    : assistant.WhenValueChanged(x => x.Configuration.ModelId)
                        .Select(_ => 0)
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

        LifetimeDisposables.Add(
            Settings.ChatWindow.WhenValueChanged(x => x.BusySubmissionMode)
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(_ => OnPropertyChanged(nameof(PrimaryComposerMode)))
        );

        InitializeToolApproval();
        WeakReferenceMessenger.Default.RegisterAll(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            if (_observedInput is not null)
            {
                _observedInput.PropertyChanged -= HandleInputPropertyChanged;
            }
        }

        base.Dispose(disposing);
    }

    public void Receive(ActivateChatSessionMessage message)
    {
        HandleActivateChatSessionMessageCommand.Execute(message);
    }

    public bool CanAddVisualElement(RemoteVisualAnchor anchor) =>
        ChatAttachmentsSource.Count < PersistentState.MaxChatAttachmentCount &&
        !ChatAttachmentsSource.Items
            .AsValueEnumerable()
            .OfType<VisualElementAttachment>()
            .Any(attachment => attachment is { IsElementValid: true, Anchor: { } existingAnchor } &&
                ReferenceEquals(existingAnchor.Context, anchor.Context) &&
                string.Equals(attachment.InitialSnapshot?.Id, anchor.Snapshot.Id, StringComparison.Ordinal));

    public bool TryAddVisualElementAttachment(VisualElementAttachment attachment)
    {
        if (attachment.Anchor is not { } anchor || !CanAddVisualElement(anchor)) return false;

        ChatAttachmentsSource.Add(attachment);
        return true;
    }

    public bool ContainsAttachment(VisualElementAttachment attachment) =>
        ChatAttachmentsSource.Items.Any(item => ReferenceEquals(item, attachment));

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
        var destination = ChatContextManager.Current;
        var draft = destination.Input.ActiveDraft;
        try
        {
            if (draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

            // Hide the chat window to avoid picking itself
            var isOpened = IsOpened;
            if (isOpened) WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true));
            var bitmap = default(Bitmap?);
            try
            {
                bitmap = await _screenSelectionService.TakeScreenshotAsync(_visualService.AcquisitionContext, null, cancellationToken);
            }
            finally
            {
                if (isOpened || bitmap is not null)
                {
                    WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
                }
            }

            using var screenshot = bitmap;
            if (bitmap is null) return;

            AddImportedAttachment(destination, draft, await Task.Run(() => CreateFromBitmapAsync(bitmap, cancellationToken), cancellationToken));
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
        var destination = ChatContextManager.Current;
        var draft = destination.Input.ActiveDraft;
        try
        {
            if (draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

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
                    addedFiles |= await ImportFileAsync(destination, draft, uri.LocalPath, "from clipboard, temporary filepath", cancellationToken);
                    if (draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                }
            }

            // A copied image file offers a file URL, while a screenshot or an image copied from an app
            // usually offers bitmap data only. Use the bitmap when no file could be added.
            if (!addedFiles && formats.Contains(DataFormat.Bitmap) && await Clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                AddImportedAttachment(destination, draft, await Task.Run(() => CreateFromBitmapAsync(bitmap, cancellationToken), cancellationToken));
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
        var destination = ChatContextManager.Current;
        var draft = destination.Input.ActiveDraft;
        try
        {
            if (draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) return;

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
                            new FilePickerFileType(LocaleKey.FilePickerFileType_SupportedFiles.I18N())
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                    .AsValueEnumerable()
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document))
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                    .Select(x => '*' + x)
                                    .ToArray()
                            },
                            new FilePickerFileType(LocaleKey.ChatWindowViewModel_AddFile_FilePickerFileType_Images.I18N())
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Image)
                                    .AsValueEnumerable()
                                    .Select(x => '*' + x)
                                    .ToArray()
                            },
                            new FilePickerFileType(LocaleKey.ChatWindowViewModel_AddFile_FilePickerFileType_Documents.I18N())
                            {
                                Patterns = FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Document)
                                    .AsValueEnumerable()
                                    .Concat(FileUtilities.GetFileExtensionsByCategory(FileTypeCategory.Script))
                                    .Select(x => '*' + x)
                                    .ToArray()
                            },
                            new FilePickerFileType(LocaleKey.FilePickerFileType_AllFiles.I18N())
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
                if (draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                if (file.TryGetLocalPath() is not { } filePath)
                {
                    _logger.LogWarning("File path is not available for {File}.", file.Name);
                    continue;
                }

                await ImportFileAsync(destination, draft, filePath, cancellationToken: cancellationToken);
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
    /// Imports a file into the captured draft, checking its attachment limit when the result arrives.
    /// </summary>
    /// <param name="destination">The conversation captured before opening a picker or reading the clipboard.</param>
    /// <param name="draft">The ordinary or editing draft that requested the import.</param>
    /// <param name="filePath"></param>
    /// <param name="description"></param>
    /// <param name="cancellationToken"></param>
    private async ValueTask<bool> ImportFileAsync(
        ChatContext destination,
        ChatInputDraft draft,
        string filePath,
        string? description = null,
        CancellationToken cancellationToken = default
    )
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
                .CreateToast(LocaleKey.Common_Error.I18N())
                .WithContent(ex.GetFriendlyMessage().ToTextBlock())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return false;
        }

        return AddImportedAttachment(destination, draft, attachment);
    }

    /// <summary>
    /// Imports one drop batch into the conversation and draft that received it.
    /// Checks the attachment count limit.
    /// </summary>
    /// <param name="filePaths">The local files from this drop.</param>
    /// <param name="cancellationToken"></param>
    public async Task AddFilesFromDragDropAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken)
    {
        var destination = ChatContextManager.Current;
        var draft = destination.Input.ActiveDraft;
        try
        {
            foreach (var filePath in filePaths)
            {
                if (draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount) break;
                await ImportFileAsync(destination, draft, filePath, "from drag&drop", cancellationToken);
            }
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
        if (memoryStream.Length > BlobStorage.MaximumCopiedBlobSizeInBytes)
        {
            throw new HandledException(
                new NotSupportedException("The image is too large to store as a chat attachment."),
                new FormattedDynamicLocaleKey(
                    LocaleKey.FileAttachment_Create_FileTooLarge,
                    new DirectLocaleKey(Humanizer.HumanizeBytes(memoryStream.Length)),
                    new DirectLocaleKey(Humanizer.HumanizeBytes(BlobStorage.MaximumCopiedBlobSizeInBytes))),
                showDetails: false);
        }

        var blob = await _blobStorage.StorageBlobAsync(memoryStream, "image/png", cancellationToken: cancellationToken);
        return new FileAttachment(
            new DynamicLocaleKey(string.Empty),
            blob.LocalPath,
            blob.Sha256,
            blob.MimeType);
    }

    private bool AddImportedAttachment(ChatContext destination, ChatInputDraft draft, FileAttachment attachment)
    {
        // Navigation does not redirect an import. A canceled edit or deleted chat no longer
        // owns its old draft; imported files retain their ordinary file/blob storage lifetime.
        if (destination.Data.IsDisposed ||
            !ReferenceEquals(destination.Input.Draft, draft) && !ReferenceEquals(destination.Input.Edit?.Draft, draft) ||
            draft.AttachmentsSource.Count >= PersistentState.MaxChatAttachmentCount)
        {
            return false;
        }

        draft.AttachmentsSource.Add(attachment);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanChangeDraft))]
    private void RemoveAttachment(ChatAttachment attachment)
    {
        Input.ActiveDraft.RemoveAttachment(attachment);
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private Task SendMessage(string? message)
    {
        return SubmitMessageWithModeAsync(message, Settings.ChatWindow.BusySubmissionMode);
    }

    [RelayCommand(CanExecute = nameof(CanSendOppositeMode))]
    private Task SendMessageOppositeMode(string? message)
    {
        var oppositeMode = Settings.ChatWindow.BusySubmissionMode == ChatInputDeliveryMode.Steer
            ? ChatInputDeliveryMode.Queue
            : ChatInputDeliveryMode.Steer;
        return SubmitMessageWithModeAsync(message, oppositeMode);
    }

    [RelayCommand(CanExecute = nameof(IsPrimaryButtonEnabled))]
    private Task PrimaryComposerAction()
    {
        if (CanResumeConversation)
        {
            ResumeConversation();
            return Task.CompletedTask;
        }

        return SendMessage(ChatInputAreaText);
    }

    private async Task SubmitMessageWithModeAsync(string? message, ChatInputDeliveryMode busyDeliveryMode)
    {
        message = message?.Trim() ?? string.Empty;
        if (message.Length == 0 && SelectedStrategy is null && ChatAttachmentsSource.Count == 0) return;

        var destinationContext = ChatContextManager.Current;
        var input = destinationContext.Input;
        var draft = input.ActiveDraft;
        var submittedText = draft.Text;
        var editingMessageNode = input.Edit is { } edit ? destinationContext.Data.FindNode(edit.NodeId) : null;
        var submittedStrategy = draft.SelectedStrategy;
        var submittedAttachments = draft.AttachmentsSource.Items.ToArray();
        UserChatMessage userMessage;
        if (submittedStrategy is not null)
        {
            userMessage = new UserStrategyChatMessage(message, submittedAttachments, submittedStrategy);
        }
        else
        {
            userMessage = new UserChatMessage(message, submittedAttachments);
        }

        if (!_submittingDrafts.Add(draft)) return;
        NotifyDraftCommandsChanged();

        try
        {
            // Wait for the local model operation, not visual RPC, generation or persistence.
            // Clearing earlier could persist an empty input before its message joins the graph.
            // Shutdown drains root work without waiting for this UI continuation. Exiting in
            // this gap may restore a redundant draft rather than discard accepted user input.
            bool isAccepted;
            if (editingMessageNode is not null)
            {
                isAccepted = await _chatService.EditAsync(destinationContext, editingMessageNode, userMessage);
            }
            else if (destinationContext.IsTurnActive)
            {
                isAccepted = await _chatService.SubmitMessageAsync(
                    destinationContext,
                    userMessage,
                    Settings.Model.SelectedCustomAssistant,
                    busyDeliveryMode);
            }
            else
            {
                isAccepted = await _chatService.SendMessageAsync(destinationContext, userMessage, Settings.Model.SelectedCustomAssistant);
            }

            if (!isAccepted || destinationContext.Data.IsDisposed)
            {
                return;
            }

            input.CompleteSubmission(draft, submittedText, userMessage);
        }
        finally
        {
            _submittingDrafts.Remove(draft);
            NotifyDraftCommandsChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeConversation))]
    private void ResumeConversation()
    {
        _chatService.Resume(ChatContextManager.Current);
    }

    [RelayCommand]
    private void TogglePendingInputDeliveryMode(ChatPendingInputItem item)
    {
        ChatContextManager.Current.PendingInputs.TryToggleDeliveryMode(item.Id, out _);
    }

    [RelayCommand]
    private void WithdrawPendingInput(ChatPendingInputItem item)
    {
        var context = ChatContextManager.Current;
        if (!context.PendingInputs.TryWithdraw(item.Id, out var withdrawn)) return;

        var draft = context.Input.ActiveDraft;
        var restoredText = withdrawn.Message.Content;
        if (!string.IsNullOrEmpty(restoredText))
        {
            draft.Text = string.IsNullOrWhiteSpace(draft.Text) ? restoredText : $"{draft.Text}\n{restoredText}";
        }

        if (withdrawn.Message is UserStrategyChatMessage strategyMessage && draft.SelectedStrategy is null)
        {
            draft.SelectedStrategy = strategyMessage.Strategy;
        }

        if (withdrawn.Message.Attachments.Count > 0)
        {
            draft.AttachmentsSource.Edit(list =>
            {
                foreach (var attachment in withdrawn.Message.Attachments)
                {
                    if (!list.Contains(attachment) && list.Count < PersistentState.MaxChatAttachmentCount)
                    {
                        list.Add(attachment);
                    }
                    else if (attachment is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            });
        }
    }

    [RelayCommand]
    private void RemovePendingInput(ChatPendingInputItem item)
    {
        _chatService.RemovePendingInput(ChatContextManager.Current, item.Id);
    }

    [RelayCommand]
    private void ClearPendingInputs()
    {
        _chatService.ClearPendingInputs(ChatContextManager.Current);
    }

    [RelayCommand]
    private void ResumePendingInputs()
    {
        _chatService.ResumePendingInputs(ChatContextManager.Current);
    }

    [RelayCommand]
    private static void StopBackgroundJob(ChatBackgroundJob job)
    {
        job.RequestStop();
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void EditMessageNode(ChatMessageNode userChatMessageNode)
    {
        if (!userChatMessageNode.IsTurnStart) return;
        Input.BeginEdit(userChatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(CanChangeDraft))]
    public void CancelEditing()
    {
        // Escape calls this directly, bypassing the command's CanExecute check.
        if (!CanChangeDraft) return;
        Input.EndEdit();
    }

    [RelayCommand(CanExecute = nameof(IsTurnInactive))]
    private void RetryMessageNode(ChatMessageNode chatMessageNode)
    {
        _chatService.Retry(ChatContextManager.Current, chatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(IsTurnInactive))]
    private void ContinueMessageNode(ChatMessageNode chatMessageNode)
    {
        _chatService.Continue(ChatContextManager.Current, chatMessageNode);
    }

    [RelayCommand(CanExecute = nameof(IsTurnInactive))]
    private void CompactContext() => _chatService.CompactContext();

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel()
    {
        ChatContextManager.Current.Cancel();
    }

    [RelayCommand]
    private Task CopyMessageNodeAsync(ChatMessageNode node)
    {
        return Clipboard.SetTextAsync(node.Message.ToString());
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
    private async Task ExportMarkdownAsync(ChatMetadata metadata, CancellationToken cancellationToken)
    {
        var chatContext = await ChatContextManager.LoadChatContextAsync(metadata, cancellationToken);
        if (chatContext is null)
        {
            ToastHost
                .CreateToast(LocaleKey.Common_Error.I18N())
                .WithContent(LocaleKey.ChatWindowViewModel_ExportMarkdown_FailedToLoadChatContext.I18N())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
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
                        new FilePickerFileType(LocaleKey.ChatWindowViewModel_ExportMarkdown_FilePickerFileType_Markdown.I18N())
                        {
                            Patterns = ["*.md"]
                        },
                        new FilePickerFileType(LocaleKey.FilePickerFileType_AllFiles.I18N())
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
            await ChatDataExporter.ExportAsMarkdown(chatContext, stream, cancellationToken);
            exportPath = storageFile.TryGetLocalPath() ?? exportPath;
        }
        catch (Exception e)
        {
            e = HandledSystemException.Handle(e);

            _logger.LogError(e, "Failed to export chat data to markdown file.");

            ToastHost
                .CreateToast(LocaleKey.ChatWindowViewModel_ExportMarkdown_FailedToSaveFile.I18N())
                .WithContent(e.GetFriendlyMessage())
                .DismissOnClick()
                .OnBottomRight()
                .ShowError();
            return;
        }

        // Show success toast and open file
        ToastHost
            .CreateToast(LocaleKey.ChatWindowViewModel_ExportMarkdown_ExportSuccess.I18N())
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
        UpdateCurrentTitleKey();
    }

    private void HandleCurrentChatContextIsTurnActiveChanged(bool isTurnActive)
    {
        IsTurnActive = isTurnActive;
    }

    private void UpdateCurrentContextUsageModel()
    {
        var assistant = Settings.Model.SelectedCustomAssistant;
        ChatContextManager.Current.UpdateContextUsageModelAsync(
                assistant?.Configuration.ModelId,
                assistant?.Configuration.ContextLimit ?? 0)
            .Detach(_logger.ToExceptionHandler());
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSendMessage));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendMessageOppositeModeCommand.NotifyCanExecuteChanged();
        PrimaryComposerActionCommand.NotifyCanExecuteChanged();
        ResumeConversationCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();
        RetryMessageNodeCommand.NotifyCanExecuteChanged();
        ContinueMessageNodeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTurnActiveChanged(bool value)
    {
        SendMessageCommand.NotifyCanExecuteChanged();
        SendMessageOppositeModeCommand.NotifyCanExecuteChanged();
        PrimaryComposerActionCommand.NotifyCanExecuteChanged();
        ResumeConversationCommand.NotifyCanExecuteChanged();
        EditMessageNodeCommand.NotifyCanExecuteChanged();
        CompactContextCommand.NotifyCanExecuteChanged();
        RetryMessageNodeCommand.NotifyCanExecuteChanged();
        ContinueMessageNodeCommand.NotifyCanExecuteChanged();

        UpdateWatermark(value, SelectedStrategy);
    }

    private void ObserveContextState(ChatContext context)
    {
        void HandleStateChanged() => Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (ReferenceEquals(ChatContextManager.Current, context))
            {
                NotifyComposerStateChanged();
                UpdateCurrentTitleKey();
            }
        });

        void HandleBranchChanged(object? sender, EventArgs e) => HandleStateChanged();

        void HandleMetadataPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ChatMetadata.Topic) or nameof(ChatMetadata.IsTemporary))
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (ReferenceEquals(ChatContextManager.Current, context))
                    {
                        UpdateCurrentTitleKey();
                    }
                });
            }
        }

        context.Data.BranchChanged += HandleBranchChanged;
        context.PendingInputs.DrainStateChanged += HandleStateChanged;
        context.Data.Metadata.PropertyChanged += HandleMetadataPropertyChanged;
        _contextSubscriptions.Disposable = Disposable.Create(() =>
        {
            context.Data.BranchChanged -= HandleBranchChanged;
            context.PendingInputs.DrainStateChanged -= HandleStateChanged;
            context.Data.Metadata.PropertyChanged -= HandleMetadataPropertyChanged;
        });

        NotifyComposerStateChanged();
        UpdateCurrentTitleKey();
    }

    private void UpdateCurrentTitleKey()
    {
        var nextKey = ResolveTitleKey(ChatContextManager.Current);
        if (CurrentTitleKey.GetType() != nextKey.GetType() || !Equals(CurrentTitleKey, nextKey))
        {
            CurrentTitleKey = nextKey;
        }
    }

    private static IDynamicLocaleKey ResolveTitleKey(ChatContext context)
    {
        var metadata = context.Data.Metadata;
        if (!string.IsNullOrWhiteSpace(metadata.Topic))
        {
            return new DirectLocaleKey(metadata.Topic);
        }

        var hasStarted = context.IsBusy || context.Data.HasHistory || metadata.IsPersisted;
        return (hasStarted, metadata.IsTemporary) switch
        {
            (false, false) => NewChatTitleKey,
            (false, true) => NewTemporaryChatTitleKey,
            (true, false) => DefaultChatTitleKey,
            (true, true) => TemporaryChatTitleKey
        };
    }

    private void ObserveInput(ChatInputState input)
    {
        if (_observedInput is not null)
        {
            _observedInput.PropertyChanged -= HandleInputPropertyChanged;
        }

        _observedInput = input;
        input.PropertyChanged += HandleInputPropertyChanged;
        ObserveActiveDraft();
    }

    private void HandleInputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatInputState.ActiveDraft))
        {
            ObserveActiveDraft();
        }
        else if (e.PropertyName == nameof(ChatInputState.Edit))
        {
            OnPropertyChanged(nameof(EditingMessageNode));
            NotifyComposerStateChanged();
        }
    }

    private void ObserveActiveDraft()
    {
        var draft = Input.ActiveDraft;
        var subscriptions = new CompositeDisposable();
        draft.PropertyChanged += HandleDraftPropertyChanged;
        subscriptions.Add(Disposable.Create(() => draft.PropertyChanged -= HandleDraftPropertyChanged));
        subscriptions.Add(
            draft.AttachmentsSource.Connect()
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(_ => NotifyComposerStateChanged()));
        subscriptions.Add(
            draft.AttachmentsSource.Connect()
                .ToCollection()
                // Empty lists do not emit an initial change set. Initialize both projections from
                // this draft's current snapshot, including when switching back to an empty composer.
                .StartWith(draft.AttachmentsSource.Items)
                .Do(_toolbarService.ValidateChatSelection)
                .ThrottleWithLeadingEdge(TimeSpan.FromMilliseconds(250))
                .ObserveOn(TaskPoolScheduler.Default)
                .Select(GetAttachmentStrategies)
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(strategies => StrategiesSnapshot = strategies));
        _inputSubscriptions.Disposable = subscriptions;

        OnPropertyChanged(nameof(ChatAttachments));
        OnPropertyChanged(nameof(ChatInputAreaText));
        OnPropertyChanged(nameof(EditingMessageNode));
        OnPropertyChanged(nameof(SelectedStrategy));
        NotifyDraftCommandsChanged();
        UpdateWatermark(IsTurnActive, draft.SelectedStrategy);
    }

    private void NotifyDraftCommandsChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSendMessage));
        NotifyComposerStateChanged();
        EditMessageNodeCommand.NotifyCanExecuteChanged();
        CancelEditingCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
    }

    private void NotifyComposerStateChanged()
    {
        OnPropertyChanged(nameof(HasDraftInput));
        OnPropertyChanged(nameof(CanSendDraft));
        OnPropertyChanged(nameof(CanSendOppositeMode));
        OnPropertyChanged(nameof(CanResumeConversation));
        OnPropertyChanged(nameof(PrimaryComposerMode));
        OnPropertyChanged(nameof(IsPrimaryButtonVisible));
        OnPropertyChanged(nameof(IsPrimaryButtonEnabled));
        SendMessageCommand.NotifyCanExecuteChanged();
        SendMessageOppositeModeCommand.NotifyCanExecuteChanged();
        PrimaryComposerActionCommand.NotifyCanExecuteChanged();
        ResumeConversationCommand.NotifyCanExecuteChanged();
    }

    private void HandleDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatInputDraft.Text))
        {
            OnPropertyChanged(nameof(ChatInputAreaText));
            NotifyComposerStateChanged();
        }
        else if (e.PropertyName == nameof(ChatInputDraft.SelectedStrategy))
        {
            OnPropertyChanged(nameof(SelectedStrategy));
            NotifyComposerStateChanged();
            UpdateWatermark(IsTurnActive, SelectedStrategy);
        }
    }

    private void UpdateWatermark(bool isTurnActive, Strategy? selectedStrategy)
    {
        if (isTurnActive)
        {
            ChatInputAreaWatermarkKey = new DynamicLocaleKey(LocaleKey.ChatInputArea_Watermark_Busy);
        }
        else if (selectedStrategy is not null)
        {
            ChatInputAreaWatermarkKey = selectedStrategy.ArgumentHintKey is null
                ? selectedStrategy.DescriptionKey
                : new FormattedDynamicLocaleKey(
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
            var version = ++_textSelectionVersion;
            _toolbarService.Hide();
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

            if (version != _textSelectionVersion || !data.IsCurrent) return;

            var shouldAttach = CanChangeDraft && Settings.TextSelection.IsAutomaticAttachmentEnabled &&
                (ChatAttachmentsSource.Count < PersistentState.MaxChatAttachmentCount ||
                    ChatAttachmentsSource.Items.AsValueEnumerable().OfType<TextSelectionAttachment>().Any());
            if (shouldAttach)
            {
                ChatAttachmentsSource.Edit(list =>
                {
                    foreach (var attachment in list.AsValueEnumerable().OfType<TextSelectionAttachment>().ToArray())
                    {
                        list.Remove(attachment);
                        attachment.Dispose();
                    }

                    // ReSharper disable once AccessToDisposedClosure
                    // ReSharper disable once AccessToModifiedClosure
                    list.Insert(0, pendingAttachment);
                });
            }

            // The toolbar borrows draft-owned attachments; otherwise it takes ownership until dismissed or invoked.
            _toolbarService.Show(data, pendingAttachment, shouldAttach);
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

    /// <summary>
    /// Submits a standalone selection message without consuming or replacing the main window's draft.
    /// Completes after publication, when the chat service owns its attachments and a borrowed selection leaves the draft.
    /// On rejection, the caller retains ownership.
    /// This method must be called on the UI thread.
    /// </summary>
    public async Task<bool> SendTextSelectionMessageAsync(UserChatMessage message, CustomAssistant? assistant)
    {
        var source = ChatContextManager.Current;
        var sourceDraft = source.Input.ActiveDraft;
        var hasBorrowedAttachments = message.Attachments.Any(sourceDraft.AttachmentsSource.Items.Contains);
        if (hasBorrowedAttachments)
        {
            if (!_submittingDrafts.Add(sourceDraft)) return false;
            NotifyDraftCommandsChanged();
        }

        try
        {
            var destination = ChatContextManager.CreateChat(Settings.TextSelection.ShouldStartToolbarChatsAsTemporary);
            if (!await _chatService.SendMessageAsync(destination, message, assistant)) return false;

            // Only adopted references leave the captured source. Never consume its text,
            // strategy, edit state or singleton draft slot, even when navigation changed.
            if (hasBorrowedAttachments && !source.Data.IsDisposed &&
                (ReferenceEquals(source.Input.Draft, sourceDraft) || ReferenceEquals(source.Input.Edit?.Draft, sourceDraft)))
            {
                sourceDraft.AttachmentsSource.Edit(list =>
                {
                    foreach (var attachment in message.Attachments) list.Remove(attachment);
                });
            }

            return true;
        }
        finally
        {
            if (hasBorrowedAttachments)
            {
                _submittingDrafts.Remove(sourceDraft);
                NotifyDraftCommandsChanged();
            }
        }
    }

    private IReadOnlyList<Strategy> GetAttachmentStrategies(IReadOnlyCollection<ChatAttachment> attachments)
    {
        try
        {
            var context = StrategyContext.FromAttachments([.. attachments]);
            return _strategyEngine.GetStrategies(context);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get strategies for current attachments.");
            return [];
        }
    }

    [RelayCommand]
    private void SelectStrategy(Strategy strategy)
    {
        SelectedStrategy = strategy;
    }

    #endregion

}
