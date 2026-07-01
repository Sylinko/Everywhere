using Avalonia.Controls.Primitives;
using Everywhere.Cloud;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Views;

public class OfficialWebSearchProviderSettingsControl(IServiceProvider serviceProvider, OfficialWebSearchEngineSettings settings) : TemplatedControl
{
    public ICloudClient CloudClient { get; } = serviceProvider.GetRequiredService<ICloudClient>();

    public SettingsItems SettingsItems { get; } = settings.SettingsItems;
}