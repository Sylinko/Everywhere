using Avalonia.Headless.NUnit;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.I18N;
using ChannelLocaleKey = Everywhere.Abstractions.I18N.LocaleKey;

namespace Everywhere.Core.Tests.Configuration;

/// <summary>
/// Verifies that moving the channel enum to a dependency-free assembly preserves generated option keys.
/// </summary>
public sealed class UpdateChannelSettingsTests
{
    [AvaloniaTest]
    public void SettingsItems_ExternalChannelEnum_PreservesLocalizedOptions()
    {
        var settings = new UpdateChannelSettingsFixture();
        var item = settings.SettingsItems.OfType<SettingsSelectionItem>().Single();

        Assert.That(((DynamicLocaleKey)item.HeaderKey).Key, Is.EqualTo(ChannelLocaleKey.UpdateChannel_Unknown));
        Assert.That(item.DescriptionKey is DynamicLocaleKey { Key: ChannelLocaleKey.UpdateChannel_Stable }, Is.True);
        Assert.That(item.ItemsSource.Select(option => (option.Value, option.Key.Key)), Is.EqualTo(new (object, object)[]
        {
            (UpdateChannel.Unknown, ChannelLocaleKey.UpdateChannel_Unknown),
            (UpdateChannel.Canary, ChannelLocaleKey.UpdateChannel_Canary),
            (UpdateChannel.Stable, ChannelLocaleKey.UpdateChannel_Stable)
        }));
    }
}

/// <summary>
/// Uses the same enum settings generation path as the application's common settings.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class UpdateChannelSettingsFixture : ObservableObject
{
    /// <summary>
    /// Gets or sets the selected feed.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        global::Everywhere.Abstractions.I18N.LocaleKey.UpdateChannel_Unknown,
        global::Everywhere.Abstractions.I18N.LocaleKey.UpdateChannel_Stable)]
    public partial UpdateChannel Channel { get; set; }
}
