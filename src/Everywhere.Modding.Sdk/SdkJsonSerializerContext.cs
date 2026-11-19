using System.Text.Json.Serialization;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Sdk;

/// <summary>
/// Provides generated metadata for SDK import records and command results.
/// Host import records retain their default PascalCase format; result properties declare their command-line names.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ModHostDescriptor))]
[JsonSerializable(typeof(ManifestValidationResult))]
[JsonSerializable(typeof(PackageCreationResult))]
[JsonSerializable(typeof(KeyPairGenerationResult))]
[JsonSerializable(typeof(IndexCreationResult))]
[JsonSerializable(typeof(SdkErrorResult))]
[JsonSerializable(typeof(ModIndexBuildSpec))]
internal partial class SdkJsonSerializerContext : JsonSerializerContext;
