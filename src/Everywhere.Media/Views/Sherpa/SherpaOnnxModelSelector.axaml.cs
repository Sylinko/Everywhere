using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Media.SpeechRecognition.Sherpa;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Media.Views.Sherpa;

public sealed partial class SherpaOnnxModelSelector(IServiceProvider serviceProvider) : TemplatedControl
{

    public static readonly DirectProperty<SherpaOnnxModelSelector, string?> SelectedModelIdProperty =
        AvaloniaProperty.RegisterDirect<SherpaOnnxModelSelector, string?>(
            nameof(SelectedModelId),
            o => o.SelectedModelId,
            (o, v) => o.SelectedModelId = v);

    public string? SelectedModelId
    {
        get;
        set => SetAndRaise(SelectedModelIdProperty, ref field, value);
    }

    public static readonly DirectProperty<SherpaOnnxModelSelector, SherpaOnnxModelMetadata?> SelectedModelProperty =
        AvaloniaProperty.RegisterDirect<SherpaOnnxModelSelector, SherpaOnnxModelMetadata?>(
            nameof(SelectedModel),
            o => o.SelectedModel);

    public SherpaOnnxModelMetadata? SelectedModel
    {
        get;
        private set => SetAndRaise(SelectedModelProperty, ref field, value);
    }

    public static readonly DirectProperty<SherpaOnnxModelSelector, string?> SupportedLocalesTextProperty =
        AvaloniaProperty.RegisterDirect<SherpaOnnxModelSelector, string?>(
            nameof(SupportedLocalesText),
            o => o.SupportedLocalesText);

    public string? SupportedLocalesText
    {
        get;
        private set => SetAndRaise(SupportedLocalesTextProperty, ref field, value);
    }

    public static readonly DirectProperty<SherpaOnnxModelSelector, IDynamicResourceKey> ActionContentKeyProperty =
        AvaloniaProperty.RegisterDirect<SherpaOnnxModelSelector, IDynamicResourceKey>(
            nameof(ActionContentKey),
            o => o.ActionContentKey);

    public IDynamicResourceKey ActionContentKey
    {
        get;
        private set => SetAndRaise(ActionContentKeyProperty, ref field, value);
    } = new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_DownloadButton_Content);

    public static readonly DirectProperty<SherpaOnnxModelSelector, bool> CanInstallProperty =
        AvaloniaProperty.RegisterDirect<SherpaOnnxModelSelector, bool>(
            nameof(CanInstall),
            o => o.CanInstall);

    public bool CanInstall
    {
        get;
        private set
        {
            if (field == value) return;
            SetAndRaise(CanInstallProperty, ref field, value);
            InstallModelCommand.NotifyCanExecuteChanged();
        }
    } = true;

    public static readonly DirectProperty<SherpaOnnxModelSelector, bool> IsBusyProperty =
        AvaloniaProperty.RegisterDirect<SherpaOnnxModelSelector, bool>(
            nameof(IsInstalling),
            o => o.IsInstalling);

    public bool IsInstalling
    {
        get;
        private set => SetAndRaise(IsBusyProperty, ref field, value);
    }

    public IReadOnlyList<SherpaOnnxModelMetadata> Models => _registry.Models;

    private readonly SherpaOnnxModelRegistry _registry = serviceProvider.GetRequiredService<SherpaOnnxModelRegistry>();
    private readonly SherpaOnnxModelInstaller _installer = serviceProvider.GetRequiredService<SherpaOnnxModelInstaller>();
    private readonly ToastHost _toastHost = serviceProvider.GetRequiredService<ToastHost>();
    private readonly ILogger<SherpaOnnxModelSelector> _logger = serviceProvider.GetRequiredService<ILogger<SherpaOnnxModelSelector>>();

    private bool _isAttached;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _isAttached = true;
        _installer.PropertyChanged += HandleInstallerPropertyChanged;
        EnsureSelectedModel();
        UpdateSelectedModel();
        if (!RefreshStatusCommand.IsRunning)
        {
            RefreshStatusCommand.Execute(null);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _installer.PropertyChanged -= HandleInstallerPropertyChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SelectedModelIdProperty)
        {
            UpdateSelectedModel();
            if (_isAttached && !RefreshStatusCommand.IsRunning)
            {
                RefreshStatusCommand.Execute(null);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        var model = GetSelectedModel();
        await _installer.RefreshStateAsync(model.Id, cancellationToken);
        UpdateInstallStatePresentation();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallModelAsync(CancellationToken cancellationToken)
    {
        var model = GetSelectedModel();
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new Progress<double>();

        _toastHost
            .CreateToast(AbstractionsLocaleResolver.Common_Info)
            .WithContent(LocaleResolver.SherpaOnnxModelSelector_DownloadingToast_Content.Format(model.DisplayName))
            .WithProgress(progress)
            .WithCancellationTokenSource(cancellationTokenSource)
            .OnBottomRight()
            .ShowInfo();

        try
        {
            await _installer.EnsureInstalledAsync(model.Id, progress, cancellationTokenSource.Token);
            _toastHost
                .CreateToast(AbstractionsLocaleResolver.Common_Info)
                .WithContent(LocaleResolver.SherpaOnnxModelSelector_InstalledToast_Content.Format(model.DisplayName))
                .DismissOnClick()
                .ShowSuccess();
        }
        catch (OperationCanceledException)
        {
            await _installer.RefreshStateAsync(model.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to install sherpa-onnx model {ModelId}.", model.Id);
            _toastHost
                .CreateToast(AbstractionsLocaleResolver.Common_Error)
                .WithContent(ex.GetFriendlyMessage())
                .DismissOnClick()
                .ShowError();
        }
        finally
        {
            UpdateInstallStatePresentation();
        }
    }

    private void HandleInstallerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SherpaOnnxModelInstaller.State) or nameof(SherpaOnnxModelInstaller.CurrentModelId))
        {
            Dispatcher.UIThread.Post(UpdateInstallStatePresentation);
        }
    }

    private void EnsureSelectedModel()
    {
        if (!string.IsNullOrWhiteSpace(SelectedModelId)) return;
        SelectedModelId = SherpaOnnxModelRegistry.DefaultModelId;
    }

    private SherpaOnnxModelMetadata GetSelectedModel() => _registry.GetModel(SelectedModelId);

    private void UpdateSelectedModel()
    {
        SelectedModel = GetSelectedModel();
        SupportedLocalesText = string.Join(", ", SelectedModel.SupportedLocales.Select(static locale => locale.ToNativeName()));
    }

    private void UpdateInstallStatePresentation()
    {
        var model = GetSelectedModel();
        var state = string.Equals(_installer.CurrentModelId, model.Id, StringComparison.Ordinal) ?
            _installer.State :
            SherpaOnnxModelInstallState.NotInstalled;

        IsInstalling = state is
            SherpaOnnxModelInstallState.Downloading or
            SherpaOnnxModelInstallState.Verifying or
            SherpaOnnxModelInstallState.Installing;

        CanInstall = state is
            SherpaOnnxModelInstallState.NotInstalled or
            SherpaOnnxModelInstallState.DownloadFailed or
            SherpaOnnxModelInstallState.Corrupted or
            SherpaOnnxModelInstallState.UpdateAvailable;

        ActionContentKey = state switch
        {
            SherpaOnnxModelInstallState.Downloading => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_DownloadingButton_Content),
            SherpaOnnxModelInstallState.Verifying => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_VerifyingButton_Content),
            SherpaOnnxModelInstallState.Installing => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_InstallingButton_Content),
            SherpaOnnxModelInstallState.Installed => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_InstalledButton_Content),
            SherpaOnnxModelInstallState.Corrupted => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_RepairButton_Content),
            SherpaOnnxModelInstallState.DownloadFailed => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_RetryButton_Content),
            SherpaOnnxModelInstallState.Unsupported => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_UnsupportedButton_Content),
            _ => new DynamicResourceKey(LocaleKey.SherpaOnnxModelSelector_DownloadButton_Content)
        };
    }
}
