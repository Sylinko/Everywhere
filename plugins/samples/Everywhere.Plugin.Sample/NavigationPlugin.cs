using Avalonia.Threading;
using Everywhere.I18N;
using Everywhere.Plugin.Abstractions;
using Everywhere.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Plugin.Sample;

/// <summary>
/// Demonstrates plugin-owned compiled XAML, localized resources and reversible navigation registration.
/// </summary>
public sealed class NavigationPlugin : IPlugin
{
    /// <inheritdoc />
    public async ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Register(
            LocaleManager.RegisterProvider(
                typeof(NavigationPlugin).Assembly,
                static () => new ResourceManagerLocaleResourceProvider("Everywhere.Plugin.Sample.I18N.Strings", typeof(NavigationPlugin).Assembly)));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var navigation = context.Services.GetRequiredService<MainViewModel>();
            context.Register(navigation.RegisterNavigationItem(new SamplePage()));
        });
    }
}