using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace Everywhere.Views;

public partial class SoftwareUpdateControl(
    PersistentState persistentState,
    ISoftwareUpdater softwareUpdater,
    IServiceProvider serviceProvider
) : TemplatedControl
{
    /// <summary>
    /// Gets the device-local state displayed by the update control.
    /// </summary>
    public PersistentState PersistentState { get; } = persistentState;

    public ISoftwareUpdater SoftwareUpdater { get; } = softwareUpdater;

    public static readonly StyledProperty<IDynamicLocaleKey?> UpdateOrCheckTitleProperty = AvaloniaProperty.Register<SoftwareUpdateControl, IDynamicLocaleKey?>(
        nameof(UpdateOrCheckTitle));

    public IDynamicLocaleKey? UpdateOrCheckTitle
    {
        get => GetValue(UpdateOrCheckTitleProperty);
        set => SetValue(UpdateOrCheckTitleProperty, value);
    }

    [RelayCommand]
    private async Task UpdateOrCheckAsync(CancellationToken cancellationToken)
    {
        UpdateOrCheckTitle = new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_CheckingUpdateTitle_Text);
        if (SoftwareUpdater.DisplayUpdate is not null)
        {
            serviceProvider.GetRequiredService<MainViewModel>().NavigateTo(serviceProvider.GetRequiredService<ChangeLogView>());
            return;
        }

        try
        {
            await SoftwareUpdater.CheckForUpdatesAsync(true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ex = new HandledException(ex, new DynamicLocaleKey(LocaleKey.CommonSettings_SoftwareUpdate_Toast_CheckForUpdatesFailed_Content));
            ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage());
        }
    }
}