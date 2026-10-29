using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
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

    /// <summary>
    /// Gets whether the current platform uses terminal-managed updates.
    /// </summary>
    public bool IsTerminalUpdate => SoftwareUpdater.TerminalUpdateHandler is not null;

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
            if (IsTerminalUpdate)
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard ?? throw new InvalidOperationException("Clipboard is unavailable.");
                    await clipboard.SetTextAsync("ew update");
                    ToastManager.Success(LocaleKey.Common_Copied.I18N(), LocaleKey.TerminalUpdate_PasteHint.I18N());
                }
                catch (Exception ex)
                {
                    ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage());
                }
                return;
            }
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