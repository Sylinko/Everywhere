using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Common;
using Everywhere.Messages;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Selects the active prompt for an assistant from the compact ChatWindow surface.
/// </summary>
/// <remarks>
/// The control owns only the short-lived prompt list needed by its flyout. The persisted prompt ID
/// remains on <see cref="CustomAssistant"/> and is never rewritten while the list is loading or when
/// a referenced prompt no longer exists.
/// </remarks>
[TemplatePart(Name = PromptButtonPartName, Type = typeof(Button), IsRequired = true)]
public sealed partial class ChatAssistantPromptSelector(IPromptService promptService) : TemplatedControl
{
    private const string PromptButtonPartName = "PART_PromptButton";

    public static readonly StyledProperty<CustomAssistant?> AssistantProperty =
        AvaloniaProperty.Register<ChatAssistantPromptSelector, CustomAssistant?>(nameof(Assistant));

    public static readonly DirectProperty<ChatAssistantPromptSelector, IReadOnlyList<Item>> ItemsSourceProperty =
        AvaloniaProperty.RegisterDirect<ChatAssistantPromptSelector, IReadOnlyList<Item>>(
            nameof(ItemsSource),
            control => control.ItemsSource);

    public static readonly DirectProperty<ChatAssistantPromptSelector, Item?> SelectedItemProperty =
        AvaloniaProperty.RegisterDirect<ChatAssistantPromptSelector, Item?>(
            nameof(SelectedItem),
            control => control.SelectedItem);

    public static readonly DirectProperty<ChatAssistantPromptSelector, IDynamicLocaleKey> SelectedPromptNameKeyProperty =
        AvaloniaProperty.RegisterDirect<ChatAssistantPromptSelector, IDynamicLocaleKey>(
            nameof(SelectedPromptNameKey),
            control => control.SelectedPromptNameKey);

    public static readonly DirectProperty<ChatAssistantPromptSelector, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<ChatAssistantPromptSelector, bool>(
            nameof(IsLoading),
            control => control.IsLoading);

    public static readonly DirectProperty<ChatAssistantPromptSelector, IDynamicLocaleKey> LoadErrorProperty =
        AvaloniaProperty.RegisterDirect<ChatAssistantPromptSelector, IDynamicLocaleKey>(
            nameof(LoadErrorKey),
            control => control.LoadErrorKey);

    public CustomAssistant? Assistant
    {
        get => GetValue(AssistantProperty);
        set => SetValue(AssistantProperty, value);
    }

    public IReadOnlyList<Item> ItemsSource
    {
        get;
        private set => SetAndRaise(ItemsSourceProperty, ref field, value);
    } = [];

    public Item? SelectedItem
    {
        get;
        private set => SetAndRaise(SelectedItemProperty, ref field, value);
    }

    public IDynamicLocaleKey SelectedPromptNameKey
    {
        get;
        private set => SetAndRaise(SelectedPromptNameKeyProperty, ref field, value);
    } = DirectLocaleKey.Empty;

    public bool IsLoading
    {
        get;
        private set => SetAndRaise(IsLoadingProperty, ref field, value);
    }

    public IDynamicLocaleKey LoadErrorKey
    {
        get;
        private set => SetAndRaise(LoadErrorProperty, ref field, value);
    } = DirectLocaleKey.Empty;

    private CustomAssistant? _subscribedAssistant;
    private FlyoutBase? _flyout;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private bool _hasLoaded;
    private bool _isAttached;

    public ChatAssistantPromptSelector() : this(ServiceLocator.Resolve<IPromptService>()) { }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != AssistantProperty) return;

        if (_isAttached) BindAssistant(Assistant);
        ResolveSelection();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        _flyout?.Opened -= HandleFlyoutOpened;

        base.OnApplyTemplate(e);

        _flyout = e.NameScope.Find<Button>(PromptButtonPartName)?.Flyout;
        _flyout?.Opened += HandleFlyoutOpened;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttached = true;
        BindAssistant(Assistant);
        QueueRefreshPrompts();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        BindAssistant(null);
        CancelRefreshPrompts();

        base.OnDetachedFromVisualTree(e);
    }

    [RelayCommand]
    private void SelectPrompt(Item item)
    {
        var assistant = Assistant;
        if (assistant is null) return;

        assistant.SystemPromptId = item.Id;
        ResolveSelection();
        _flyout?.Hide();
    }

    [RelayCommand]
    private void ManagePrompts()
    {
        var route = MainViewNavigateMessage.ToPrompt(Assistant?.SystemPromptId ?? Guid.Empty);
        _flyout?.Hide();

        // ChatWindow can be open while the main window is hidden or has not been created yet.
        // Show it before navigating; a MainViewNavigateMessage alone does not open the window.
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(new ShowWindowMessage(ShowWindowMessage.MainWindow, route));
    }

    private void BindAssistant(CustomAssistant? assistant)
    {
        if (ReferenceEquals(_subscribedAssistant, assistant)) return;

        _subscribedAssistant?.PropertyChanged -= HandleAssistantPropertyChanged;
        _subscribedAssistant = assistant;
        _subscribedAssistant?.PropertyChanged += HandleAssistantPropertyChanged;
    }

    private void HandleAssistantPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CustomAssistant.SystemPromptId)) return;

        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (ReferenceEquals(sender, _subscribedAssistant)) ResolveSelection();
        });
    }

    private void HandleFlyoutOpened(object? sender, EventArgs e)
    {
        QueueRefreshPrompts();
    }

    private void QueueRefreshPrompts()
    {
        if (!_isAttached || _loadCancellationTokenSource is not null) return;

        var cancellationTokenSource = new CancellationTokenSource();
        _loadCancellationTokenSource = cancellationTokenSource;
        IsLoading = true;
        LoadErrorKey = DirectLocaleKey.Empty;
        RefreshPromptsAsync(cancellationTokenSource).Detach();
    }

    private void CancelRefreshPrompts()
    {
        var cancellationTokenSource = _loadCancellationTokenSource;
        _loadCancellationTokenSource = null;
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        IsLoading = false;
    }

    private async Task RefreshPromptsAsync(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            var prompts = await promptService.ListPromptsAsync(cancellationTokenSource.Token);
            if (!ReferenceEquals(_loadCancellationTokenSource, cancellationTokenSource)) return;

            ItemsSource = [.. prompts.Select(Item.FromPrompt)];
            _hasLoaded = true;
            ResolveSelection();
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_loadCancellationTokenSource, cancellationTokenSource)) return;

            var handledException = HandledSystemException.Handle(ex);
            if (ItemsSource.Count == 0) LoadErrorKey = handledException.GetFriendlyMessage();
            Log.Logger.ForContext<ChatAssistantPromptSelector>().Warning(
                handledException,
                "Failed to load prompts for the ChatWindow assistant selector.");
        }
        finally
        {
            if (ReferenceEquals(_loadCancellationTokenSource, cancellationTokenSource))
            {
                _loadCancellationTokenSource = null;
                IsLoading = false;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private void ResolveSelection()
    {
        var assistant = Assistant;
        if (assistant is null)
        {
            SelectedItem = null;
            SelectedPromptNameKey = DirectLocaleKey.Empty;
            return;
        }

        SelectedItem = ItemsSource.FirstOrDefault(item => item.Id == assistant.SystemPromptId);
        var displayNameKey = SelectedItem?.DisplayNameKey;
        if (displayNameKey is not null || !_hasLoaded)
        {
            SelectedPromptNameKey = displayNameKey ?? DirectLocaleKey.Empty;
            return;
        }

        SelectedPromptNameKey = new FormattedDynamicLocaleKey(
            LocaleKey.PromptPage_MissingRoutePrompt_Content,
            new DirectLocaleKey(assistant.SystemPromptId));
    }

    /// <summary>
    /// Immutable row displayed by the compact prompt list.
    /// </summary>
    public sealed record Item(Guid Id, IDynamicLocaleKey DisplayNameKey, string Template)
    {
        public static Item FromPrompt(PromptDefinition prompt) =>
            new(
                prompt.Id,
                PromptDisplayNameProvider.GetDisplayNameKey(prompt),
                PromptTemplateRenderer.Render(prompt.Template, SystemPromptPlaceholderSource.Shared, PromptPlaceholderContext.Preview));
    }
}