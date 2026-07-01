using System.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI.Configurator;

[TypeConverter(typeof(FallbackEnumConverter))]
[DefaultValue(2)]
public enum AssistantConfiguratorType
{
    /// <summary>
    /// Advanced first for forward compatibility.
    /// </summary>
    Advanced = 0,
    PresetBased = 1,
    Official = 2,
}