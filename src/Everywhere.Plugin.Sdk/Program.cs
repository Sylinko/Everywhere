using System.Text.Json;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Sdk;

/// <summary>
/// Provides the noninteractive process boundary used by MSBuild and author tooling.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Dispatches commands and emits value-only structured errors with meaningful exit status.
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["import", var host, var output]:
                {
                    var references = new HostReferenceSet(host);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
                    await File.WriteAllLinesAsync(output, references.Assemblies.Values.Order(StringComparer.Ordinal));
                    await File.WriteAllLinesAsync(output + ".runtime.txt", references.RuntimeAssemblies.Values.Concat(references.ResourceAssemblies).Order(StringComparer.Ordinal));
                    await File.WriteAllTextAsync(
                        output + ".host.json",
                        JsonSerializer.Serialize(references.Descriptor, SdkJsonSerializerContext.Default.PluginHostDescriptor));
                    return 0;
                }
                case ["check", var packageDirectory]:
                {
                    var manifest = PluginManifestFile.Read(packageDirectory);
                    var result = new ManifestValidationResult { Plugin = manifest.Id, Version = manifest.Version };
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.ManifestValidationResult));
                    return 0;
                }
                case
                [
                    "pack", var assembly, var referencesPath, var payload, var id, var version, var type,
                    var framework, var hostRange, var apiRange, var platform, var archive, var presentation, var projectDirectory
                ]:
                {
                    PluginPackager.Pack(
                        assembly,
                        referencesPath,
                        payload,
                        id,
                        version,
                        type,
                        framework,
                        hostRange,
                        apiRange,
                        platform == "universal" ? "" : platform,
                        archive,
                        presentation,
                        projectDirectory);
                    var result = new PackageCreationResult { Path = Path.GetFullPath(archive) };
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.PackageCreationResult));
                    return 0;
                }
                default:
                {
                    await Console.Error.WriteLineAsync(
                        "Usage: Everywhere.Plugin.Sdk import <host-directory> <reference-list> | check <package-directory> | pack <assembly> <reference-list> <payload-list> <id> <version> <entry-type> <framework> <host-range> <api-range> <platform|universal> <archive> <presentation-directory> <project-directory>");
                    return 2;
                }
            }
        }
        catch (Exception exception)
        {
            var code = exception is PluginValidationException validation ? validation.Code : "SdkOperationFailed";
            var result = new SdkErrorResult { Code = code, Message = exception.Message };
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.SdkErrorResult));
            return 1;
        }
    }
}
