using Avalonia.Threading;
using Everywhere.I18N;
using Everywhere.Modding.Abstractions;
using Everywhere.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Mods.Sample;

/// <summary>
/// Demonstrates mod-owned compiled XAML, localized resources and reversible navigation registration.
/// </summary>
public sealed class NavigationMod : IMod
{
    /// <inheritdoc />
    public async ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        context.Register(
            LocaleManager.RegisterProvider(
                typeof(NavigationMod).Assembly,
                static () => new ResourceManagerLocaleResourceProvider("Everywhere.Mods.Sample.I18N.Strings", typeof(NavigationMod).Assembly)));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var navigation = context.Services.GetRequiredService<MainViewModel>();
            context.Register(navigation.RegisterNavigationItem(new SamplePage()));
        });
    }
}