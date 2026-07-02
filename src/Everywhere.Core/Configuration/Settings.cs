using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Configuration;

/// <summary>
/// Represents the application settings.
/// A singleton that holds all the settings categories.
/// And automatically saves the settings to a JSON file when any setting is changed.
/// </summary>
[Serializable]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
public sealed partial class Settings(IServiceProvider serviceProvider) : SettingsBase(serviceProvider)
{
    [ObservableProperty]
    public partial string? Version { get; set; }

    #region Common

    public CommonSettings Common { get; } = new(serviceProvider);

    public DisplaySettings Display { get; } = new(serviceProvider);

    public ShortcutSettings Shortcut { get; } = new(serviceProvider);

    public ProxySettings Proxy { get; } = new(serviceProvider);

    #endregion

    public ModelSettings Model { get; } = new(serviceProvider);

    public SystemAssistantSettings SystemAssistant { get; } = new(serviceProvider);

    public ChatWindowSettings ChatWindow { get; } = new(serviceProvider);

    public PluginSettings Plugin { get; } = new(serviceProvider);
}

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Settings))]
public partial class SettingsJsonSerializerContext : JsonSerializerContext;