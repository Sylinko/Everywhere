using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Modding;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Packaging;
using Everywhere.Utilities;
using Tabler.Avalonia;
using ShadUI;

namespace Everywhere.ViewModels;

/// <summary>
/// Coordinates installed mod lifecycle actions, repository discovery, metadata/README viewing,
/// mod-owned settings attachment, and repository source management for <see cref="Views.Pages.ModPage"/>.
/// </summary>
public sealed partial class ModPageViewModel : BusyViewModelBase
{
    private const string AllFilterKey = "__all__";
    private const string BuiltInSourceId = "builtin";

    /// <summary>
    /// Gets the backing mod lifecycle manager.
    /// </summary>
    public IModManager ModManager { get; }

    /// <summary>
    /// Gets the registry providing per-mod settings UI views.
    /// </summary>
    public IModSettingsRegistry SettingsRegistry { get; }

    /// <summary>
    /// Gets or sets the View-owned callback used to execute interactive mod enable/disable confirmation dialogs.
    /// </summary>
    public Func<ModItemPresentation, bool, CancellationToken, Task>? EnableModAsyncHandler { get; set; }

    /// <summary>
    /// Gets the installed mods visible under the current search filter.
    /// </summary>
    public IReadOnlyBindableList<ModItemPresentation> InstalledItems { get; }

    /// <summary>
    /// Gets the discoverable repository mods visible under the active search, source, category, and sort criteria.
    /// </summary>
    public IReadOnlyBindableList<ModItemPresentation> DiscoverItems { get; }

    /// <summary>
    /// Gets source filter choices derived from enabled repository catalogs.
    /// </summary>
    public IReadOnlyBindableList<ModFilterOption> SourceFilterItems => _sourceFilterItems;

    /// <summary>
    /// Gets category filter choices derived from enabled repository catalog metadata.
    /// </summary>
    public IReadOnlyBindableList<ModFilterOption> CategoryFilterItems => _categoryFilterItems;

    /// <summary>
    /// Gets sort options declared by enabled repository catalogs.
    /// </summary>
    public IReadOnlyBindableList<ModSortOption> SortOptionItems => _sortOptionItems;

    /// <summary>
    /// Gets or sets the selected mod shown in the detail pane.
    /// </summary>
    public ModItemPresentation? SelectedMod
    {
        get => _selectedMod;
        set
        {
            var previousId = _selectedMod?.Id;
            if (!SetProperty(ref _selectedMod, value)) return;

            OnPropertyChanged(nameof(SelectedInstalledItem));
            OnPropertyChanged(nameof(SelectedDiscoverItem));
            SyncSelectedModDetails(forceReloadReadme: !string.Equals(previousId, value?.Id, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Gets or sets selection synchronized with the Installed tab list.
    /// </summary>
    public ModItemPresentation? SelectedInstalledItem
    {
        get => SelectedMod?.IsInstalled is true ? SelectedMod : null;
        set
        {
            if (value is not null) SelectedMod = value;
        }
    }

    /// <summary>
    /// Gets or sets selection synchronized with the Discover tab list.
    /// </summary>
    public ModItemPresentation? SelectedDiscoverItem
    {
        get => SelectedMod is { HasCatalogEntry: true } ? SelectedMod : null;
        set
        {
            if (value is not null) SelectedMod = value;
        }
    }

    /// <summary>
    /// Gets or sets the selected left-pane tab index (<c>0</c> Installed, <c>1</c> Discover).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiscoverTabSelected), nameof(HasDiscoverFilterMenu))]
    public partial int SelectedPaneTabIndex { get; set; }

    /// <summary>
    /// Gets whether the Discover repository tab is active in the left pane.
    /// </summary>
    public bool IsDiscoverTabSelected => SelectedPaneTabIndex == 1;

    /// <summary>
    /// Gets or sets the selected detail tab index (<c>0</c> Overview, <c>1</c> Settings).
    /// </summary>
    [ObservableProperty]
    public partial int SelectedDetailTabIndex { get; set; }

    /// <summary>
    /// Gets the lazily loaded Markdown README content for <see cref="SelectedMod"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedReadme))]
    public partial string? SelectedReadmeMarkdown { get; private set; }

    /// <summary>
    /// Gets whether the README document for <see cref="SelectedMod"/> is currently being loaded.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoadingSelectedReadme { get; private set; }

    /// <summary>
    /// Gets whether non-empty README content is available for display.
    /// </summary>
    public bool HasSelectedReadme => !string.IsNullOrWhiteSpace(SelectedReadmeMarkdown);

    /// <summary>
    /// Gets or sets the search query filtering both Installed and Discover lists.
    /// </summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>
    /// Gets or sets the active source filter in the Discover tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMultipleSources), nameof(HasDiscoverFilterMenu), nameof(HasActiveDiscoverFilter))]
    public partial ModFilterOption? SelectedSourceFilter { get; set; }

    /// <summary>
    /// Gets or sets the active category filter in the Discover tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCategoryFilters), nameof(HasDiscoverFilterMenu), nameof(HasActiveDiscoverFilter))]
    public partial ModFilterOption? SelectedCategoryFilter { get; set; }

    /// <summary>
    /// Gets or sets the active sort mode in the Discover tab.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSortOptions), nameof(HasDiscoverFilterMenu), nameof(HasActiveDiscoverFilter))]
    public partial ModSortOption? SelectedSortOption { get; set; }

    partial void OnSelectedSourceFilterChanged(ModFilterOption? value)
    {
        UpdateOptionSelection(_sourceFilterItems, value);
        if (_isUpdatingFilters) return;

        RebuildModels();
        SyncSelectedModDetails(forceReloadReadme: SelectedMod is { IsInstalled: false });
    }

    partial void OnSelectedCategoryFilterChanged(ModFilterOption? value) =>
        UpdateOptionSelection(_categoryFilterItems, value);

    partial void OnSelectedSortOptionChanged(ModSortOption? value) =>
        UpdateOptionSelection(_sortOptionItems, value);

    /// <summary>
    /// Gets whether repository catalogs are currently being fetched.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLoadingCatalog { get; private set; }

    /// <summary>
    /// Gets an optional summary of repository fetch failures from the latest refresh.
    /// </summary>
    [ObservableProperty]
    public partial string? CatalogErrorSummary { get; private set; }

    /// <summary>
    /// Gets whether the host requires a restart to finish unloading or upgrading active mods.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMutateMods))]
    public partial bool IsRestartRequired { get; private set; }

    /// <summary>
    /// Gets whether mod mutations (install, update, enable/disable, import, uninstall, rollback) are currently permitted.
    /// </summary>
    public bool CanMutateMods => IsNotBusy && !IsRestartRequired;

    /// <summary>
    /// Gets whether there is at least one installed mod matching the current filter.
    /// </summary>
    public bool HasInstalledItems => InstalledItems.Count > 0;

    /// <summary>
    /// Gets whether there is at least one catalog mod matching the current filter.
    /// </summary>
    public bool HasDiscoverItems => DiscoverItems.Count > 0;

    /// <summary>
    /// Gets whether more than one source contributes catalog entries.
    /// </summary>
    public bool HasMultipleSources =>
        _sourceFilterItems.Count > 2 ||
        (SelectedSourceFilter is not null && SelectedSourceFilter.Key != AllFilterKey);

    /// <summary>
    /// Gets whether at least two distinct repository category filters are available.
    /// </summary>
    public bool HasCategoryFilters =>
        _categoryFilterItems.Count > 2 ||
        (SelectedCategoryFilter is not null && SelectedCategoryFilter.Key != AllFilterKey);

    /// <summary>
    /// Gets whether at least one custom repository sort option is available.
    /// </summary>
    public bool HasSortOptions =>
        _sortOptionItems.Count > 1 ||
        (SelectedSortOption is not null && SelectedSortOption.Key is not null);

    /// <summary>
    /// Gets whether the Discover filter and sort menu button should be visible.
    /// </summary>
    public bool HasDiscoverFilterMenu =>
        IsDiscoverTabSelected && (HasMultipleSources || HasCategoryFilters || HasSortOptions);

    /// <summary>
    /// Gets whether any non-default source, category, or sort option is active.
    /// </summary>
    public bool HasActiveDiscoverFilter =>
        (SelectedSourceFilter is not null && SelectedSourceFilter.Key != AllFilterKey) ||
        (SelectedCategoryFilter is not null && SelectedCategoryFilter.Key != AllFilterKey) ||
        (SelectedSortOption is not null && SelectedSortOption.Key is not null);

    private static readonly IComparer<ModItemPresentation> InstalledItemComparer =
        Comparer<ModItemPresentation>.Create(static (left, right) =>
        {
            var nameComparison = StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            return nameComparison != 0 ? nameComparison : StringComparer.Ordinal.Compare(left.Id, right.Id);
        });

    private readonly BindableList<ModFilterOption> _sourceFilterItems = [];
    private readonly BindableList<ModFilterOption> _categoryFilterItems = [];
    private readonly BindableList<ModSortOption> _sortOptionItems = [];

    private readonly SourceCache<ModItemPresentation, string> _mods = new(static item => item.Id);
    private readonly ModFilterOption _allSourcesOption;
    private readonly ModFilterOption _allCategoriesOption;
    private readonly ModSortOption _defaultSortOption;

    private ModItemPresentation? _selectedMod;
    private IReadOnlyList<ModSourceCatalogSnapshot> _catalogs = [];
    private CancellationTokenSource? _readmeCts;
    private bool _hasFetchedCatalogs;
    private bool _isUpdatingFilters;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new <see cref="ModPageViewModel"/> bound to <paramref name="modManager"/> and <paramref name="settingsRegistry"/>.
    /// </summary>
    public ModPageViewModel(IModManager modManager, IModSettingsRegistry settingsRegistry)
    {
        ModManager = modManager;
        SettingsRegistry = settingsRegistry;

        _allSourcesOption = new ModFilterOption(
            AllFilterKey,
            new DynamicLocaleKey(LocaleKey.ModPage_Filter_AllSources),
            option => SelectedSourceFilter = option);
        _allCategoriesOption = new ModFilterOption(
            AllFilterKey,
            new DynamicLocaleKey(LocaleKey.ModPage_Filter_AllCategories),
            option => SelectedCategoryFilter = option);
        _defaultSortOption = new ModSortOption(
            null,
            "desc",
            new DynamicLocaleKey(LocaleKey.ModPage_Sort_Default),
            option => SelectedSortOption = option);

        _isUpdatingFilters = true;
        _sourceFilterItems.Add(_allSourcesOption);
        _categoryFilterItems.Add(_allCategoriesOption);
        _sortOptionItems.Add(_defaultSortOption);
        SelectedSourceFilter = _allSourcesOption;
        SelectedCategoryFilter = _allCategoriesOption;
        SelectedSortOption = _defaultSortOption;
        _isUpdatingFilters = false;

        var installedFilter = this.WhenValueChanged(x => x.SearchText)
            .Select(BuildInstalledFilter);

        _mods.Connect()
            .Filter(installedFilter)
            .SortAndBind(out var installedItems, InstalledItemComparer)
            .Subscribe(_ =>
            {
                OnPropertyChanged(nameof(HasInstalledItems));
                OnPropertyChanged(nameof(SelectedInstalledItem));
            })
            .AddTo(LifetimeDisposables);
        InstalledItems = installedItems.ToReadOnlyBindableList();

        var discoverFilter = this.WhenValueChanged(x => x.SearchText)
            .CombineLatest(
                this.WhenValueChanged(x => x.SelectedSourceFilter),
                this.WhenValueChanged(x => x.SelectedCategoryFilter),
                BuildDiscoverFilter);

        var discoverComparer = this.WhenValueChanged(x => x.SelectedSortOption)
            .Select(BuildDiscoverComparer);

        _mods.Connect()
            .Filter(discoverFilter)
            .SortAndBind(out var discoverItems, discoverComparer)
            .Subscribe(_ =>
            {
                OnPropertyChanged(nameof(HasDiscoverItems));
                OnPropertyChanged(nameof(SelectedDiscoverItem));
            })
            .AddTo(LifetimeDisposables);
        DiscoverItems = discoverItems.ToReadOnlyBindableList();

        LifetimeDisposables.Add(_mods);

        modManager.StateChanged += HandleManagerStateChangedFromWorker;
        LifetimeDisposables.Add(
            Disposable.Create(() => modManager.StateChanged -= HandleManagerStateChangedFromWorker));

        WeakReferenceMessenger.Default.Register<ModPageViewModel, LocaleChangedMessage>(
            this,
            static (recipient, _) => Dispatcher.UIThread.PostOnDemand(recipient.HandleLocaleChanged));
        LifetimeDisposables.Add(
            Disposable.Create(() => WeakReferenceMessenger.Default.UnregisterAll(this)));

        RebuildModels();
    }

    /// <inheritdoc />
    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        if (_hasFetchedCatalogs) return;

        await FetchCatalogsCoreAsync(shouldForceRefresh: false, showErrorToast: false, cancellationToken);
    }

    /// <inheritdoc />
    protected internal override Task ViewUnloaded()
    {
        DisposeHelper.CancelAndDisposeToDefault(ref _readmeCts);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Forces a catalog refresh for repository discovery and available-update detection.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsNotBusy))]
    private Task RefreshCatalogsAsync(CancellationToken cancellationToken) =>
        FetchCatalogsCoreAsync(shouldForceRefresh: true, showErrorToast: true, cancellationToken);

    /// <summary>
    /// Resets active Discover source, category, and sort filters to their default values.
    /// </summary>
    [RelayCommand]
    private void ResetDiscoverFilters()
    {
        SelectedCategoryFilter = _allCategoriesOption;
        SelectedSortOption = _defaultSortOption;
        SelectedSourceFilter = _allSourcesOption;
    }

    /// <summary>
    /// Refreshes repository catalogs silently after source configuration changes.
    /// </summary>
    public Task RefreshCatalogsQuietAsync(CancellationToken cancellationToken = default) =>
        FetchCatalogsCoreAsync(shouldForceRefresh: true, showErrorToast: false, cancellationToken);

    /// <summary>
    /// Executes an asynchronous mod mutation under the ViewModel busy guard and toast exception handler.
    /// </summary>
    public Task ExecuteBusyMutationAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
        ExecuteBusyTaskAsync(action, ToastExceptionHandler, cancellationToken);

    /// <summary>
    /// Imports a standalone <c>.zip</c> mod archive from <paramref name="path"/> and selects the imported mod.
    /// </summary>
    public async Task<ModOperationResult> ImportZipFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ModOperationResult? result = null;
        await ExecuteBusyTaskAsync(
            async token =>
            {
                var beforeIds = ModManager.InstalledMods.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                result = await ModManager.ImportZipAsync(path, token);

                var importedId = ModManager.InstalledMods.Select(item => item.Id).FirstOrDefault(id => !beforeIds.Contains(id));
                TrySelectMod(importedId);
                if (importedId is not null) SelectedPaneTabIndex = 0;
            },
            ToastExceptionHandler,
            cancellationToken);

        return result ?? new ModOperationResult { Status = ModOperationStatus.Failed };
    }

    /// <summary>
    /// Imports an unpacked local development directory from <paramref name="path"/> and selects the imported mod.
    /// </summary>
    public async Task<ModOperationResult> ImportDirectoryFromPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ModOperationResult? result = null;
        await ExecuteBusyTaskAsync(
            async token =>
            {
                var beforeIds = ModManager.InstalledMods.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                result = await ModManager.ImportDirectoryAsync(path, token);

                var importedId = ModManager.InstalledMods.Select(item => item.Id).FirstOrDefault(id => !beforeIds.Contains(id));
                TrySelectMod(importedId);
                if (importedId is not null) SelectedPaneTabIndex = 0;
            },
            ToastExceptionHandler,
            cancellationToken);

        return result ?? new ModOperationResult { Status = ModOperationStatus.Failed };
    }

    /// <summary>
    /// Reloads an unpacked local development directory mod from disk.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private async Task ReloadLocalDirectoryAsync(ModItemPresentation? item, CancellationToken cancellationToken)
    {
        if (item is not { IsLocalDirectory: true }) return;

        await ExecuteBusyTaskAsync(
            async token =>
            {
                var result = await ModManager.ReloadLocalDirectoryAsync(item.Id, token);
                ReportOperationFailureToast(result);
            },
            ToastExceptionHandler,
            cancellationToken);
    }

    /// <summary>
    /// Opens the tracked unpacked local folder for a development mod.
    /// </summary>
    [RelayCommand]
    private static async Task OpenLocalDirectoryAsync(ModItemPresentation? item)
    {
        if (item?.LocalDirectoryPath is not { Length: > 0 } path || !Directory.Exists(path)) return;

        await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    /// <summary>
    /// Enables or disables an installed mod programmatically.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private Task ToggleEnabledAsync(ModItemPresentation? item, CancellationToken cancellationToken) =>
        item is null ? Task.CompletedTask : InvokeSetModEnabledAsync(item, !item.IsEnabled, cancellationToken);

    private Task InvokeSetModEnabledAsync(ModItemPresentation item, bool targetEnabled, CancellationToken cancellationToken)
    {
        if (!item.IsInstalled || IsRestartRequired || item.IsEnabled == targetEnabled) return Task.CompletedTask;

        return ExecuteBusyTaskAsync(
            async token =>
            {
                if (EnableModAsyncHandler is { } handler)
                {
                    await handler(item, targetEnabled, token);
                    return;
                }

                var result = await ModManager.SetEnabledAsync(
                    item.Id,
                    targetEnabled,
                    shouldCascadeDependents: false,
                    shouldCascadeDependencies: false,
                    cancellationToken: token);
                ReportOperationFailureToast(result);
            },
            ToastExceptionHandler,
            cancellationToken);
    }

    /// <summary>
    /// Opens an external HTTP(S) URL in the default browser.
    /// </summary>
    [RelayCommand]
    private static async Task OpenUrlAsync(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;

        await Launcher.LaunchUriAsync(uri);
    }

    /// <inheritdoc />
    protected override void OnIsBusyChanged()
    {
        base.OnIsBusyChanged();
        OnPropertyChanged(nameof(CanMutateMods));
        RefreshCatalogsCommand.NotifyCanExecuteChanged();
        NotifyMutationCommandsCanExecuteChanged();
    }

    partial void OnIsRestartRequiredChanged(bool value) => NotifyMutationCommandsCanExecuteChanged();

    private void NotifyMutationCommandsCanExecuteChanged()
    {
        ReloadLocalDirectoryCommand.NotifyCanExecuteChanged();
        ToggleEnabledCommand.NotifyCanExecuteChanged();
        foreach (var item in _mods.Items)
        {
            item.NotifyCanToggleChanged();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_isDisposed)
        {
            _isDisposed = true;
            DisposeHelper.CancelAndDisposeToDefault(ref _readmeCts);
            EnableModAsyncHandler = null;
            if (Dispatcher.UIThread.CheckAccess())
            {
                SelectedMod = null;
            }
            else
            {
                _selectedMod = null;
            }
        }

        base.Dispose(disposing);
    }

    private async Task FetchCatalogsCoreAsync(bool shouldForceRefresh, bool showErrorToast, CancellationToken cancellationToken)
    {
        IsLoadingCatalog = true;
        try
        {
            var catalogs = await ModManager.FetchCatalogsAsync(shouldForceRefresh, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _hasFetchedCatalogs = true;
            _catalogs = catalogs;
            var errors = catalogs
                .Where(c => !string.IsNullOrWhiteSpace(c.Error))
                .Select(c => $"{c.Source.Id}: {c.Error}")
                .ToArray();
            CatalogErrorSummary = errors.Length > 0 ? string.Join(Environment.NewLine, errors) : null;

            RebuildModels();

            if (showErrorToast && CatalogErrorSummary is not null)
            {
                ToastHost
                    .CreateToast(LocaleKey.ModPage_Toast_CatalogRefreshFailed.I18N(), CatalogErrorSummary)
                    .OnBottomRight()
                    .DismissOnClick()
                    .ShowWarning();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            CatalogErrorSummary = exception.Message;
            if (showErrorToast) ToastExceptionHandler.HandleException(exception, LocaleKey.ModPage_Toast_CatalogRefreshFailed.I18N());
        }
        finally
        {
            IsLoadingCatalog = false;
        }
    }

    /// <summary>
    /// Runs an asynchronous download/install delegate while displaying a bottom-right progress toast.
    /// </summary>
    public async Task<ModOperationResult> RunDownloadWithProgressAsync(
        string displayName,
        bool isUpdate,
        Func<IProgress<double>, Task<ModOperationResult>> execute
    )
    {
        var progress = new Progress<double>();
        using var closeToastCts = new CancellationTokenSource();
        var toastTitle = (isUpdate ? LocaleKey.ModPage_ProgressToast_Updating : LocaleKey.ModPage_ProgressToast_Installing)
            .I18N()
            .Format(displayName);

        ToastHost
            .CreateToast(toastTitle)
            .WithProgress(progress)
            .WithCancellationToken(closeToastCts.Token)
            .OnBottomRight()
            .ShowInfo();

        try
        {
            var result = await execute(progress);
            await closeToastCts.CancelAsync();
            return result;
        }
        finally
        {
            if (!closeToastCts.IsCancellationRequested)
            {
                await closeToastCts.CancelAsync();
            }
        }
    }

    /// <summary>
    /// Projects an interactive installation plan result into View-ready display entries.
    /// </summary>
    public ModInstallPlanPreview BuildInstallPlanPreview(ModOperationResult plan, string targetModId)
    {
        var packagesToApply = plan.SolveResult?.Packages.Where(static pkg => !pkg.IsAlreadyInstalledVersion).ToArray() ?? [];
        var packageEntries = new List<ModInstallPackageEntry>(packagesToApply.Length);
        foreach (var pkg in packagesToApply)
        {
            var displayName = FindItem(pkg.Id)?.DisplayName ?? pkg.Id;
            var sourceNameKey = CreateSourceNameKey(pkg.SourceId);
            packageEntries.Add(new ModInstallPackageEntry(displayName, pkg.Version, sourceNameKey));
        }

        var disabledDependencies = plan.AffectedDependencies.Count > 0
            ? plan.AffectedDependencies
            :
            [
                .. (plan.SolveResult?.Packages ?? [])
                .Select(static pkg => pkg.Id)
                .Where(id =>
                    !string.Equals(id, targetModId, StringComparison.Ordinal) &&
                    FindItem(id) is { IsInstalled: true, IsEnabled: false })
                .Distinct(StringComparer.Ordinal)
            ];
        var dependenciesToEnable = disabledDependencies
            .Select(depId => FindItem(depId) is { } existing && !string.Equals(existing.DisplayName, depId, StringComparison.Ordinal)
                ? $"{existing.DisplayName} ({depId})"
                : depId)
            .ToArray();

        var licenseEntries = plan.PendingLicenses
            .Select(requirement =>
            {
                var displayName = FindItem(requirement.ModId)?.DisplayName ?? requirement.ModId;
                var summaryText = LocaleKey.ModPage_ConfirmInstall_LicenseEntry
                    .I18N()
                    .Format(displayName, requirement.Version, requirement.Spdx);
                return new ModInstallLicenseEntry(
                    requirement.ModId,
                    requirement.Spdx,
                    summaryText,
                    requirement.SourceId,
                    requirement.LicenseDocumentReference);
            })
            .ToArray();

        return new ModInstallPlanPreview(packageEntries, dependenciesToEnable, licenseEntries);
    }

    /// <summary>
    /// Creates a View-ready preview of affected dependents and orphan auto-dependencies before uninstalling <paramref name="item"/>.
    /// </summary>
    public ModUninstallPreview CreateUninstallPreview(ModItemPresentation item)
    {
        var dependents = ModManager.GetReverseDependents(item.Id, shouldIncludeDisabled: true);
        var removeSet = new HashSet<string>(dependents, StringComparer.Ordinal) { item.Id };
        var orphans = ModManager.GetOrphanAutoDependencies(removeSet);

        var message = LocaleKey.ModPage_ConfirmUninstall_Message.I18N().Format(item.DisplayName);
        var dependentsWarning = dependents.Count > 0
            ? LocaleKey.ModPage_ConfirmUninstall_DependentsWarning.I18N().Format(FormatModList(dependents))
            : null;
        var orphansLabel = orphans.Count > 0
            ? LocaleKey.ModPage_ConfirmUninstall_RemoveOrphansCheckBox.I18N().Format(FormatModList(orphans))
            : null;

        return new ModUninstallPreview(item.Id, item.DisplayName, message, dependentsWarning, orphansLabel);
    }

    /// <summary>
    /// Creates a View-ready summary of pending restart entries and combined diagnostic logs.
    /// </summary>
    public ModRestartDetailsSnapshot CreateRestartDetailsSnapshot(IReadOnlyList<ModDiagnostic>? extraDiagnostics = null)
    {
        var pendingEntries = ModManager.InstalledMods
            .Where(static summary => summary.PendingOperation is not null || summary.IsPendingRestart)
            .Select(static summary =>
            {
                var displayName = ModLocalizedText.Resolve(summary.Name, summary.Id);
                return summary.PendingOperation?.Kind switch
                {
                    ModPendingOperationKind.Uninstall => new ModRestartPendingEntry(
                        TablerIconKind.Trash,
                        displayName,
                        new DynamicLocaleKey(LocaleKey.ModPage_RestartDialog_ActionUninstall)),
                    ModPendingOperationKind.Update when summary.PendingOperation.TargetInstallation is { } targetSlot =>
                        new ModRestartPendingEntry(
                            TablerIconKind.Refresh,
                            displayName,
                            new FormattedDynamicLocaleKey(
                                LocaleKey.ModPage_RestartDialog_ActionUpdate,
                                new DirectLocaleKey(targetSlot.Version))),
                    _ => new ModRestartPendingEntry(
                        TablerIconKind.PlugOff,
                        displayName,
                        new DynamicLocaleKey(LocaleKey.ModPage_RestartDialog_ActionDisable))
                };
            })
            .ToArray();

        var combinedDiagnostics = (extraDiagnostics is { Count: > 0 } ? extraDiagnostics : [])
            .Concat(ModManager.RestartDiagnostics)
            .GroupBy(static d => $"{d.ModId}|{d.Code}|{d.Detail}", StringComparer.Ordinal)
            .Select(static g => g.First())
            .ToArray();

        var diagnosticText = combinedDiagnostics.Length > 0
            ? string.Join(
                Environment.NewLine,
                combinedDiagnostics.Select(static d =>
                    string.IsNullOrWhiteSpace(d.ModId)
                        ? $"[{d.Code}] {d.Detail}"
                        : $"[{d.ModId} / {d.Code}] {d.Detail}"))
            : null;

        return new ModRestartDetailsSnapshot(pendingEntries, diagnosticText);
    }

    /// <summary>
    /// Displays an error or warning toast when <paramref name="result"/> indicates failures; remains silent on success or restart-pending states.
    /// </summary>
    public void ReportOperationFailureToast(ModOperationResult result)
    {
        switch (result.Status)
        {
            case ModOperationStatus.CompletedWithErrors:
                ToastHost
                    .CreateToast(LocaleKey.ModPage_Toast_OperationCompletedWithErrors.I18N(), FormatConciseFailureSummary(result))
                    .OnBottomRight()
                    .DismissOnClick()
                    .ShowWarning();
                break;
            case ModOperationStatus.Failed:
            case ModOperationStatus.ConfirmationRequired:
                ToastHost
                    .CreateToast(LocaleKey.ModPage_Toast_OperationFailed.I18N(), FormatConciseFailureSummary(result))
                    .OnBottomRight()
                    .DismissOnClick()
                    .ShowError();
                break;
        }
    }

    /// <summary>
    /// Formats a concise single-line summary for diagnostic or operation failures.
    /// </summary>
    public string? FormatConciseFailureSummary(ModOperationResult result)
    {
        if (result.Diagnostics.Count > 0)
        {
            var firstDiagnostic = result.Diagnostics[0];
            var (titleKey, _) = MapDiagnosticMeta(firstDiagnostic.Code);
            var (firstLine, _) = SplitDiagnosticDetail(firstDiagnostic.Detail);

            var modLabel = firstDiagnostic.ModId is { Length: > 0 } modId && FindItem(modId) is { } modItem
                ? modItem.DisplayName
                : firstDiagnostic.ModId;
            var title = titleKey.ToString() ?? string.Empty;
            var header = !string.IsNullOrWhiteSpace(modLabel)
                ? $"{modLabel}: {title}"
                : title;

            return !string.IsNullOrWhiteSpace(firstLine) ? $"{header} — {firstLine}" : header;
        }

        var (summary, _) = SplitDiagnosticDetail(result.Message);
        return string.IsNullOrWhiteSpace(summary) ? result.Message : summary;
    }

    private IDynamicLocaleKey CreateSourceNameKey(string sourceId)
    {
        var matching = _catalogs.FirstOrDefault(c => string.Equals(c.Source.Id, sourceId, StringComparison.Ordinal));
        if (matching?.Index?.Title is { Count: > 0 } titleMap && ModLocalizedText.ResolveEntry(titleMap) is { } localized)
        {
            return new DirectLocaleKey(localized);
        }

        return ModLocalizedText.CreateSourceDisplayNameKey(sourceId);
    }

    /// <summary>
    /// Builds a structured dependency failure preview for dialog presentation.
    /// </summary>
    public static ModDependencyFailurePreview BuildDependencyFailurePreview(ModSolveResult solve, string? fallbackMessage)
    {
        if (!solve.IsSuccess)
        {
            var missingClauses = solve.Conflicts
                .Where(static clause => clause.Reason.Contains("was not found in any eligible source", StringComparison.Ordinal))
                .Select(static clause => new ModDependencyIssueEntry(
                    TablerIconKind.PackageOff,
                    clause.DependencyId,
                    clause.RequiredRange))
                .Distinct()
                .ToArray();

            if (missingClauses.Length > 0)
            {
                return new ModDependencyFailurePreview(
                    new DynamicLocaleKey(LocaleKey.ModPage_Error_MissingDependencies_Header),
                    missingClauses);
            }

            var conflictClauses = solve.Conflicts
                .Select(static clause => new ModDependencyIssueEntry(
                    TablerIconKind.ArrowsDiff,
                    clause.ConsumerId is { Length: > 0 } consumer
                        ? $"{consumer}  →  {clause.DependencyId}"
                        : clause.DependencyId,
                    clause.Reason))
                .Distinct()
                .ToArray();

            if (conflictClauses.Length == 0 && !string.IsNullOrWhiteSpace(solve.FailureSummary))
            {
                conflictClauses =
                [
                    new ModDependencyIssueEntry(
                        TablerIconKind.ArrowsDiff,
                        solve.FailureSummary,
                        Detail: null)
                ];
            }

            return new ModDependencyFailurePreview(
                new DynamicLocaleKey(LocaleKey.ModPage_Error_DependencyConflict_Header),
                conflictClauses);
        }

        var missing = solve.Packages
            .Where(static pkg => !pkg.IsAlreadyInstalledVersion)
            .Select(static pkg => new ModDependencyIssueEntry(TablerIconKind.PackageOff, pkg.Id, $"v{pkg.Version}"))
            .Distinct()
            .ToArray();

        if (missing.Length > 0)
        {
            return new ModDependencyFailurePreview(
                new DynamicLocaleKey(LocaleKey.ModPage_Error_MissingDependencies_Header),
                missing);
        }

        ModDependencyIssueEntry[] fallbackEntries = string.IsNullOrWhiteSpace(fallbackMessage)
            ? []
            : [new ModDependencyIssueEntry(TablerIconKind.ArrowsDiff, fallbackMessage, Detail: null)];
        return new ModDependencyFailurePreview(
            new DynamicLocaleKey(LocaleKey.ModPage_Error_DependencyConflict_Header),
            fallbackEntries);
    }

    /// <summary>
    /// Loads the primary license text and any bundled third-party notices for <paramref name="item"/>.
    /// </summary>
    public async Task<string?> LoadCombinedLicenseDocumentAsync(
        ModItemPresentation item,
        CancellationToken cancellationToken = default
    )
    {
        string? primary = null;
        var thirdPartyTexts = new List<string>();

        if (item.InstalledSummary is { License.File: { Length: > 0 } installedLicensePath })
        {
            primary = await ModManager.ReadInstalledDocumentAsync(item.Id, installedLicensePath, cancellationToken);
        }
        else if (item.CatalogSource is not null && item.CatalogVersion?.LicenseUrl is { Length: > 0 } licenseUrl)
        {
            primary = await ModManager.ReadDocumentAsync(item.CatalogSource.Id, licenseUrl, cancellationToken);
        }

        if (item.InstalledSummary?.License?.ThirdPartyFiles is { Length: > 0 } localThirdParty)
        {
            foreach (var file in localThirdParty)
            {
                var content = await ModManager.ReadInstalledDocumentAsync(item.Id, file, cancellationToken);
                if (!string.IsNullOrWhiteSpace(content)) thirdPartyTexts.Add(content);
            }
        }
        else if (item.CatalogSource is not null && item.CatalogVersion?.ThirdPartyLicenseUrls is { Length: > 0 } remoteThirdParty)
        {
            foreach (var docRef in remoteThirdParty)
            {
                var content = await ModManager.ReadDocumentAsync(item.CatalogSource.Id, docRef, cancellationToken);
                if (!string.IsNullOrWhiteSpace(content)) thirdPartyTexts.Add(content);
            }
        }

        if (thirdPartyTexts.Count == 0) return primary;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(primary)) parts.Add(primary.TrimEnd());
        parts.Add($"--- {LocaleKey.ModPage_LicenseDialog_ThirdPartySection.I18N()} ---\n\n{string.Join("\n\n---\n\n", thirdPartyTexts)}");
        return string.Join("\n\n", parts);
    }

    private void HandleManagerStateChangedFromWorker(object? sender, EventArgs e) =>
        Dispatcher.UIThread.PostOnDemand(HandleManagerStateChanged);

    private void HandleManagerStateChanged()
    {
        if (_isDisposed) return;

        var previousSelectedId = SelectedMod?.Id;
        var previousReadme = SelectedReadmeMarkdown;
        RebuildModels();
        TrySelectMod(previousSelectedId);
        SyncSelectedModDetails(forceReloadReadme: string.IsNullOrWhiteSpace(previousReadme));
    }

    private void HandleLocaleChanged()
    {
        if (_isDisposed) return;

        foreach (var item in _mods.Items)
        {
            item.RefreshLocale();
        }

        _isUpdatingFilters = true;
        try
        {
            RebuildSourceFilters();
            RebuildCategoryAndSortOptions();
        }
        finally
        {
            _isUpdatingFilters = false;
        }

        _mods.Refresh();
        SyncSelectedModDetails(forceReloadReadme: true);
    }

    private void RebuildModels()
    {
        IsRestartRequired = ModManager.IsRestartRequired;

        var host = ModManager.HostEnvironment;
        var installedById = ModManager.InstalledMods.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var sourcesById = ModManager.Sources.ToDictionary(source => source.Id, StringComparer.Ordinal);

        _isUpdatingFilters = true;
        try
        {
            RebuildSourceFilters();
        }
        finally
        {
            _isUpdatingFilters = false;
        }

        var catalogById = BuildCatalogLookupForActiveFilter(host);

        var allIds = new HashSet<string>(installedById.Keys, StringComparer.Ordinal);
        allIds.UnionWith(catalogById.Keys);

        _mods.Edit(updater =>
        {
            // Remove stale entries before reusing surviving presentation objects by stable ModId.
            foreach (var staleId in updater.Keys.Where(id => !allIds.Contains(id)).ToArray())
            {
                updater.RemoveKey(staleId);
            }

            foreach (var id in allIds)
            {
                installedById.TryGetValue(id, out var installed);
                catalogById.TryGetValue(id, out var catalog);
                var owningSourceId = installed?.SourceId ?? catalog?.Source.Id;
                ModSourceInfo? configuredSource = null;
                if (owningSourceId is not null)
                {
                    sourcesById.TryGetValue(owningSourceId, out configuredSource);
                }

                var lookup = updater.Lookup(id);
                var presentation = lookup.HasValue ? lookup.Value : new ModItemPresentation(this, id);
                var updateVersion = ResolveAvailableUpdateVersion(installed, catalog, host);
                presentation.Update(
                    installed,
                    catalog,
                    configuredSource,
                    updateVersion,
                    IsRestartRequired);
                updater.AddOrUpdate(presentation);
            }
        });

        _isUpdatingFilters = true;
        try
        {
            RebuildCategoryAndSortOptions();
        }
        finally
        {
            _isUpdatingFilters = false;
        }

        if (SelectedMod is { } selected && !_mods.Lookup(selected.Id).HasValue)
        {
            SelectedMod = null;
        }
    }

    private Dictionary<string, ResolvedCatalogItem> BuildCatalogLookupForActiveFilter(ModHostEnvironment? host)
    {
        var result = new Dictionary<string, ResolvedCatalogItem>(StringComparer.Ordinal);
        var activeSourceKey = SelectedSourceFilter?.Key;
        var hasExplicitSource = activeSourceKey is { Length: > 0 } && !string.Equals(activeSourceKey, AllFilterKey, StringComparison.Ordinal);

        for (var sourceOrder = 0; sourceOrder < _catalogs.Count; sourceOrder++)
        {
            var catalog = _catalogs[sourceOrder];
            if (catalog.Index is null) continue;
            if (hasExplicitSource && !string.Equals(catalog.Source.Id, activeSourceKey, StringComparison.Ordinal))
            {
                continue;
            }

            var metadata = catalog.Index.Metadata;
            for (var modOrder = 0; modOrder < catalog.Index.Mods.Length; modOrder++)
            {
                var entry = catalog.Index.Mods[modOrder];
                if (result.ContainsKey(entry.Id)) continue;

                var compatibleVersion = SelectCompatibleVersion(entry, host, out var incompatibilityReason);
                var displayVersion = compatibleVersion ?? entry.Versions.FirstOrDefault();

                IReadOnlyDictionary<string, string>? categoryTitle = null;
                if (!string.IsNullOrWhiteSpace(entry.Metadata.Category) &&
                    metadata.Categories.TryGetValue(entry.Metadata.Category, out var categoryDecl))
                {
                    categoryTitle = categoryDecl.Title;
                }

                result[entry.Id] = new ResolvedCatalogItem(
                    catalog.Source,
                    catalog.Index,
                    entry,
                    compatibleVersion,
                    displayVersion,
                    incompatibilityReason,
                    categoryTitle,
                    sourceOrder,
                    modOrder);
            }
        }

        return result;
    }

    private static ModIndexVersion? SelectCompatibleVersion(
        ModIndexEntry entry,
        ModHostEnvironment? host,
        out IDynamicLocaleKey? incompatibilityReason
    )
    {
        incompatibilityReason = null;
        if (entry.Versions.Length == 0)
        {
            incompatibilityReason = new DirectLocaleKey("No packaged versions.");
            return null;
        }

        if (host is null)
        {
            return entry.Versions
                .OrderByDescending(static v => ParseVersionSafe(v.Version))
                .FirstOrDefault();
        }

        var hostVersion = ModCompatibility.ParseVersion(host.HostVersion);
        var modApiVersion = ModCompatibility.ParseVersion(host.ModApiVersion);
        entry.Channels.TryGetValue("stable", out var stableVersion);

        var compatible = entry.Versions
            .Where(version => IsVersionHostCompatible(version, host.RuntimeIdentifier, hostVersion, modApiVersion, host.HostFramework))
            .OrderByDescending(version => stableVersion is not null && string.Equals(version.Version, stableVersion, StringComparison.Ordinal))
            .ThenByDescending(static version => ParseVersionSafe(version.Version))
            .FirstOrDefault();

        if (compatible is not null) return compatible;

        incompatibilityReason = DescribeIncompatibility(entry, host);
        return null;
    }

    private string? ResolveAvailableUpdateVersion(
        ModSummary? installed,
        ResolvedCatalogItem? primaryCatalog,
        ModHostEnvironment? host
    )
    {
        if (installed is null ||
            installed.PinnedVersion is not null ||
            installed.SourceId is ModStateRecord.LocalZipSourceId or ModStateRecord.LocalDirectorySourceId)
        {
            return null;
        }

        var installedVersion = ParseVersionSafe(installed.Version);
        ModIndexVersion? candidate = null;

        // Prefer updates from the source that originally installed the mod when that catalog is available.
        var matchingSource = _catalogs.FirstOrDefault(c =>
            string.Equals(c.Source.Id, installed.SourceId, StringComparison.Ordinal) && c.Index is not null);
        var matchingEntry = matchingSource?.Index?.Mods.FirstOrDefault(m => string.Equals(m.Id, installed.Id, StringComparison.Ordinal));
        if (matchingEntry is not null)
        {
            candidate = SelectCompatibleVersion(matchingEntry, host, out _);
        }
        else if (installed.SourceId != BuiltInSourceId && primaryCatalog?.CompatibleVersion is { } compatible)
        {
            candidate = compatible;
        }

        if (candidate is null) return null;

        var candidateVersion = ParseVersionSafe(candidate.Version);
        return candidateVersion > installedVersion ? candidate.Version : null;
    }

    private void RebuildSourceFilters()
    {
        var previousKey = SelectedSourceFilter?.Key ?? AllFilterKey;
        var next = new List<ModFilterOption> { _allSourcesOption };

        foreach (var catalog in _catalogs)
        {
            if (catalog.Index is null || catalog.Index.Mods.Length == 0) continue;

            var localizedTitle = catalog.Index.Title is { Count: > 0 } rawTitle
                ? ModLocalizedText.ResolveEntry(rawTitle)
                : null;
            var label = !string.IsNullOrWhiteSpace(localizedTitle)
                ? new DirectLocaleKey(localizedTitle)
                : ModLocalizedText.CreateSourceDisplayNameKey(catalog.Source.Id);

            next.Add(new ModFilterOption(catalog.Source.Id, label, option => SelectedSourceFilter = option));
        }

        SyncCollection(_sourceFilterItems, next);
        SelectedSourceFilter = _sourceFilterItems.FirstOrDefault(item => string.Equals(item.Key, previousKey, StringComparison.Ordinal))
            ?? _allSourcesOption;
        UpdateOptionSelection(_sourceFilterItems, SelectedSourceFilter);
        OnPropertyChanged(nameof(HasMultipleSources));
        OnPropertyChanged(nameof(HasDiscoverFilterMenu));
        OnPropertyChanged(nameof(HasActiveDiscoverFilter));
    }

    private void RebuildCategoryAndSortOptions()
    {
        var previousCategory = SelectedCategoryFilter?.Key ?? AllFilterKey;
        var previousSort = SelectedSortOption?.Key;
        var activeSourceKey = SelectedSourceFilter?.Key;

        var categories = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var sortKeys = new Dictionary<string, ModIndexSortKeyDeclaration>(StringComparer.Ordinal);

        foreach (var catalog in _catalogs)
        {
            if (catalog.Index is null) continue;
            if (activeSourceKey is not null &&
                activeSourceKey != AllFilterKey &&
                !string.Equals(catalog.Source.Id, activeSourceKey, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (categoryId, declaration) in catalog.Index.Metadata.Categories)
            {
                categories.TryAdd(categoryId, declaration.Title);
            }

            foreach (var (sortKeyId, declaration) in catalog.Index.Metadata.SortKeys)
            {
                sortKeys.TryAdd(sortKeyId, declaration);
            }
        }

        var nextCategories = new List<ModFilterOption> { _allCategoriesOption };
        foreach (var (categoryId, titleMap) in categories.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            nextCategories.Add(
                new ModFilterOption(
                    categoryId,
                    new DirectLocaleKey(ModLocalizedText.Resolve(titleMap, categoryId)),
                    option => SelectedCategoryFilter = option));
        }

        SyncCollection(_categoryFilterItems, nextCategories);
        SelectedCategoryFilter = _categoryFilterItems.FirstOrDefault(item => string.Equals(item.Key, previousCategory, StringComparison.Ordinal))
            ?? _allCategoriesOption;
        UpdateOptionSelection(_categoryFilterItems, SelectedCategoryFilter);
        OnPropertyChanged(nameof(HasCategoryFilters));

        var nextSorts = new List<ModSortOption> { _defaultSortOption };
        foreach (var (sortKeyId, declaration) in sortKeys)
        {
            nextSorts.Add(
                new ModSortOption(
                    sortKeyId,
                    declaration.Order,
                    new DirectLocaleKey(ModLocalizedText.Resolve(declaration.Title, sortKeyId)),
                    option => SelectedSortOption = option));
        }

        SyncCollection(_sortOptionItems, nextSorts);
        SelectedSortOption = _sortOptionItems.FirstOrDefault(item => string.Equals(item.Key, previousSort, StringComparison.Ordinal))
            ?? _defaultSortOption;
        UpdateOptionSelection(_sortOptionItems, SelectedSortOption);
        OnPropertyChanged(nameof(HasSortOptions));
        OnPropertyChanged(nameof(HasDiscoverFilterMenu));
        OnPropertyChanged(nameof(HasActiveDiscoverFilter));
    }

    private static void UpdateOptionSelection(IReadOnlyList<ModFilterOption> items, ModFilterOption? selected)
    {
        foreach (var item in items)
        {
            item.IsSelected = ReferenceEquals(item, selected) ||
                (selected is not null && string.Equals(item.Key, selected.Key, StringComparison.Ordinal));
        }
    }

    private static void UpdateOptionSelection(IReadOnlyList<ModSortOption> items, ModSortOption? selected)
    {
        foreach (var item in items)
        {
            item.IsSelected = ReferenceEquals(item, selected) ||
                (selected is not null && string.Equals(item.Key, selected.Key, StringComparison.Ordinal));
        }
    }

    private static Func<ModItemPresentation, bool> BuildInstalledFilter(string? searchText)
    {
        var query = searchText?.Trim();
        return item => item.IsInstalled && MatchesSearch(item, query);
    }

    private static Func<ModItemPresentation, bool> BuildDiscoverFilter(
        string? searchText,
        ModFilterOption? selectedSourceFilter,
        ModFilterOption? selectedCategoryFilter
    )
    {
        var query = searchText?.Trim();
        var sourceKey = selectedSourceFilter?.Key;
        var categoryKey = selectedCategoryFilter?.Key;
        return item =>
            item.HasCatalogEntry &&
            MatchesSearch(item, query) &&
            (sourceKey is null || sourceKey == AllFilterKey || string.Equals(item.CatalogSource?.Id, sourceKey, StringComparison.Ordinal)) &&
            (categoryKey is null || categoryKey == AllFilterKey || string.Equals(item.CategoryId, categoryKey, StringComparison.Ordinal));
    }

    private static IComparer<ModItemPresentation> BuildDiscoverComparer(ModSortOption? sortOption) =>
        Comparer<ModItemPresentation>.Create((left, right) =>
        {
            if (sortOption?.Key is { Length: > 0 } sortKey)
            {
                var leftValue = left.TryGetSortValue(sortKey);
                var rightValue = right.TryGetSortValue(sortKey);
                var hasValueComparison = rightValue.HasValue.CompareTo(leftValue.HasValue);
                if (hasValueComparison != 0) return hasValueComparison;

                if (leftValue.HasValue && rightValue.HasValue)
                {
                    var isAscending = string.Equals(sortOption.Order, "asc", StringComparison.OrdinalIgnoreCase);
                    var metricComparison = isAscending
                        ? leftValue.Value.CompareTo(rightValue.Value)
                        : rightValue.Value.CompareTo(leftValue.Value);
                    if (metricComparison != 0) return metricComparison;
                }
            }

            var sourceComparison = left.CatalogSourceOrder.CompareTo(right.CatalogSourceOrder);
            return sourceComparison != 0 ? sourceComparison : left.CatalogModOrder.CompareTo(right.CatalogModOrder);
        });

    private ModItemPresentation? FindItem(string id) =>
        _mods.Lookup(id) is { HasValue: true, Value: var item } ? item : null;

    private void SyncSelectedModDetails(bool forceReloadReadme)
    {
        var mod = SelectedMod;
        if (mod is null)
        {
            DisposeHelper.CancelAndDisposeToDefault(ref _readmeCts);
            SelectedReadmeMarkdown = null;
            IsLoadingSelectedReadme = false;
            SelectedDetailTabIndex = 0;
            return;
        }

        if (!mod.HasSettings && SelectedDetailTabIndex != 0)
        {
            SelectedDetailTabIndex = 0;
        }

        if (forceReloadReadme)
        {
            LoadReadmeAsync(mod).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }
    }

    private async Task LoadReadmeAsync(ModItemPresentation mod)
    {
        DisposeHelper.CancelAndDisposeToDefault(ref _readmeCts);
        var cts = new CancellationTokenSource();
        _readmeCts = cts;
        var token = cts.Token;

        IsLoadingSelectedReadme = true;
        try
        {
            string? markdown = null;
            if (mod.InstalledSummary is { Readme: { Count: > 0 } installedReadme } &&
                ModLocalizedText.ResolveEntry(installedReadme) is { } relativePath)
            {
                markdown = await ModManager.ReadInstalledDocumentAsync(mod.Id, relativePath, token);
            }
            else if (mod.CatalogSource is not null &&
                     mod.CatalogVersion?.Readme is { Count: > 0 } catalogReadme &&
                     ModLocalizedText.ResolveEntry(catalogReadme) is { } docRef)
            {
                markdown = await ModManager.ReadDocumentAsync(mod.CatalogSource.Id, docRef, token);
            }

            if (token.IsCancellationRequested || !ReferenceEquals(SelectedMod, mod)) return;

            SelectedReadmeMarkdown = markdown;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!token.IsCancellationRequested && ReferenceEquals(SelectedMod, mod))
            {
                SelectedReadmeMarkdown = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_readmeCts, cts))
            {
                IsLoadingSelectedReadme = false;
            }
        }
    }

    private void TrySelectMod(string? preferredId)
    {
        if (preferredId is not null && FindItem(preferredId) is { } matched)
        {
            SelectedMod = matched;
            return;
        }

        SelectedMod = InstalledItems.FirstOrDefault() ?? DiscoverItems.FirstOrDefault();
    }

    /// <summary>
    /// Formats a list of mod identifiers using their localized display names where known.
    /// </summary>
    public string FormatModList(IReadOnlyList<string> modIds) =>
        string.Join(", ", modIds.Select(id => FindItem(id) is { } item ? $"{item.DisplayName} ({id})" : id));

    private static bool MatchesSearch(ModItemPresentation item, string? query) =>
        string.IsNullOrWhiteSpace(query) ||
        item.SearchValues.AsValueEnumerable().Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static void SyncCollection<T>(BindableList<T> target, IReadOnlyList<T> desired) where T : notnull
    {
        if (target.SequenceEqual(desired)) return;

        var desiredSet = new HashSet<T>(desired);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var item = desired[i];
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], item)) continue;

            var existingIndex = target.IndexOf(item);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, item);
            }
        }
    }

    private static bool IsVersionHostCompatible(
        ModIndexVersion version,
        string runtimeIdentifier,
        SemanticVersion hostVersion,
        SemanticVersion modApiVersion,
        string hostFramework
    )
    {
        var selected = ModPlatformSelector.Select(version.Entry, runtimeIdentifier);
        if (selected is null) return false;

        if (!SemanticVersionRange.TryParse(selected.ModApiVersion, out var apiRange) || !apiRange.Contains(modApiVersion))
        {
            return false;
        }

        if (selected.HostVersion is not null &&
            (!SemanticVersionRange.TryParse(selected.HostVersion, out var hostRange) || !hostRange.Contains(hostVersion)))
        {
            return false;
        }

        return ModCompatibility.IsFrameworkCompatible(hostFramework, selected.Framework);
    }

    private static FormattedDynamicLocaleKey DescribeIncompatibility(ModIndexEntry entry, ModHostEnvironment host)
    {
        var hasPlatform = entry.Versions.AsValueEnumerable().Any(v => ModPlatformSelector.Select(v.Entry, host.RuntimeIdentifier) is not null);
        return hasPlatform
            ? new FormattedDynamicLocaleKey(
                LocaleKey.ModPage_Incompatible_Version,
                new DirectLocaleKey(host.HostVersion),
                new DirectLocaleKey(host.ModApiVersion))
            : new FormattedDynamicLocaleKey(
                LocaleKey.ModPage_Incompatible_Platform,
                new DirectLocaleKey(host.RuntimeIdentifier));
    }

    private static SemanticVersion ParseVersionSafe(string value) =>
        SemanticVersion.TryParse(value, out var parsed) ? parsed : new SemanticVersion(0);

    /// <summary>
    /// Represents a filter item in the Discover source or category menu.
    /// </summary>
    public sealed partial class ModFilterOption(
        string key,
        IDynamicLocaleKey titleKey,
        Action<ModFilterOption> onSelect
    ) : ObservableObject, IEquatable<ModFilterOption>
    {
        /// <summary>
        /// Gets the underlying filter key.
        /// </summary>
        public string Key { get; } = key;

        /// <summary>
        /// Gets the localized option label.
        /// </summary>
        public IDynamicLocaleKey TitleKey { get; } = titleKey;

        /// <summary>
        /// Gets whether this option is currently active.
        /// </summary>
        [ObservableProperty]
        public partial bool IsSelected { get; internal set; }

        [RelayCommand]
        private void Select() => onSelect(this);

        /// <inheritdoc />
        public bool Equals(ModFilterOption? other) =>
            other is not null &&
            string.Equals(Key, other.Key, StringComparison.Ordinal) &&
            Equals(TitleKey, other.TitleKey);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ModFilterOption);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Key, TitleKey);
    }

    /// <summary>
    /// Represents a sort mode option in the Discover sort menu.
    /// </summary>
    public sealed partial class ModSortOption(
        string? key,
        string order,
        IDynamicLocaleKey titleKey,
        Action<ModSortOption> onSelect
    ) : ObservableObject, IEquatable<ModSortOption>
    {
        /// <summary>
        /// Gets the repository metric key, or <see langword="null"/> for default index order.
        /// </summary>
        public string? Key { get; } = key;

        /// <summary>
        /// Gets the sort direction (<c>asc</c> or <c>desc</c>).
        /// </summary>
        public string Order { get; } = order;

        /// <summary>
        /// Gets the localized option label.
        /// </summary>
        public IDynamicLocaleKey TitleKey { get; } = titleKey;

        /// <summary>
        /// Gets whether this option is currently active.
        /// </summary>
        [ObservableProperty]
        public partial bool IsSelected { get; internal set; }

        [RelayCommand]
        private void Select() => onSelect(this);

        /// <inheritdoc />
        public bool Equals(ModSortOption? other) =>
            other is not null &&
            string.Equals(Key, other.Key, StringComparison.Ordinal) &&
            string.Equals(Order, other.Order, StringComparison.Ordinal) &&
            Equals(TitleKey, other.TitleKey);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ModSortOption);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Key, Order, TitleKey);
    }

    /// <summary>
    /// Represents one metadata key/value entry inside the Overview details card.
    /// </summary>
    public sealed record ModMetadataField(
        IDynamicLocaleKey LabelKey,
        string? Value,
        IDynamicLocaleKey? LocalizedValueKey = null,
        bool IsMonospace = false
    )
    {
        /// <summary>
        /// Gets whether <see cref="LocalizedValueKey"/> should be used instead of literal <see cref="Value"/>.
        /// </summary>
        public bool HasLocalizedValue => LocalizedValueKey is not null;
    }

    /// <summary>
    /// Represents one package entry to display inside the install/update confirmation dialog.
    /// </summary>
    public sealed record ModInstallPackageEntry(
        string DisplayName,
        string Version,
        IDynamicLocaleKey SourceNameKey
    )
    {
        /// <summary>
        /// Gets the formatted version badge label.
        /// </summary>
        public string VersionBadge => $"v{Version}";
    }

    /// <summary>
    /// Represents one pending license acceptance row inside the install/update confirmation dialog.
    /// </summary>
    public sealed record ModInstallLicenseEntry(
        string ModId,
        string Spdx,
        string SummaryText,
        string SourceId,
        string? DocumentReference
    )
    {
        /// <summary>
        /// Gets whether a remote or bundled license document reference can be previewed.
        /// </summary>
        public bool HasDocument => !string.IsNullOrWhiteSpace(DocumentReference);
    }

    /// <summary>
    /// Summarizes packages, dependencies to enable, and licenses pending user approval before installation or update.
    /// </summary>
    public sealed record ModInstallPlanPreview(
        IReadOnlyList<ModInstallPackageEntry> Packages,
        IReadOnlyList<string> DependenciesToEnable,
        IReadOnlyList<ModInstallLicenseEntry> PendingLicenses
    )
    {
        /// <summary>
        /// Gets whether any new packages need to be downloaded or installed.
        /// </summary>
        public bool HasPackages => Packages.Count > 0;

        /// <summary>
        /// Gets whether any currently installed dependencies must be enabled.
        /// </summary>
        public bool HasDependenciesToEnable => DependenciesToEnable.Count > 0;

        /// <summary>
        /// Gets whether any license terms require explicit user acceptance.
        /// </summary>
        public bool HasLicenses => PendingLicenses.Count > 0;
    }

    /// <summary>
    /// Summarizes affected dependents and orphan auto-dependencies before confirming mod uninstallation.
    /// </summary>
    public sealed record ModUninstallPreview(
        string ModId,
        string DisplayName,
        string Message,
        string? DependentsWarning,
        string? OrphansLabel
    )
    {
        /// <summary>
        /// Gets whether dependent mods will also be uninstalled alongside the target mod.
        /// </summary>
        public bool HasDependentsWarning => !string.IsNullOrWhiteSpace(DependentsWarning);

        /// <summary>
        /// Gets whether unused auto-installed dependencies can be cleaned up together.
        /// </summary>
        public bool HasOrphans => !string.IsNullOrWhiteSpace(OrphansLabel);
    }

    /// <summary>
    /// Represents one pending restart mod entry with its icon and action label.
    /// </summary>
    public sealed record ModRestartPendingEntry(
        TablerIconKind Icon,
        string DisplayName,
        IDynamicLocaleKey ActionBadgeKey
    );

    /// <summary>
    /// Summarizes pending restart operations and collected technical diagnostics for display in the restart dialog.
    /// </summary>
    public sealed record ModRestartDetailsSnapshot(
        IReadOnlyList<ModRestartPendingEntry> PendingEntries,
        string? DiagnosticText
    )
    {
        /// <summary>
        /// Gets whether at least one mod operation is queued for host restart.
        /// </summary>
        public bool HasPendingEntries => PendingEntries.Count > 0;

        /// <summary>
        /// Gets whether any host or mod diagnostics were captured.
        /// </summary>
        public bool HasDiagnostics => !string.IsNullOrWhiteSpace(DiagnosticText);
    }

    /// <summary>
    /// Represents one missing dependency or conflict item shown in the dependency error dialog.
    /// </summary>
    public sealed record ModDependencyIssueEntry(
        TablerIconKind Icon,
        string Title,
        string? Detail
    )
    {
        /// <summary>
        /// Gets whether secondary detail text is present.
        /// </summary>
        public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    }

    /// <summary>
    /// Summarizes dependency solver failures for structured dialog display.
    /// </summary>
    public sealed record ModDependencyFailurePreview(
        IDynamicLocaleKey HeaderKey,
        IReadOnlyList<ModDependencyIssueEntry> Entries
    );

    /// <summary>
    /// Represents one icon-paired secondary metadata chip displayed in a mod hover card.
    /// </summary>
    public sealed record ModToolTipChip(TablerIconKind Icon, string Text);

    /// <summary>
    /// Represents one supplementary footer note displayed in a mod hover card.
    /// </summary>
    public sealed record ModToolTipNote(TablerIconKind Icon, IDynamicLocaleKey TextKey, bool IsWarning = false);

    /// <summary>
    /// Represents one icon-paired metadata or verification item displayed in the detail header subtitle row.
    /// </summary>
    public sealed record ModHeaderMetaItem(
        TablerIconKind Icon,
        IDynamicLocaleKey TextKey,
        IDynamicLocaleKey? ToolTipKey = null,
        bool IsTrusted = false,
        bool IsWarning = false
    );

    /// <summary>
    /// Represents a declared logical mod dependency displayed in the Overview details sidebar.
    /// </summary>
    public sealed record ModDependencyDisplayItem(
        string Id,
        IDynamicLocaleKey VersionLabelKey
    );

    /// <summary>
    /// Represents a stable, UI-thread presentation model for one logical mod across Installed and Discover views.
    /// </summary>
    public sealed partial class ModItemPresentation : ObservableObject
    {
        /// <summary>
        /// Gets the canonical mod identifier.
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Gets the localized display name.
        /// </summary>
        [ObservableProperty]
        public partial string DisplayName { get; private set; }

        /// <summary>
        /// Gets the localized short description.
        /// </summary>
        [ObservableProperty]
        public partial string? Description { get; private set; }

        /// <summary>
        /// Gets the active installed version or latest catalog version string.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(VersionBadgeText))]
        public partial string Version { get; private set; } = "0.0.0";

        /// <summary>
        /// Gets the available newer catalog version when an update exists.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(CanUpdate), nameof(UpdateButtonContentKey), nameof(VersionBadgeText))]
        public partial string? AvailableUpdateVersion { get; private set; }

        /// <summary>
        /// Gets the formatted version label shown in the hover card header.
        /// </summary>
        public string VersionBadgeText => HasUpdate && AvailableUpdateVersion is { Length: > 0 } next ? $"v{Version} → v{next}" : $"v{Version}";

        /// <summary>
        /// Gets whether a newer compatible repository update is available.
        /// </summary>
        public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableUpdateVersion);

        /// <summary>
        /// Gets the localized text for the update action button.
        /// </summary>
        public IDynamicLocaleKey? UpdateButtonContentKey => AvailableUpdateVersion is { Length: > 0 } nextVersion
            ? new FormattedDynamicLocaleKey(LocaleKey.ModPage_UpdateButton_Content, new DirectLocaleKey(nextVersion))
            : null;

        /// <summary>
        /// Gets the optional author string.
        /// </summary>
        [ObservableProperty]
        public partial string? Author { get; private set; }

        /// <summary>
        /// Gets the owning source identifier.
        /// </summary>
        [ObservableProperty]
        public partial string SourceId { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the localized display label for <see cref="SourceId"/>.
        /// </summary>
        [ObservableProperty]
        public partial IDynamicLocaleKey SourceDisplayNameKey { get; private set; } = new DirectLocaleKey(string.Empty);

        /// <summary>
        /// Gets the resolved icon URI or data URL for <c>AsyncImageLoader</c>.
        /// </summary>
        [ObservableProperty]
        public partial string? IconSource { get; private set; }

        /// <summary>
        /// Gets whether the mod is currently installed.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(
            nameof(CanInstall),
            nameof(CanUpdate),
            nameof(CanRollback),
            nameof(CanToggleEnabled),
            nameof(ShowListInstallButton),
            nameof(ShowListIncompatibleBadge),
            nameof(ShowListWarningIcon))]
        public partial bool IsInstalled { get; private set; }

        /// <summary>
        /// Gets whether a list-initiated installation for this mod is currently in progress.
        /// </summary>
        [ObservableProperty]
        public partial bool IsInstalling { get; internal set; }

        /// <summary>
        /// Gets whether the user intends the installed mod to be enabled.
        /// </summary>
        [ObservableProperty]
        public partial bool IsEnabled { get; private set; }

        /// <summary>
        /// Gets the runtime activation state.
        /// </summary>
        [ObservableProperty]
        public partial ModState State { get; private set; }

        /// <summary>
        /// Gets the localized status badge label key when a badge should be displayed.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatusBadge))]
        public partial IDynamicLocaleKey? StatusBadgeTextKey { get; private set; }

        /// <summary>
        /// Gets whether <see cref="StatusBadgeTextKey"/> is populated.
        /// </summary>
        public bool HasStatusBadge => StatusBadgeTextKey is not null;

        /// <summary>
        /// Gets whether <see cref="State"/> represents a healthy active status.
        /// </summary>
        [ObservableProperty]
        public partial bool IsStatusActive { get; private set; }

        /// <summary>
        /// Gets whether <see cref="State"/> represents an ordinary inactive status.
        /// </summary>
        [ObservableProperty]
        public partial bool IsStatusInactive { get; private set; }

        /// <summary>
        /// Gets whether <see cref="State"/> represents an in-progress loading or stopping transition.
        /// </summary>
        [ObservableProperty]
        public partial bool IsStatusTransitioning { get; private set; }

        /// <summary>
        /// Gets whether <see cref="State"/> or diagnostics indicate an error status.
        /// </summary>
        [ObservableProperty]
        public partial bool IsStatusError { get; private set; }

        /// <summary>
        /// Gets whether the status represents a warning condition such as skipped or pending restart.
        /// </summary>
        [ObservableProperty]
        public partial bool IsStatusWarning { get; private set; }

        /// <summary>
        /// Gets the trust/signature verification icon shown in the hover card.
        /// </summary>
        [ObservableProperty]
        public partial TablerIconKind VerificationIcon { get; private set; } = TablerIconKind.RosetteDiscountCheck;

        /// <summary>
        /// Gets the localized source trust and signature verification description shown in the hover card.
        /// </summary>
        [ObservableProperty]
        public partial IDynamicLocaleKey VerificationTextKey { get; private set; } = DirectLocaleKey.Empty;

        /// <summary>
        /// Gets the concise localized verification badge label shown in the detail header.
        /// </summary>
        [ObservableProperty]
        public partial IDynamicLocaleKey VerificationBadgeKey { get; private set; } = DirectLocaleKey.Empty;

        /// <summary>
        /// Gets whether the mod originates from a verified or built-in trusted source.
        /// </summary>
        [ObservableProperty]
        public partial bool IsVerificationTrusted { get; private set; }

        /// <summary>
        /// Gets whether the mod originates from an unsigned remote source.
        /// </summary>
        [ObservableProperty]
        public partial bool IsVerificationWarning { get; private set; }

        /// <summary>
        /// Gets icon-paired metadata and verification items displayed in the detail header subtitle row.
        /// </summary>
        public IReadOnlyList<ModHeaderMetaItem> HeaderMetaItems => _headerMetaItems;

        /// <summary>
        /// Gets whether <see cref="HeaderMetaItems"/> contains any items.
        /// </summary>
        public bool HasHeaderMetaItems => _headerMetaItems.Count > 0;

        /// <summary>
        /// Gets secondary icon-paired metadata chips displayed in the hover card.
        /// </summary>
        public IReadOnlyList<ModToolTipChip> ToolTipChips => _toolTipChips;

        /// <summary>
        /// Gets whether <see cref="ToolTipChips"/> contains any items.
        /// </summary>
        public bool HasToolTipChips => _toolTipChips.Count > 0;

        /// <summary>
        /// Gets supplementary footer notes displayed in the hover card.
        /// </summary>
        public IReadOnlyList<ModToolTipNote> ToolTipNotes => _toolTipNotes;

        /// <summary>
        /// Gets whether <see cref="ToolTipNotes"/> contains any items.
        /// </summary>
        public bool HasToolTipNotes => _toolTipNotes.Count > 0;

        /// <summary>
        /// Gets localized category and label tags displayed in the Overview details sidebar.
        /// </summary>
        public IReadOnlyList<string> Tags => _tags;

        /// <summary>
        /// Gets whether <see cref="Tags"/> contains any items.
        /// </summary>
        public bool HasTags => _tags.Count > 0;

        /// <summary>
        /// Gets logical mod dependencies displayed in the Overview details sidebar.
        /// </summary>
        public IReadOnlyList<ModDependencyDisplayItem> DependencyItems => _dependencyItems;

        /// <summary>
        /// Gets whether <see cref="DependencyItems"/> contains any items.
        /// </summary>
        public bool HasDependencies => _dependencyItems.Count > 0;

        /// <summary>
        /// Gets whether the Overview Resources section has at least one link or action to display.
        /// </summary>
        public bool HasResources => HasHomepage || HasRepositoryUrl || HasLicense || IsLocalDirectory;

        /// <summary>
        /// Gets whether the compact list-row install button should be displayed.
        /// </summary>
        public bool ShowListInstallButton => !IsInstalled && HasCatalogEntry && IsCompatible;

        /// <summary>
        /// Gets whether the compact list-row incompatible badge should be displayed.
        /// </summary>
        public bool ShowListIncompatibleBadge => !IsInstalled && !IsCompatible;

        /// <summary>
        /// Gets whether the compact list-row warning icon should be displayed for installed mod notices.
        /// </summary>
        public bool ShowListWarningIcon => IsInstalled && HasNotices;

        /// <summary>
        /// Gets whether this installed mod currently exposes a settings view via <see cref="IModSettingsRegistry"/>.
        /// </summary>
        [ObservableProperty]
        public partial bool HasSettings { get; private set; }

        /// <summary>
        /// Gets the metadata rows displayed in the Overview details card.
        /// </summary>
        public IReadOnlyList<ModMetadataField> MetadataFields => _metadataFields;

        /// <summary>
        /// Gets whether the mod was imported as an unpacked local directory.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasResources))]
        public partial bool IsLocalDirectory { get; private set; }

        /// <summary>
        /// Gets the local directory path when <see cref="IsLocalDirectory"/> is true.
        /// </summary>
        [ObservableProperty]
        public partial string? LocalDirectoryPath { get; private set; }

        /// <summary>
        /// Gets whether an <c>N=1</c> rollback installation slot is retained.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRollback))]
        public partial bool HasRollback { get; private set; }

        /// <summary>
        /// Gets the retained prior version for rollback.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RollbackButtonContentKey))]
        public partial string? PreviousVersion { get; private set; }

        /// <summary>
        /// Gets the formatted label key for the rollback button.
        /// </summary>
        public IDynamicLocaleKey? RollbackButtonContentKey => HasRollback && PreviousVersion is not null
            ? new FormattedDynamicLocaleKey(LocaleKey.ModPage_RollbackButton_Content, new DirectLocaleKey(PreviousVersion))
            : null;

        /// <summary>
        /// Gets whether a compatible version exists in the repository catalog.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanInstall), nameof(ShowListInstallButton), nameof(ShowListIncompatibleBadge))]
        public partial bool IsCompatible { get; private set; } = true;

        /// <summary>
        /// Gets a localized incompatibility message when <see cref="IsCompatible"/> is false.
        /// </summary>
        [ObservableProperty]
        public partial IDynamicLocaleKey? IncompatibilityReason { get; private set; }

        /// <summary>
        /// Gets whether the Install button is available.
        /// </summary>
        public bool CanInstall => !IsInstalled && HasCatalogEntry && IsCompatible && !_isHostRestartRequired;

        /// <summary>
        /// Gets whether the Update button is available.
        /// </summary>
        public bool CanUpdate => IsInstalled && HasUpdate && !_isHostRestartRequired;

        /// <summary>
        /// Gets whether the Rollback button is available.
        /// </summary>
        public bool CanRollback => IsInstalled && HasRollback && !_isHostRestartRequired;

        /// <summary>
        /// Gets whether the enablement switch may change this mod's state.
        /// </summary>
        public bool CanToggleEnabled => IsInstalled && !_isHostRestartRequired;

        /// <summary>
        /// Gets whether this mod has a catalog entry.
        /// </summary>
        public bool HasCatalogEntry => CatalogEntry is not null;

        /// <summary>
        /// Gets the optional homepage URL.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasHomepage), nameof(HasResources))]
        public partial string? Homepage { get; private set; }

        /// <summary>
        /// Gets whether an HTTP(S) homepage URL is present.
        /// </summary>
        public bool HasHomepage => !string.IsNullOrWhiteSpace(Homepage);

        /// <summary>
        /// Gets the optional source repository URL.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRepositoryUrl), nameof(HasResources))]
        public partial string? RepositoryUrl { get; private set; }

        /// <summary>
        /// Gets whether a repository URL is present.
        /// </summary>
        public bool HasRepositoryUrl => !string.IsNullOrWhiteSpace(RepositoryUrl);

        /// <summary>
        /// Gets the optional SPDX license identifier.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLicense), nameof(LicenseLabelKey), nameof(HasResources))]
        public partial string? LicenseSpdx { get; private set; }

        /// <summary>
        /// Gets whether license metadata or documentation is available.
        /// </summary>
        public bool HasLicense => !string.IsNullOrWhiteSpace(LicenseSpdx) ||
            InstalledSummary?.License?.File is { Length: > 0 } ||
            CatalogVersion?.LicenseUrl is { Length: > 0 };

        /// <summary>
        /// Gets the localized label shown on the license viewer button.
        /// </summary>
        public IDynamicLocaleKey LicenseLabelKey => !string.IsNullOrWhiteSpace(LicenseSpdx)
            ? new DirectLocaleKey(LicenseSpdx)
            : new DynamicLocaleKey(LocaleKey.ModPage_Metadata_ViewLicense);

        /// <summary>
        /// Gets host-owned runtime diagnostics and skip notices for display in the detail pane.
        /// </summary>
        public IReadOnlyBindableList<ModNoticeItem> Notices => _notices;

        /// <summary>
        /// Gets whether any runtime notice or diagnostic exists.
        /// </summary>
        public bool HasNotices => _notices.Count > 0;

        /// <summary>
        /// Gets the localized category title declared by the repository.
        /// </summary>
        public string? CategoryTitle { get; private set; }

        /// <summary>
        /// Gets the category identifier declared by the repository.
        /// </summary>
        public string? CategoryId => CatalogEntry?.Metadata.Category;

        /// <summary>
        /// Gets the latest installed snapshot if installed.
        /// </summary>
        public ModSummary? InstalledSummary { get; private set; }

        /// <summary>
        /// Gets the merged catalog entry if present in an enabled catalog.
        /// </summary>
        public ModIndexEntry? CatalogEntry { get; private set; }

        /// <summary>
        /// Gets the repository index containing <see cref="CatalogEntry"/>, when available.
        /// </summary>
        public ModIndex? CatalogIndex { get; private set; }

        /// <summary>
        /// Gets the selected compatible catalog version, or fallback display version when incompatible.
        /// </summary>
        public ModIndexVersion? CatalogVersion { get; private set; }

        /// <summary>
        /// Gets the catalog source owning <see cref="CatalogEntry"/>.
        /// </summary>
        public ModSourceInfo? CatalogSource { get; private set; }

        /// <summary>
        /// Gets the source priority order in the merged catalog.
        /// </summary>
        internal int CatalogSourceOrder { get; private set; }

        /// <summary>
        /// Gets the entry order within its repository index.
        /// </summary>
        internal int CatalogModOrder { get; private set; }

        /// <summary>
        /// Gets all searchable strings for text filtering.
        /// </summary>
        internal IReadOnlyList<string> SearchValues { get; private set; } = [];

        private readonly BindableList<ModNoticeItem> _notices = [];
        private readonly BindableList<ModMetadataField> _metadataFields = [];
        private readonly BindableList<ModHeaderMetaItem> _headerMetaItems = [];
        private readonly BindableList<ModToolTipChip> _toolTipChips = [];
        private readonly BindableList<ModToolTipNote> _toolTipNotes = [];
        private readonly BindableList<string> _tags = [];
        private readonly BindableList<ModDependencyDisplayItem> _dependencyItems = [];
        private readonly ModPageViewModel _owner;
        private IReadOnlyDictionary<string, string>? _categoryTitleMap;
        private bool _isHostRestartRequired;

        /// <summary>
        /// Initializes a presentation object with stable identity <paramref name="id"/>.
        /// </summary>
        public ModItemPresentation(ModPageViewModel owner, string id)
        {
            _owner = owner;
            Id = id;
            DisplayName = id;
        }

        [RelayCommand(CanExecute = nameof(CanToggleEnabled))]
        private Task SetEnabledAsync(bool isEnabled, CancellationToken cancellationToken) =>
            _owner.InvokeSetModEnabledAsync(this, isEnabled, cancellationToken);

        /// <summary>
        /// Notifies UI bindings when external busy or restart locks affect toggle availability.
        /// </summary>
        internal void NotifyCanToggleChanged() => SetEnabledCommand.NotifyCanExecuteChanged();

        /// <summary>
        /// Updates all state and catalog fields in place without replacing this object reference.
        /// </summary>
        internal void Update(
            ModSummary? installed,
            ResolvedCatalogItem? catalog,
            ModSourceInfo? configuredSource,
            string? availableUpdateVersion,
            bool isHostRestartRequired
        )
        {
            InstalledSummary = installed;
            CatalogEntry = catalog?.Entry;
            CatalogIndex = catalog?.Index;
            CatalogVersion = catalog?.DisplayVersion;
            CatalogSource = catalog?.Source;
            CatalogSourceOrder = catalog?.SourceOrder ?? int.MaxValue;
            CatalogModOrder = catalog?.ModOrder ?? int.MaxValue;
            _categoryTitleMap = catalog?.CategoryTitle;
            _isHostRestartRequired = isHostRestartRequired;

            IsInstalled = installed is not null;
            IsEnabled = installed?.IsEnabled ?? false;
            State = installed?.State ?? ModState.Inactive;
            HasSettings = installed is not null && _owner.SettingsRegistry.HasSettings(Id);
            Version = installed?.Version ?? catalog?.DisplayVersion?.Version ?? "0.0.0";
            AvailableUpdateVersion = availableUpdateVersion;
            Author = installed?.Author ?? catalog?.Entry.Author;
            SourceId = installed?.SourceId ?? catalog?.Source.Id ?? string.Empty;
            SourceDisplayNameKey = string.IsNullOrEmpty(SourceId)
                ? new DirectLocaleKey(string.Empty)
                : ModLocalizedText.CreateSourceDisplayNameKey(SourceId);
            IconSource = ModLocalizedText.ResolveIconSource(installed, catalog?.Entry, catalog?.Source);
            IsLocalDirectory = installed?.SourceId == ModStateRecord.LocalDirectorySourceId;
            LocalDirectoryPath = installed?.LocalDirectoryPath;
            HasRollback = installed?.HasRollback ?? false;
            PreviousVersion = installed?.PreviousVersion;

            var isCompatible = installed is not null || catalog is null || catalog.CompatibleVersion is not null;
            IsCompatible = isCompatible;
            IncompatibilityReason = isCompatible ? null : catalog?.IncompatibilityReason;

            Homepage = installed?.Homepage ?? catalog?.Entry.Homepage;
            RepositoryUrl = installed?.Repository?.Url ?? catalog?.Entry.Repository?.Url;
            LicenseSpdx = installed?.License?.Spdx ?? catalog?.Entry.License?.Spdx;
            OnPropertyChanged(nameof(HasLicense));
            OnPropertyChanged(nameof(HasResources));

            UpdateStatusBadge(installed, catalog);
            UpdateVerification(catalog, configuredSource);
            RebuildNotices(installed, catalog);
            RefreshLocale();

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(CanRollback));
            OnPropertyChanged(nameof(CanToggleEnabled));
            OnPropertyChanged(nameof(ShowListInstallButton));
            OnPropertyChanged(nameof(ShowListIncompatibleBadge));
            OnPropertyChanged(nameof(ShowListWarningIcon));
            OnPropertyChanged(nameof(VersionBadgeText));
            SetEnabledCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Re-resolves locale-sensitive strings when the application language changes.
        /// </summary>
        internal void RefreshLocale()
        {
            DisplayName = ModLocalizedText.Resolve(
                InstalledSummary?.Name ?? CatalogEntry?.Name,
                fallback: Id);
            Description = ModLocalizedText.ResolveEntry(
                InstalledSummary?.Description ?? CatalogEntry?.Description);
            CategoryTitle = _categoryTitleMap is not null
                ? ModLocalizedText.Resolve(_categoryTitleMap, CategoryId ?? string.Empty)
                : null;

            RefreshMetadata();
            RefreshHeaderMeta();
            RefreshToolTip();

            var search = new List<string> { Id, DisplayName };
            if (!string.IsNullOrWhiteSpace(Description)) search.Add(Description);
            if (!string.IsNullOrWhiteSpace(Author)) search.Add(Author);
            if (!string.IsNullOrWhiteSpace(SourceId)) search.Add(SourceId);
            if (!string.IsNullOrWhiteSpace(CategoryTitle)) search.Add(CategoryTitle);
            if (CatalogEntry?.Metadata.Keywords is { Length: > 0 } keywords) search.AddRange(keywords);
            SearchValues = search;
        }

        private void RefreshMetadata()
        {
            var selectedEntry = ResolveSelectedEntry();
            var next = new List<ModMetadataField>
            {
                new(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_Id), Id, IsMonospace: true),
                new(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_Version), Version, IsMonospace: true),
                new(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_Source), null, SourceDisplayNameKey)
            };

            if (CatalogVersion?.Artifact.Size is > 0 and var artifactSize)
            {
                next.Add(new ModMetadataField(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_PackageSize), Humanizer.HumanizeBytes(artifactSize)));
            }

            if (CatalogVersion?.PublishedAt is > 0 and var publishedAt)
            {
                var publishedText = DateTimeOffset.FromUnixTimeSeconds(publishedAt)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd", CultureInfo.CurrentUICulture);
                next.Add(new ModMetadataField(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_PublishedAt), publishedText));
            }

            if (selectedEntry?.ModApiVersion is { Length: > 0 } apiVersion)
            {
                next.Add(new ModMetadataField(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_ApiVersion), apiVersion, IsMonospace: true));
            }

            if (!string.IsNullOrWhiteSpace(LocalDirectoryPath))
            {
                next.Add(new ModMetadataField(new DynamicLocaleKey(LocaleKey.ModPage_Metadata_LocalPath), LocalDirectoryPath, IsMonospace: true));
            }

            _metadataFields.Reset(next);

            var tags = new List<string>();
            var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(CategoryTitle) && seenTags.Add(CategoryTitle))
            {
                tags.Add(CategoryTitle);
            }

            if (CatalogEntry?.Metadata.Labels is { Length: > 0 } labelIds)
            {
                foreach (var labelId in labelIds)
                {
                    if (string.IsNullOrWhiteSpace(labelId)) continue;

                    var labelTitle = CatalogIndex?.Metadata.Labels.TryGetValue(labelId, out var decl) == true &&
                        decl.Title.Count > 0
                            ? ModLocalizedText.Resolve(decl.Title, labelId)
                            : labelId;
                    if (!string.IsNullOrWhiteSpace(labelTitle) && seenTags.Add(labelTitle))
                    {
                        tags.Add(labelTitle);
                    }
                }
            }

            _tags.Reset(tags);
            OnPropertyChanged(nameof(HasTags));

            var dependencies = selectedEntry?.Dependencies is { Length: > 0 } declaredDependencies
                ? declaredDependencies.Select(static dep => new ModDependencyDisplayItem(
                        dep.Id,
                        dep.IsOptional
                            ? new FormattedDynamicLocaleKey(LocaleKey.ModPage_Metadata_DependencyOptional, new DirectLocaleKey(dep.Version))
                            : new DirectLocaleKey(dep.Version)))
                    .ToList()
                : [];
            _dependencyItems.Reset(dependencies);
            OnPropertyChanged(nameof(HasDependencies));
        }

        private void RefreshHeaderMeta()
        {
            var items = new List<ModHeaderMetaItem>();
            if (!string.IsNullOrWhiteSpace(Author))
            {
                items.Add(
                    new ModHeaderMetaItem(
                        TablerIconKind.User,
                        new DirectLocaleKey(Author),
                        new DynamicLocaleKey(LocaleKey.ModPage_Metadata_Author)));
            }

            items.Add(
                new ModHeaderMetaItem(
                    VerificationIcon,
                    VerificationBadgeKey,
                    VerificationTextKey,
                    IsVerificationTrusted,
                    IsVerificationWarning));

            if (CatalogEntry?.Metadata.SortValues is { Count: > 0 } sortValues)
            {
                foreach (var (metricKey, metricValue) in sortValues)
                {
                    if (!metricValue.HasValue) continue;

                    var metricTitle = ResolveSortKeyTitle(metricKey);
                    var formattedValue = Humanizer.HumanizeNumber(metricValue.Value, "0.#", CultureInfo.CurrentUICulture);
                    var exactToolTip = new DirectLocaleKey($"{metricTitle}: {metricValue.Value.ToString("N0", CultureInfo.CurrentUICulture)}");
                    if (IsDownloadsMetricKey(metricKey))
                    {
                        items.Add(new ModHeaderMetaItem(TablerIconKind.Download, new DirectLocaleKey(formattedValue), exactToolTip));
                    }
                    else
                    {
                        items.Add(
                            new ModHeaderMetaItem(
                                TablerIconKind.ChartHistogram,
                                new DirectLocaleKey($"{metricTitle}: {formattedValue}"),
                                exactToolTip));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(LicenseSpdx))
            {
                items.Add(
                    new ModHeaderMetaItem(
                        TablerIconKind.Scale,
                        new DirectLocaleKey(LicenseSpdx),
                        new DynamicLocaleKey(LocaleKey.ModPage_Metadata_License)));
            }

            _headerMetaItems.Reset(items);
            OnPropertyChanged(nameof(HasHeaderMetaItems));
        }

        private UniversalModEntry? ResolveSelectedEntry()
        {
            var entry = InstalledSummary?.Entry ?? CatalogVersion?.Entry;
            if (entry is null) return null;

            var rid = _owner.ModManager.HostEnvironment?.RuntimeIdentifier ?? RuntimeInformation.RuntimeIdentifier;
            return ModPlatformSelector.Select(entry, rid) ??
                (entry is PlatformModEntry platformEntry ? platformEntry.Variants.FirstOrDefault() : null);
        }

        private string ResolveSortKeyTitle(string metricKey) =>
            CatalogIndex?.Metadata.SortKeys.TryGetValue(metricKey, out var decl) == true && decl.Title.Count > 0
                ? ModLocalizedText.Resolve(decl.Title, metricKey)
                : metricKey;

        private static bool IsDownloadsMetricKey(string key) =>
            string.Equals(key, "downloads", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "download_count", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "installs", StringComparison.OrdinalIgnoreCase);

        private void UpdateVerification(ResolvedCatalogItem? catalog, ModSourceInfo? configuredSource)
        {
            var effectiveSource = configuredSource ?? catalog?.Source;
            var isSigned = effectiveSource?.IsSignatureRequired == true ||
                !string.IsNullOrWhiteSpace(effectiveSource?.PublicKey) ||
                !string.IsNullOrWhiteSpace(catalog?.Index.PublicKey);

            IsVerificationTrusted = false;
            IsVerificationWarning = false;

            switch (SourceId)
            {
                case BuiltInSourceId:
                    VerificationIcon = TablerIconKind.RosetteDiscountCheck;
                    var builtInKey = new DynamicLocaleKey(LocaleKey.ModPage_Verification_BuiltIn);
                    VerificationBadgeKey = builtInKey;
                    VerificationTextKey = builtInKey;
                    IsVerificationTrusted = true;
                    break;
                case Modding.ModManager.OfficialSourceId when isSigned:
                    VerificationIcon = TablerIconKind.RosetteDiscountCheck;
                    VerificationBadgeKey = new DynamicLocaleKey(LocaleKey.ModPage_VerificationBadge_OfficialSigned);
                    VerificationTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Verification_OfficialSigned);
                    IsVerificationTrusted = true;
                    break;
                case ModStateRecord.LocalZipSourceId:
                    VerificationIcon = TablerIconKind.Package;
                    VerificationBadgeKey = new DynamicLocaleKey(LocaleKey.ModPage_VerificationBadge_LocalArchive);
                    VerificationTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Verification_LocalArchive);
                    break;
                case ModStateRecord.LocalDirectorySourceId:
                    VerificationIcon = TablerIconKind.FolderCode;
                    VerificationBadgeKey = new DynamicLocaleKey(LocaleKey.ModPage_VerificationBadge_LocalDirectory);
                    VerificationTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Verification_LocalDirectory);
                    break;
                default:
                    if (isSigned)
                    {
                        VerificationIcon = TablerIconKind.ShieldCheck;
                        VerificationBadgeKey = new FormattedDynamicLocaleKey(
                            LocaleKey.ModPage_VerificationBadge_CustomSigned,
                            SourceDisplayNameKey);
                        VerificationTextKey = new FormattedDynamicLocaleKey(
                            LocaleKey.ModPage_Verification_CustomSigned,
                            SourceDisplayNameKey);
                        IsVerificationTrusted = true;
                    }
                    else
                    {
                        VerificationIcon = TablerIconKind.ShieldExclamation;
                        VerificationBadgeKey = new FormattedDynamicLocaleKey(
                            LocaleKey.ModPage_VerificationBadge_UnsignedSource,
                            SourceDisplayNameKey);
                        VerificationTextKey = new FormattedDynamicLocaleKey(
                            LocaleKey.ModPage_Verification_UnsignedSource,
                            SourceDisplayNameKey);
                        IsVerificationWarning = true;
                    }

                    break;
            }
        }

        private void RefreshToolTip()
        {
            var chips = new List<ModToolTipChip>();
            if (!string.IsNullOrWhiteSpace(Author))
            {
                chips.Add(new ModToolTipChip(TablerIconKind.User, Author));
            }

            if (!string.IsNullOrWhiteSpace(LicenseSpdx))
            {
                chips.Add(new ModToolTipChip(TablerIconKind.Scale, LicenseSpdx));
            }

            if (!string.IsNullOrWhiteSpace(CategoryTitle))
            {
                chips.Add(new ModToolTipChip(TablerIconKind.Tag, CategoryTitle));
            }

            if (!IsInstalled && CatalogVersion?.Artifact.Size is > 0 and var artifactSize)
            {
                chips.Add(new ModToolTipChip(TablerIconKind.Server2, Humanizer.HumanizeBytes(artifactSize)));
            }

            if (CatalogEntry?.Metadata.SortValues is { Count: > 0 } sortValues)
            {
                foreach (var (metricKey, metricValue) in sortValues)
                {
                    if (!metricValue.HasValue) continue;

                    var formattedValue = Humanizer.HumanizeNumber(metricValue.Value, "0.#", CultureInfo.CurrentUICulture);
                    if (IsDownloadsMetricKey(metricKey))
                    {
                        chips.Add(new ModToolTipChip(TablerIconKind.Download, formattedValue));
                    }
                    else
                    {
                        var metricTitle = ResolveSortKeyTitle(metricKey);
                        chips.Add(new ModToolTipChip(TablerIconKind.ChartHistogram, $"{metricTitle}: {formattedValue}"));
                    }
                }
            }

            _toolTipChips.Reset(chips);
            OnPropertyChanged(nameof(HasToolTipChips));

            var notes = new List<ModToolTipNote>();
            if (InstalledSummary?.Intent == ModInstallIntent.AutoDependency)
            {
                notes.Add(new ModToolTipNote(TablerIconKind.GitFork, new DynamicLocaleKey(LocaleKey.ModPage_ToolTip_AutoDependency)));
            }

            if (CatalogEntry?.Channels is { Count: > 0 } channels)
            {
                foreach (var (channelName, channelVersion) in channels)
                {
                    if (string.Equals(channelName, "stable", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(channelVersion, Version, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    notes.Add(
                        new ModToolTipNote(
                            TablerIconKind.Stack2,
                            new FormattedDynamicLocaleKey(
                                LocaleKey.ModPage_ToolTip_ChannelAvailable,
                                new DirectLocaleKey(channelName),
                                new DirectLocaleKey(channelVersion))));
                    break;
                }
            }

            if (InstalledSummary is { IsPendingRestart: true } || InstalledSummary?.PendingOperation is not null)
            {
                notes.Add(
                    new ModToolTipNote(
                        TablerIconKind.Clock,
                        new DynamicLocaleKey(LocaleKey.ModPage_Status_PendingRestart),
                        IsWarning: true));
            }
            else if (_notices.Count > 0)
            {
                notes.Add(new ModToolTipNote(_notices[0].IconKind, _notices[0].SummaryKey, IsWarning: true));
            }

            _toolTipNotes.Reset(notes);
            OnPropertyChanged(nameof(HasToolTipNotes));
        }

        /// <summary>
        /// Gets an optional numeric sort metric declared in repository catalog metadata.
        /// </summary>
        internal double? TryGetSortValue(string key) =>
            CatalogEntry?.Metadata.SortValues.TryGetValue(key, out var value) == true ? value : null;

        private void UpdateStatusBadge(ModSummary? installed, ResolvedCatalogItem? catalog)
        {
            IsStatusActive = false;
            IsStatusInactive = false;
            IsStatusTransitioning = false;
            IsStatusWarning = false;
            IsStatusError = false;

            if (installed is null)
            {
                if (catalog is { CompatibleVersion: null })
                {
                    StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_IncompatibleBadge);
                    IsStatusWarning = true;
                }
                else
                {
                    StatusBadgeTextKey = null;
                }

                return;
            }

            if (installed.PendingOperation is not null || installed.IsPendingRestart)
            {
                StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Status_PendingRestart);
                IsStatusWarning = true;
                return;
            }

            switch (installed.State)
            {
                case ModState.Active:
                    StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Status_Active);
                    IsStatusActive = true;
                    break;
                case ModState.Initializing or ModState.Stopping:
                    StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Status_Loading);
                    IsStatusTransitioning = true;
                    break;
                case ModState.Failed or ModState.Blocked:
                    StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Status_Error);
                    IsStatusError = true;
                    break;
                case ModState.Skipped:
                    StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Status_Skipped);
                    IsStatusWarning = true;
                    break;
                default:
                    StatusBadgeTextKey = new DynamicLocaleKey(LocaleKey.ModPage_Status_Inactive);
                    IsStatusInactive = true;
                    break;
            }
        }

        private void RebuildNotices(ModSummary? installed, ResolvedCatalogItem? catalog)
        {
            var next = new List<ModNoticeItem>();

            if (installed?.SkipReason is { } skipReason)
            {
                var (skipTitle, skipSummary) = skipReason == ModSkipReason.UnsupportedPlatform
                    ? (
                        new DynamicLocaleKey(LocaleKey.ModPage_SkipReason_UnsupportedPlatform_Title) as IDynamicLocaleKey,
                        new DynamicLocaleKey(LocaleKey.ModPage_SkipReason_UnsupportedPlatform_Detail) as IDynamicLocaleKey
                    )
                    : (
                        new FormattedDynamicLocaleKey(LocaleKey.ModPage_Diagnostic_Generic, new DirectLocaleKey(skipReason.ToString())),
                        new DirectLocaleKey(skipReason.ToString())
                    );
                next.Add(new ModNoticeItem(skipTitle, skipSummary, TechnicalDetail: null, IsError: false));
            }
            else if (catalog is { CompatibleVersion: null, IncompatibilityReason: { } reasonKey })
            {
                next.Add(
                    new ModNoticeItem(
                        new DynamicLocaleKey(LocaleKey.ModPage_IncompatibleBadge),
                        reasonKey,
                        TechnicalDetail: null,
                        IsError: false));
            }

            if (installed is { Diagnostics.Count: > 0 })
            {
                next.AddRange(installed.Diagnostics.Select(CreateDiagnosticNotice));
            }

            _notices.Reset(next);
            OnPropertyChanged(nameof(HasNotices));
            OnPropertyChanged(nameof(ShowListWarningIcon));
        }
    }

    private static ModNoticeItem CreateDiagnosticNotice(ModDiagnostic diagnostic)
    {
        var (titleKey, isError) = MapDiagnosticMeta(diagnostic.Code);
        var (summary, technicalDetail) = SplitDiagnosticDetail(diagnostic.Detail);
        var summaryKey = string.IsNullOrWhiteSpace(summary)
            ? titleKey
            : new DirectLocaleKey(summary);
        return new ModNoticeItem(titleKey, summaryKey, technicalDetail, isError);
    }

    private static (IDynamicLocaleKey TitleKey, bool IsError) MapDiagnosticMeta(string code) => code switch
    {
        "ModInitializeFailed" => (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_ModInitializeFailed), true),
        "ModRetirementFailed" => (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_ModRetirementFailed), true),
        "DependencyFailed" or "MissingDependency" or "InvalidDependencyMapping" =>
            (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_DependencyFailed), true),
        "AssemblyPreparationFailed" or "InvalidEntryType" or "PreflightFailed" =>
            (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_LoadFailed), true),
        "AssemblyConflict" or "SharedBindingConflict" or "AssemblyCycle" or "ModCycle" or "DuplicateMod" =>
            (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_Conflict), true),
        "IncompatibleHost" => (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_IncompatibleHost), true),
        "InstallationMissing" or "PendingUpdateMissing" =>
            (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_InstallationMissing), true),
        "LocalDirectoryMissing" => (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_LocalDirectoryIssue), false),
        "LocalDirectoryIdMismatch" or "LocalDirectorySyncFailed" =>
            (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_LocalDirectoryIssue), true),
        "CollectionUnconfirmed" or "AssemblyCleanupFailed" or "AssemblyCleanupTimedOut" =>
            (new DynamicLocaleKey(LocaleKey.ModPage_Diagnostic_PendingUnload), false),
        _ => (new FormattedDynamicLocaleKey(LocaleKey.ModPage_Diagnostic_Generic, new DirectLocaleKey(code)), true)
    };

    private static (string Summary, string? TechnicalDetail) SplitDiagnosticDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return (string.Empty, null);
        }

        var trimmed = detail.Trim();
        var newlineIndex = trimmed.IndexOfAny(['\r', '\n']);
        if (newlineIndex < 0)
        {
            return (trimmed, null);
        }

        var firstLine = trimmed[..newlineIndex].Trim();
        return string.IsNullOrEmpty(firstLine) ? (trimmed, null) : (firstLine, trimmed);
    }

    /// <summary>
    /// Represents a warning or error banner displayed above a mod's detail tabs.
    /// </summary>
    public sealed record ModNoticeItem(
        IDynamicLocaleKey TitleKey,
        IDynamicLocaleKey SummaryKey,
        string? TechnicalDetail,
        bool IsError
    )
    {
        /// <summary>
        /// Gets the ShadUI notification severity for this notice.
        /// </summary>
        public Notification NotificationType => IsError ? Notification.Error : Notification.Warning;

        /// <summary>
        /// Gets the Tabler icon matching the notice severity.
        /// </summary>
        public TablerIconKind IconKind => IsError ? TablerIconKind.AlertCircle : TablerIconKind.AlertTriangle;

        /// <summary>
        /// Gets whether collapsible raw diagnostic details are available.
        /// </summary>
        public bool HasTechnicalDetail => !string.IsNullOrWhiteSpace(TechnicalDetail);
    }

    internal sealed record ResolvedCatalogItem(
        ModSourceInfo Source,
        ModIndex Index,
        ModIndexEntry Entry,
        ModIndexVersion? CompatibleVersion,
        ModIndexVersion? DisplayVersion,
        IDynamicLocaleKey? IncompatibilityReason,
        IReadOnlyDictionary<string, string>? CategoryTitle,
        int SourceOrder,
        int ModOrder
    );
}
