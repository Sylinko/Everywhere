using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Mods.Everything.I18N;

namespace Everywhere.Mods.Everything;

/// <summary>
/// Stores connection preferences. After initialization, edits and query snapshots belong to the UI thread.
/// Changing these values never interrupts an existing query or stops a user-owned Everything process.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class EverythingSettings : ObservableObject
{
    /// <summary>
    /// Gets or sets the executable used when the requested user instance needs to be started.
    /// An empty value enables installation discovery.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Everything_ExecutablePath_Header,
        LocaleKey.Everything_ExecutablePath_Description)]
    public partial string? ExecutablePath { get; set; }

    /// <summary>
    /// Gets or sets the exact IPC instance name. An empty value probes the default and 1.5a instances.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Everything_InstanceName_Header,
        LocaleKey.Everything_InstanceName_Description)]
    public partial string? InstanceName { get; set; }
}

/// <summary>
/// Provides explicit settings metadata owned by this mod activation.
/// </summary>
[GeneratedSettingsDescriptor(typeof(EverythingSettings))]
public partial class EverythingSettingsDescriptors;

/// <summary>
/// Keeps JSON metadata out of the host's global serializer configuration.
/// </summary>
[JsonSerializable(typeof(EverythingSettings))]
[JsonSerializable(typeof(string))]
internal partial class EverythingSettingsJsonSerializerContext : JsonSerializerContext;