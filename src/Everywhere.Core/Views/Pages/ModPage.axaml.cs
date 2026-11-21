using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Modding;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Views.Modding;
using Tabler.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views.Pages;

/// <summary>
/// Displays installed mods, repository discovery, metadata/README documentation, and mod settings.
/// Owns View-layer confirmation dialogs, file/folder pickers, and lazy settings mounting.
/// </summary>
public sealed partial class ModPage : ReactiveUserControl<ModPageViewModel>, IMainViewNavigationTopLevelItem
{
    /// <summary>
    /// Defines the <see cref="ActiveSettingsControl"/> direct property.
    /// </summary>
    public static readonly DirectProperty<ModPage, Control?> ActiveSettingsControlProperty =
        AvaloniaProperty.RegisterDirect<ModPage, Control?>(
            nameof(ActiveSettingsControl),
            owner => owner.ActiveSettingsControl);

    /// <inheritdoc />
    public int Index => 4;

    /// <inheritdoc />
    public TablerIconKind Icon => TablerIconKind.Puzzle;

    /// <inheritdoc />
    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.ModPage_Title);

    /// <summary>
    /// Gets the lazily created settings control mounted in the active mod's Settings tab.
    /// </summary>
    public Control? ActiveSettingsControl
    {
        get;
        private set => SetAndRaise(ActiveSettingsControlProperty, ref field, value);
    }

    private readonly PersistentState? _persistentState;
    private ModPageViewModel.ModItemPresentation? _observedSelectedMod;
    private bool _isShowingIntroduction;

    /// <summary>
    /// Initializes a new <see cref="ModPage"/> bound to <see cref="ModPageViewModel"/>.
    /// </summary>
    public ModPage(IServiceProvider serviceProvider) : base(serviceProvider, disposeOnUnloaded: false)
    {
        _persistentState = serviceProvider.GetService<PersistentState>();
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        ViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        ObserveSelectedMod(ViewModel.SelectedMod);
        ManageSourcesCommand.NotifyCanExecuteChanged();
        NotifyMutationCommandsCanExecuteChanged();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        ObserveSelectedMod(null);
        ActiveSettingsControl = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        ViewModel.EnableModAsyncHandler = HandleSetModEnabledAsync;
        SyncSettingsControl();
        ShowIntroductionIfNeededAsync().Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    /// <inheritdoc />
    protected override void OnUnloaded(RoutedEventArgs e)
    {
        ViewModel.EnableModAsyncHandler = null;
        ActiveSettingsControl = null;

        base.OnUnloaded(e);
    }

    private bool CanMutateMods => ViewModel.CanMutateMods;

    private bool IsViewModelNotBusy => ViewModel.IsNotBusy;

    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private async Task ImportZipAsync(CancellationToken cancellationToken)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is null) return;

        var files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = LocaleKey.ModPage_ImportZipPicker_Title.I18N(),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Mod archive")
                    {
                        Patterns = ["*.zip"]
                    }
                ]
            });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var result = await ViewModel.ImportZipFromPathAsync(path, cancellationToken);
        await PresentOperationResultAsync(result, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private async Task ImportDirectoryAsync(CancellationToken cancellationToken)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is null) return;

        var folders = await storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = LocaleKey.ModPage_ImportDirectoryPicker_Title.I18N(),
                AllowMultiple = false
            });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var result = await ViewModel.ImportDirectoryFromPathAsync(path, cancellationToken);
        await PresentOperationResultAsync(result, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(IsViewModelNotBusy))]
    private async Task ManageSourcesAsync(CancellationToken cancellationToken)
    {
        var dialog = new ModSourcesDialog(ViewModel.ModManager);
        await DialogManager
            .CreateDialog(dialog, LocaleKey.ModPage_SourcesDialog_Title.I18N(), TopLevel.GetTopLevel(this))
            .WithPrimaryButton(LocaleKey.Common_OK.I18N())
            .WithMaxWidth(720)
            .Dismissible()
            .ShowAsync(cancellationToken);

        if (dialog.HasChanges)
        {
            await ViewModel.RefreshCatalogsQuietAsync(cancellationToken);
        }
    }

    [RelayCommand]
    private async Task ShowRestartDetailsAsync(CancellationToken cancellationToken)
    {
        var snapshot = ViewModel.CreateRestartDetailsSnapshot();
        var body = new ModRestartDetailsDialog(snapshot);
        try
        {
            var dialogResult = await DialogManager
                .CreateDialog(body, LocaleKey.ModPage_RestartDialog_Title.I18N(), TopLevel.GetTopLevel(this))
                .WithPrimaryButton(LocaleKey.ModPage_RestartDialog_RestartButton.I18N())
                .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                .WithMaxWidth(580)
                .Dismissible()
                .ShowAsync(cancellationToken);

            if (dialogResult == DialogResult.Primary)
            {
                (Application.Current as App)?.Restart("--ui");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [RelayCommand]
    private async Task ShowLicenseAsync(ModPageViewModel.ModItemPresentation? item, CancellationToken cancellationToken)
    {
        if (item is null || !item.HasLicense) return;

        var topLevel = TopLevel.GetTopLevel(this);
        try
        {
            var licenseText = await ViewModel.LoadCombinedLicenseDocumentAsync(item, cancellationToken);
            var titleArg = !string.IsNullOrWhiteSpace(item.LicenseSpdx)
                ? $"{item.DisplayName} ({item.LicenseSpdx})"
                : item.DisplayName;
            await DialogManager
                .CreateDialog(new ModLicenseDialogView(licenseText), LocaleKey.ModPage_LicenseDialog_Title.I18N().Format(titleArg), topLevel)
                .WithPrimaryButton(LocaleKey.Common_OK.I18N())
                .Dismissible()
                .ShowAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ToastManager.ResolveHost(topLevel)?
                .CreateToast(LocaleKey.Common_Error.I18N())
                .WithContent(HandledSystemException.Handle(exception).GetFriendlyMessage())
                .DismissOnClick()
                .ShowError();
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private async Task InstallModAsync(ModPageViewModel.ModItemPresentation? item, CancellationToken cancellationToken)
    {
        if (item is not { CanInstall: true }) return;

        item.IsInstalling = true;
        try
        {
            await ViewModel.ExecuteBusyMutationAsync(
                token => RunPlannedInstallOrUpdateAsync(
                    item,
                    isUpdate: false,
                    preferredSourceId: item.CatalogSource?.Id,
                    channel: null,
                    token),
                cancellationToken);
        }
        finally
        {
            item.IsInstalling = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private Task UpdateModAsync(ModPageViewModel.ModItemPresentation? item, CancellationToken cancellationToken)
    {
        if (item is not { CanUpdate: true }) return Task.CompletedTask;

        var summary = item.InstalledSummary;
        return ViewModel.ExecuteBusyMutationAsync(
            token => RunPlannedInstallOrUpdateAsync(
                item,
                isUpdate: true,
                preferredSourceId: summary?.SourceId is "builtin" ? null : summary?.SourceId,
                channel: summary?.PreferredChannel,
                token),
            cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private Task UninstallModAsync(ModPageViewModel.ModItemPresentation? item, CancellationToken cancellationToken)
    {
        if (item is not { IsInstalled: true }) return Task.CompletedTask;

        return ViewModel.ExecuteBusyMutationAsync(
            async token =>
            {
                var preview = ViewModel.CreateUninstallPreview(item);
                var form = new ModUninstallConfirmForm(preview);
                var confirmed = await DialogManager
                    .CreateDialog(form, LocaleKey.ModPage_ConfirmUninstall_DialogTitle.I18N().Format(item.DisplayName), TopLevel.GetTopLevel(this))
                    .WithPrimaryButton(LocaleKey.ModPage_UninstallButton_Content.I18N(), buttonStyle: ButtonStyle.Destructive)
                    .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                    .ShowAsync(token) == DialogResult.Primary;
                if (!confirmed) return;

                var result = await ViewModel.ModManager.UninstallAsync(
                    item.Id,
                    shouldDeleteData: form.ShouldDeleteData,
                    shouldCascadeDependents: preview.HasDependentsWarning,
                    shouldRemoveOrphanDependencies: form.ShouldRemoveOrphans,
                    cancellationToken: token);
                await PresentOperationResultAsync(result, token);
            },
            cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanMutateMods))]
    private Task RollbackModAsync(ModPageViewModel.ModItemPresentation? item, CancellationToken cancellationToken)
    {
        if (item is not { HasRollback: true, PreviousVersion: { Length: > 0 } previousVersion }) return Task.CompletedTask;

        return ViewModel.ExecuteBusyMutationAsync(
            async token =>
            {
                var confirmed = await DialogManager
                    .CreateDialog(
                        LocaleKey.ModPage_ConfirmRollback_Message.I18N().Format(item.DisplayName, previousVersion),
                        LocaleKey.ModPage_ConfirmRollback_DialogTitle.I18N(),
                        TopLevel.GetTopLevel(this))
                    .WithPrimaryButton(LocaleKey.ModPage_RollbackButton_Content.I18N().Format(previousVersion))
                    .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                    .ShowAsync(token) == DialogResult.Primary;
                if (!confirmed) return;

                var result = await ViewModel.ModManager.RollbackAsync(item.Id, token);
                await PresentOperationResultAsync(result, token);
            },
            cancellationToken);
    }

    private async Task HandleSetModEnabledAsync(ModPageViewModel.ModItemPresentation item, bool targetEnabled, CancellationToken cancellationToken)
    {
        var modManager = ViewModel.ModManager;
        var result = await modManager.SetEnabledAsync(
            item.Id,
            targetEnabled,
            shouldCascadeDependents: false,
            shouldCascadeDependencies: false,
            cancellationToken: cancellationToken);

        if (!targetEnabled &&
            result is { Status: ModOperationStatus.ConfirmationRequired, AffectedDependents.Count: > 0 })
        {
            var dependentNames = ViewModel.FormatModList(result.AffectedDependents);
            var message = LocaleKey.ModPage_ConfirmCascadeDisable_Message.I18N().Format(item.DisplayName, dependentNames);
            var confirmed = await DialogManager
                .CreateDialog(message, LocaleKey.ModPage_ConfirmCascadeDisable_DialogTitle.I18N(), TopLevel.GetTopLevel(this))
                .WithPrimaryButton(LocaleKey.Common_Yes.I18N(), buttonStyle: ButtonStyle.Destructive)
                .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                .ShowAsync(cancellationToken) == DialogResult.Primary;
            if (!confirmed) return;

            result = await modManager.SetEnabledAsync(
                item.Id,
                isEnabled: false,
                shouldCascadeDependents: true,
                shouldCascadeDependencies: false,
                cancellationToken: cancellationToken);
        }
        else if (targetEnabled && result.Status == ModOperationStatus.ConfirmationRequired)
        {
            var hasDownloads = result.SolveResult?.Packages.Any(static pkg => !pkg.IsAlreadyInstalledVersion) == true;
            if (hasDownloads || result.PendingLicenses.Count > 0)
            {
                var accepted = await ConfirmInstallPlanAsync(result, item.Id, cancellationToken);
                if (accepted is null) return;

                var summary = item.InstalledSummary;
                result = await ViewModel.RunDownloadWithProgressAsync(
                    item.DisplayName,
                    isUpdate: false,
                    progress => modManager.InstallAsync(
                        item.Id,
                        preferredSourceId: summary?.SourceId,
                        channel: summary?.PreferredChannel,
                        acceptedLicenses: accepted,
                        progress: progress,
                        cancellationToken: cancellationToken));
            }
            else if (result.AffectedDependencies.Count > 0)
            {
                var dependencyNames = ViewModel.FormatModList(result.AffectedDependencies);
                var message = LocaleKey.ModPage_ConfirmCascadeEnable_Message
                    .I18N()
                    .Format(item.DisplayName, dependencyNames, result.AffectedDependencies.Count);
                var confirmed = await DialogManager
                    .CreateDialog(message, LocaleKey.ModPage_ConfirmCascadeEnable_DialogTitle.I18N(), TopLevel.GetTopLevel(this))
                    .WithPrimaryButton(LocaleKey.Common_Yes.I18N())
                    .WithCancelButton(LocaleKey.Common_Cancel.I18N())
                    .ShowAsync(cancellationToken) == DialogResult.Primary;
                if (!confirmed) return;

                result = await modManager.SetEnabledAsync(
                    item.Id,
                    isEnabled: true,
                    shouldCascadeDependents: false,
                    shouldCascadeDependencies: true,
                    cancellationToken: cancellationToken);
            }
        }

        await PresentOperationResultAsync(result, cancellationToken);
    }

    private async Task RunPlannedInstallOrUpdateAsync(
        ModPageViewModel.ModItemPresentation item,
        bool isUpdate,
        string? preferredSourceId,
        string? channel,
        CancellationToken cancellationToken
    )
    {
        var modManager = ViewModel.ModManager;
        var plan = await modManager.PlanInstallAsync(
            item.Id,
            preferredSourceId: preferredSourceId,
            versionRange: null,
            channel: channel,
            cancellationToken: cancellationToken);
        if (plan.Status is not (ModOperationStatus.Applied or ModOperationStatus.ConfirmationRequired))
        {
            await PresentOperationResultAsync(plan, cancellationToken);
            return;
        }

        IReadOnlyDictionary<string, ModAcceptedLicense>? accepted = null;
        var hasExtraDownloads = plan.SolveResult?.Packages.Any(pkg =>
            !pkg.IsAlreadyInstalledVersion && !string.Equals(pkg.Id, item.Id, StringComparison.Ordinal)) == true;
        if (plan.PendingLicenses.Count > 0 || hasExtraDownloads || plan.AffectedDependencies.Count > 0)
        {
            accepted = await ConfirmInstallPlanAsync(plan, item.Id, cancellationToken);
            if (accepted is null) return;
        }

        var result = await ViewModel.RunDownloadWithProgressAsync(
            item.DisplayName,
            isUpdate,
            progress => isUpdate
                ? modManager.UpdateAsync(
                    item.Id,
                    channel: channel,
                    acceptedLicenses: accepted,
                    progress: progress,
                    cancellationToken: cancellationToken)
                : modManager.InstallAsync(
                    item.Id,
                    preferredSourceId: preferredSourceId,
                    channel: channel,
                    acceptedLicenses: accepted,
                    progress: progress,
                    cancellationToken: cancellationToken));

        await PresentOperationResultAsync(result, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, ModAcceptedLicense>?> ConfirmInstallPlanAsync(
        ModOperationResult plan,
        string targetModId,
        CancellationToken cancellationToken
    )
    {
        var preview = ViewModel.BuildInstallPlanPreview(plan, targetModId);
        var form = new ModInstallConfirmForm(preview, ViewModel.ModManager);
        var dialogResult = await DialogManager
            .CreateDialog(form, LocaleKey.ModPage_ConfirmInstall_DialogTitle.I18N(), TopLevel.GetTopLevel(this))
            .WithPrimaryButton(form.PrimaryButtonText)
            .WithCancelButton(LocaleKey.Common_Cancel.I18N())
            .ShowAsync(cancellationToken);
        return dialogResult == DialogResult.Primary ? form.BuildAcceptedLicenses() : null;
    }

    private async Task PresentOperationResultAsync(ModOperationResult result, CancellationToken cancellationToken)
    {
        if (result is { Status: ModOperationStatus.Failed, SolveResult: { } solve })
        {
            var preview = ModPageViewModel.BuildDependencyFailurePreview(solve, result.Message);
            await DialogManager
                .CreateDialog(new ModDependencyErrorDialog(preview), LocaleKey.ModPage_Error_DependenciesUnsatisfied_DialogTitle.I18N(), TopLevel.GetTopLevel(this))
                .WithPrimaryButton(LocaleKey.Common_OK.I18N())
                .WithMaxWidth(540)
                .Dismissible()
                .ShowAsync(cancellationToken);
            return;
        }

        ViewModel.ReportOperationFailureToast(result);
    }

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ModPageViewModel.CanMutateMods) or nameof(ModPageViewModel.IsNotBusy))
        {
            ManageSourcesCommand.NotifyCanExecuteChanged();
            NotifyMutationCommandsCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(ModPageViewModel.SelectedMod))
        {
            ObserveSelectedMod(ViewModel.SelectedMod);
            SyncSettingsControl();
        }
        else if (e.PropertyName == nameof(ModPageViewModel.SelectedDetailTabIndex))
        {
            SyncSettingsControl();
        }
    }

    private void HandleObservedSelectedModPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModPageViewModel.ModItemPresentation.HasSettings))
        {
            SyncSettingsControl();
        }
    }

    private void ObserveSelectedMod(ModPageViewModel.ModItemPresentation? mod)
    {
        if (ReferenceEquals(_observedSelectedMod, mod)) return;

        if (_observedSelectedMod is not null)
        {
            _observedSelectedMod.PropertyChanged -= HandleObservedSelectedModPropertyChanged;
        }

        _observedSelectedMod = mod;
        if (_observedSelectedMod is not null)
        {
            _observedSelectedMod.PropertyChanged += HandleObservedSelectedModPropertyChanged;
        }
    }

    private void NotifyMutationCommandsCanExecuteChanged()
    {
        ImportZipCommand.NotifyCanExecuteChanged();
        ImportDirectoryCommand.NotifyCanExecuteChanged();
        InstallModCommand.NotifyCanExecuteChanged();
        UpdateModCommand.NotifyCanExecuteChanged();
        UninstallModCommand.NotifyCanExecuteChanged();
        RollbackModCommand.NotifyCanExecuteChanged();
    }

    private void SyncSettingsControl()
    {
        Dispatcher.UIThread.VerifyAccess();

        Control? nextControl = null;
        if (IsLoaded && ViewModel is { SelectedDetailTabIndex: 1, SelectedMod: { IsInstalled: true, HasSettings: true } selected })
        {
            nextControl = ViewModel.SettingsRegistry.CreateView(selected.Id);
        }

        if (ReferenceEquals(ActiveSettingsControl, nextControl)) return;

        // Clear any prior cached control first so Avalonia detaches it cleanly before mounting the next one.
        if (ActiveSettingsControl is not null)
        {
            ActiveSettingsControl = null;
        }

        ActiveSettingsControl = nextControl;
    }

    private async Task ShowIntroductionIfNeededAsync()
    {
        if (_isShowingIntroduction || _persistentState is not { HasShownModdingIntroduction: false })
        {
            return;
        }

        if (DialogManager.ResolveHost(TopLevel.GetTopLevel(this)) is not { } dialogHost)
        {
            return;
        }

        _isShowingIntroduction = true;
        try
        {
            var result = await dialogHost
                .CreateDialog(new ModdingIntroduction(), LocaleKey.ModPage_IntroductionDialog_Title.I18N())
                .WithPrimaryButton(LocaleKey.ModPage_IntroductionDialog_Confirm.I18N())
                .ShowAsync();
            if (result == DialogResult.Primary)
            {
                _persistentState.HasShownModdingIntroduction = true;
            }
        }
        finally
        {
            _isShowingIntroduction = false;
        }
    }
}
