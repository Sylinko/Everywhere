using System.Runtime.Versioning;
using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

[SupportedOSPlatform("windows")]
public partial class RestartAsAdministratorControl(INativeHelper nativeHelper, ILogger<RestartAsAdministratorControl> logger) : TemplatedControl
{
    [RelayCommand]
    private void RestartAsAdministrator()
    {
        try
        {
            nativeHelper.RestartAsAdministrator();
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex); // maybe blocked by UAC or antivirus, handle it gracefully
            logger.LogInformation(ex, "Failed to restart as administrator.");
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }
}