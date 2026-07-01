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
    Default = 0x0,

    [JsonStringEnumMemberName("title-generation")]
    TitleGeneration = 0x1,
    [JsonStringEnumMemberName("context-compression")]
    ContextCompression = 0x2,
    [JsonStringEnumMemberName("image-understanding")]
    ImageUnderstanding = 0x4,
}