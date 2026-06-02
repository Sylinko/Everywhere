using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.ViewModels;

public partial class AboutPageViewModel(IServiceProvider serviceProvider) : ReactiveViewModelBase
{
    public static string Version => RuntimeConstants.Version.ToString();

    [RelayCommand]
    private void OpenWelcomeDialog()
    {
        DialogManager
            .CreateCustomDialog(serviceProvider.GetRequiredService<WelcomeView>())
            .ShowAsync();
    }

    [RelayCommand]
    private void OpenChangeLogDialog()
    {
        serviceProvider.GetRequiredService<MainViewModel>().NavigateTo(serviceProvider.GetRequiredService<ChangeLogView>());
    }
}
