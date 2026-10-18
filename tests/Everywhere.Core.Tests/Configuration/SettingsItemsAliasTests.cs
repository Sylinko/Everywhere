using Avalonia.Headless.NUnit;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.I18N;
using GeneratedKeys = Everywhere.Core.Tests.Generated.I18N.LocaleKey;
using KeySpace = Everywhere.Core.Tests.Generated.I18N;
using KeyAttribute = Everywhere.I18N.DynamicLocaleKeyAttribute;

namespace Everywhere.Core.Tests.Configuration;

/// <summary>
/// Exercises aliases to language keys produced in the same build by a separate generator.
/// </summary>
public sealed class SettingsItemsAliasTests
{
    [AvaloniaTest]
    public void SettingsItems_GeneratedKeysWithFileLocalAliases_PreservesAllKeys()
    {
        var settings = new AliasedSettingsFixture();
        var items = settings.SettingsItems.OfType<SettingsSelectionItem>().ToArray();

        Assert.That(items, Has.Length.EqualTo(2));
        foreach (var item in items)
        {
            Assert.That(item.HeaderKey is DynamicLocaleKey { Key: GeneratedKeys.SettingsAlias_Header }, Is.True);
            Assert.That(item.DescriptionKey is DynamicLocaleKey { Key: GeneratedKeys.SettingsAlias_Description }, Is.True);
            Assert.That(item.ItemsSource.Single().Key.Key, Is.EqualTo(GeneratedKeys.SettingsAlias_Option));
        }
    }
}

/// <summary>
/// Uses type, namespace and attribute aliases from this file, without global using directives.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class AliasedSettingsFixture : ObservableObject
{
    /// <summary>
    /// Gets or sets the selection shown with generated localization metadata.
    /// </summary>
    [ObservableProperty]
    [KeyAttribute(descriptionKey: KeySpace.LocaleKey.SettingsAlias_Description, headerKey: GeneratedKeys.SettingsAlias_Header)]
    public partial AliasedSettingsChoice Selection { get; set; }

    /// <summary>
    /// Uses a named header followed by a positional description in normal constructor order.
    /// </summary>
    [ObservableProperty]
    [KeyAttribute(headerKey: GeneratedKeys.SettingsAlias_Header, KeySpace.LocaleKey.SettingsAlias_Description)]
    public partial AliasedSettingsChoice MixedArgumentsSelection { get; set; }
}

/// <summary>
/// Supplies an enum option whose localization attribute has its own aliased expression.
/// </summary>
public enum AliasedSettingsChoice
{
    /// <summary>
    /// The available fixture option.
    /// </summary>
    [KeyAttribute(GeneratedKeys.SettingsAlias_Option)]
    First
}
