using Avalonia.Controls.Primitives;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Web;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

public partial class OpenWebBrowserControl(IWebBrowserHost webBrowserHost, ILogger<OpenWebBrowserControl> logger) : TemplatedControl
{
    [RelayCommand]
    private async Task OpenBrowserAsync()
    {
        try
        {
            await webBrowserHost.OpenBrowserAsync();
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Failed to open web browser");
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }
}