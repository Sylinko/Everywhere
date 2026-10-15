using System.Text.Json.Serialization;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Provides generated JSON metadata for package and host declarations.
/// Each reader supplies its own options to preserve the rules of its document format.
/// </summary>
/// <remarks>
/// Optional model properties with nonzero or collection defaults use setters. The .NET 10 generator
/// assigns default(T) to missing init-only properties, overwriting their property initializers.
/// Keep required members and nullable validation independent from these omission defaults.
/// See https://github.com/dotnet/runtime/issues/84484.
/// </remarks>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginHostDescriptor))]
[JsonSerializable(typeof(HostDependencyManifest))]
internal partial class PackagingJsonSerializerContext : JsonSerializerContext;