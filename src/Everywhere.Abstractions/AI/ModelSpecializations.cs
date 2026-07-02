using System.ComponentModel;
using System.Text.Json.Serialization;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// ModelSpecializations represents specific capabilities or optimizations that an AI model may have for certain tasks.
/// These specializations can be used to identify models that are particularly well-suited for specific use cases, such as generating titles or compressing context.
/// </summary>
[Flags]
[TypeConverter(typeof(FallbackEnumConverter))]
public enum ModelSpecializations : uint
{
    [JsonStringEnumMemberName("Default")]
    Default = 0x0,
    [JsonStringEnumMemberName("TitleGeneration")]
    TitleGeneration = 0x1,
    [JsonStringEnumMemberName("ContextCompression")]
    ContextCompression = 0x2,
    [JsonStringEnumMemberName("ImageUnderstanding")]
    ImageUnderstanding = 0x4,
}