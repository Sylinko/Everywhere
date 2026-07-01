using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;

namespace Everywhere.ViewModels;

public sealed partial class WebSearchEnginePageViewModel(Settings settings) : ReactiveViewModelBase
{
    public WebSearchEngineSettings WebSearchEngineSettings => settings.Plugin.WebSearchEngine;

    public IEnumerable<IWebSearchEngineProvider> WebSearchEngineProviders => settings.Plugin.WebSearchEngine.Providers.Values;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetDefaultCommand))]
    public partial IWebSearchEngineProvider? SelectedWebSearchEngineProvider { get; set; }

    public bool CanSetDefault =>
        SelectedWebSearchEngineProvider is not null &&
        SelectedWebSearchEngineProvider.Id != WebSearchEngineSettings.SelectedProviderId;

    [RelayCommand(CanExecute = nameof(CanSetDefault))]
    private void SetDefault()
    {
        if (SelectedWebSearchEngineProvider is null || !SelectedWebSearchEngineProvider.Validate())
        {
            return;
        }

        WebSearchEngineSettings.SelectedProviderId = SelectedWebSearchEngineProvider.Id;
        SetDefaultCommand.NotifyCanExecuteChanged();
    }
}