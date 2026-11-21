using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Modding;
using ShadUI;

namespace Everywhere.Views.Modding;

/// <summary>
/// Provides in-dialog configuration for built-in and custom mod repository sources,
/// supporting drag-and-drop priority reordering, stacked source creation, and flyout deletion confirmation.
/// </summary>
public sealed partial class ModSourcesDialog : UserControl
{
    private const string BuiltInSourceId = "builtin";

    /// <summary>
    /// Gets whether sources were added, removed, toggled, or reordered while this dialog was open.
    /// </summary>
    public bool HasChanges { get; private set; }

    /// <summary>
    /// Gets the priority-ordered list of configured and built-in sources.
    /// </summary>
    public ObservableCollection<SourceItemPresentation> SourceItems { get; } = [];

    /// <summary>
    /// Gets or sets whether an add, remove, toggle, or reorder operation is in flight.
    /// </summary>
    public bool IsSubmitting
    {
        get => GetValue(IsSubmittingProperty);
        private set => SetValue(IsSubmittingProperty, value);
    }

    /// <summary>
    /// Gets or sets the validation or repository error banner text.
    /// </summary>
    public string? ErrorMessage
    {
        get => GetValue(ErrorMessageProperty);
        private set => SetValue(ErrorMessageProperty, value);
    }

    public static readonly StyledProperty<bool> IsSubmittingProperty =
        AvaloniaProperty.Register<ModSourcesDialog, bool>(nameof(IsSubmitting));

    public static readonly StyledProperty<string?> ErrorMessageProperty =
        AvaloniaProperty.Register<ModSourcesDialog, string?>(nameof(ErrorMessage));

    private readonly IModManager _modManager;
    private bool _isSyncingSources;
    private bool _isReorderPosted;

    /// <summary>
    /// Initializes a new repository source management dialog bound to <paramref name="modManager"/>.
    /// </summary>
    public ModSourcesDialog(IModManager modManager)
    {
        _modManager = modManager;
        DataContext = this;
        InitializeComponent();
        SourceItems.CollectionChanged += HandleSourceItemsChanged;
        RefreshSources();
    }

    [RelayCommand]
    private async Task OpenAddSourceDialogAsync(CancellationToken cancellationToken)
    {
        if (IsSubmitting) return;

        var form = new AddModSourceForm();
        var hostTopLevel = TopLevel.GetTopLevel(this);

        while (!cancellationToken.IsCancellationRequested)
        {
            var dialogResult = await DialogManager
                .CreateDialog(form, LocaleKey.ModPage_SourcesDialog_AddHeader.I18N(), hostTopLevel)
                .WithPrimaryButton(
                    LocaleKey.ModPage_SourcesDialog_AddButton.I18N(),
                    (_, e) => e.Cancel = !form.ValidateInputs())
                .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                .ShowAsync(cancellationToken);
            if (dialogResult != DialogResult.Primary) return;

            var urlOrPath = form.UrlOrPath?.Trim() ?? string.Empty;
            var customId = string.IsNullOrWhiteSpace(form.CustomId) ? null : form.CustomId.Trim();
            var mirrors = AddModSourceForm.ParseMirrors(form.Mirrors);

            var outcome = await TryAddSourceAsync(urlOrPath, customId, mirrors, isUntrustedConfirmed: false);
            if (outcome.Status == ModOperationStatus.ConfirmationRequired)
            {
                var confirmResult = await DialogManager
                    .CreateDialog(
                        LocaleKey.ModPage_SourcesDialog_UntrustedConfirmMessage.I18N().Format(urlOrPath),
                        LocaleKey.ModPage_SourcesDialog_UntrustedConfirmTitle.I18N(),
                        hostTopLevel)
                    .WithPrimaryButton(LocaleKey.ModPage_SourcesDialog_TrustButton.I18N(), buttonStyle: ButtonStyle.Destructive)
                    .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                    .ShowAsync(cancellationToken);
                if (confirmResult != DialogResult.Primary)
                {
                    continue;
                }

                outcome = await TryAddSourceAsync(urlOrPath, customId, mirrors, isUntrustedConfirmed: true);
            }

            if (outcome.Status == ModOperationStatus.Failed)
            {
                form.ErrorMessage = outcome.Message;
                continue;
            }

            HasChanges = true;
            RefreshSources();
            return;
        }
    }

    [RelayCommand]
    private async Task RemoveSourceAsync(SourceItemPresentation? item)
    {
        if (item is null || !item.IsRemovable || IsSubmitting) return;

        await RunSourceMutationAsync(() => _modManager.RemoveCustomSourceAsync(item.Id));
    }

    private Task SetSourceEnabledAsync(SourceItemPresentation item, bool isEnabled)
    {
        if (!item.CanToggle || IsSubmitting) return Task.CompletedTask;

        return RunSourceMutationAsync(() => _modManager.SetSourceEnabledAsync(item.Id, isEnabled));
    }

    private async Task<ModOperationResult> TryAddSourceAsync(
        string urlOrPath,
        string? customId,
        string[]? mirrors,
        bool isUntrustedConfirmed
    )
    {
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            return await _modManager.AddCustomSourceAsync(urlOrPath, customId, mirrors, isUntrustedConfirmed);
        }
        catch (Exception exception)
        {
            return new ModOperationResult
            {
                Status = ModOperationStatus.Failed,
                Message = exception.Message
            };
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private async Task RunSourceMutationAsync(Func<Task<ModOperationResult>> operation)
    {
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var result = await operation();
            if (result.Status == ModOperationStatus.Failed)
            {
                ErrorMessage = result.Message;
                RefreshSources();
                return;
            }

            HasChanges = true;
            RefreshSources();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            RefreshSources();
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    private void HandleSourceItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isSyncingSources ||
            e.Action is not (NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove) ||
            _isReorderPosted)
        {
            return;
        }

        // ItemDragBehavior may apply Remove + Insert or Move on the UI thread; coalesce to one persist call.
        _isReorderPosted = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _isReorderPosted = false;
                if (_isSyncingSources || IsSubmitting) return;

                var orderedIds = SourceItems.Select(entry => entry.Id).ToArray();
                var currentOrder = _modManager.Sources.Select(source => source.Id).ToArray();
                if (orderedIds.SequenceEqual(currentOrder, StringComparer.Ordinal)) return;

                RunSourceMutationAsync(() => _modManager.ReorderSourcesAsync(orderedIds)).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
            },
            DispatcherPriority.Background);
    }

    private void RefreshSources()
    {
        var sources = _modManager.Sources;
        var existingById = SourceItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var desired = new List<SourceItemPresentation>(sources.Count);

        foreach (var source in sources)
        {
            if (existingById.TryGetValue(source.Id, out var existing))
            {
                existing.Update(source);
                desired.Add(existing);
            }
            else
            {
                desired.Add(new SourceItemPresentation(this, source));
            }
        }

        _isSyncingSources = true;
        try
        {
            var desiredSet = desired.ToHashSet();
            for (var i = SourceItems.Count - 1; i >= 0; i--)
            {
                if (!desiredSet.Contains(SourceItems[i]))
                {
                    SourceItems.RemoveAt(i);
                }
            }

            for (var i = 0; i < desired.Count; i++)
            {
                var item = desired[i];
                if (i < SourceItems.Count && ReferenceEquals(SourceItems[i], item)) continue;

                var existingIndex = SourceItems.IndexOf(item);
                if (existingIndex >= 0)
                {
                    SourceItems.Move(existingIndex, i);
                }
                else
                {
                    SourceItems.Insert(i, item);
                }
            }
        }
        finally
        {
            _isSyncingSources = false;
        }
    }

    /// <summary>
    /// Presentation row representing one built-in or user-configured repository source inside <see cref="ModSourcesDialog"/>.
    /// </summary>
    public sealed partial class SourceItemPresentation : ObservableObject
    {
        /// <summary>
        /// Gets the repository source identifier.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the localized display label key for this source.
        /// </summary>
        public IDynamicLocaleKey DisplayNameKey { get; }

        /// <summary>
        /// Gets the localized subtitle key (description for the bundled built-in source, or index path/URL otherwise).
        /// </summary>
        [ObservableProperty]
        public partial IDynamicLocaleKey SubtitleKey { get; private set; } = new DirectLocaleKey(string.Empty);

        /// <summary>
        /// Gets the full URL/path and optional mirror URLs shown in the row tooltip.
        /// </summary>
        [ObservableProperty]
        public partial string? ToolTipText { get; private set; }

        /// <summary>
        /// Gets the optional localized badge label showing how many mirrors are configured.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMirrors))]
        public partial IDynamicLocaleKey? MirrorsBadgeKey { get; private set; }

        /// <summary>
        /// Gets whether at least one fallback mirror URL is configured.
        /// </summary>
        public bool HasMirrors => MirrorsBadgeKey is not null;

        /// <summary>
        /// Gets the configured index URI or local path.
        /// </summary>
        [ObservableProperty]
        public partial string Url { get; private set; } = string.Empty;

        /// <summary>
        /// Gets whether this source participates in catalog resolution.
        /// </summary>
        [ObservableProperty]
        public partial bool IsEnabled { get; private set; }

        /// <summary>
        /// Gets whether this source is shipped with the application and cannot be deleted.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRemovable))]
        public partial bool IsBuiltIn { get; private set; }

        /// <summary>
        /// Gets whether this row is the embedded built-in source bundled inside the host binary.
        /// </summary>
        public bool IsEmbeddedBuiltIn => Id == BuiltInSourceId;

        /// <summary>
        /// Gets whether signatures are verified for this source (omitted for the bundled built-in source).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsUnsigned))]
        public partial bool IsSigned { get; private set; }

        /// <summary>
        /// Gets whether this source is custom and not signature-verified.
        /// </summary>
        public bool IsUnsigned => !IsBuiltIn && !IsSigned;

        /// <summary>
        /// Gets whether this source can be deleted by the user.
        /// </summary>
        public bool IsRemovable => !IsBuiltIn;

        /// <summary>
        /// Gets whether the enablement switch can be toggled.
        /// </summary>
        public bool CanToggle => !IsEmbeddedBuiltIn;

        /// <summary>
        /// Gets the localized tooltip explaining the toggle control state.
        /// </summary>
        public IDynamicLocaleKey ToggleToolTipKey { get; }

        private readonly ModSourcesDialog _owner;

        /// <summary>
        /// Initializes a source row presentation.
        /// </summary>
        public SourceItemPresentation(ModSourcesDialog owner, ModSourceInfo source)
        {
            _owner = owner;
            Id = source.Id;
            DisplayNameKey = ModLocalizedText.CreateSourceDisplayNameKey(source.Id);
            ToggleToolTipKey = CanToggle
                ? new DynamicLocaleKey(LocaleKey.ModPage_SourcesDialog_Toggle_ToolTip)
                : new DynamicLocaleKey(LocaleKey.ModPage_SourcesDialog_BuiltInAlwaysEnabled_ToolTip);
            Update(source);
        }

        /// <summary>
        /// Updates row metadata and toggle state in place so drag reordering and toggle transitions remain smooth.
        /// </summary>
        public void Update(ModSourceInfo source)
        {
            Url = source.Url;
            IsEnabled = source.IsEnabled;
            IsBuiltIn = source.IsBuiltIn;
            IsSigned = !IsEmbeddedBuiltIn && (source.IsSignatureRequired || !string.IsNullOrWhiteSpace(source.PublicKey));

            SubtitleKey = IsEmbeddedBuiltIn
                ? new DynamicLocaleKey(LocaleKey.ModPage_SourcesDialog_BuiltInSubtitle)
                : new DirectLocaleKey(source.Url);

            MirrorsBadgeKey = source.Mirrors.Count > 0
                ? new FormattedDynamicLocaleKey(
                    LocaleKey.ModPage_SourcesDialog_MirrorsCountBadge,
                    new DirectLocaleKey(source.Mirrors.Count.ToString()))
                : null;

            ToolTipText = IsEmbeddedBuiltIn
                ? null
                : source.Mirrors.Count > 0
                    ? $"{source.Url}{Environment.NewLine}{string.Join(Environment.NewLine, source.Mirrors)}"
                    : source.Url;
        }

        [RelayCommand(CanExecute = nameof(CanToggle))]
        private Task SetEnabledAsync(bool isEnabled) => _owner.SetSourceEnabledAsync(this, isEnabled);
    }
}
