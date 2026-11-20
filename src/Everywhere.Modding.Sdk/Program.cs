using System.Text.Json;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Sdk;

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
                    return await ExportHostAsync(host, output, packageFolders: null).ConfigureAwait(false);
                }
                case ["import", var host, var output, var packageFolders]:
                {
                    return await ExportHostAsync(host, output, packageFolders).ConfigureAwait(false);
                }
                case ["check", var packageDirectory]:
                {
                    var manifest = ModManifestFile.Read(packageDirectory);
                    var result = new ManifestValidationResult { Mod = manifest.Id, Version = manifest.Version };
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.ManifestValidationResult));
                    return 0;
                }
                case ["pack", var packDirectory]:
                {
                    var archivePath = ModPackager.Pack(packDirectory);
                    var result = new PackageCreationResult { Path = archivePath };
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.PackageCreationResult));
                    return 0;
                }
                case ["keygen", var privateKeyPath, var publicKeyPath]:
                {
                    var result = ModIndexBuilder.GenerateKeyPair(privateKeyPath, publicKeyPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.KeyPairGenerationResult));
                    return 0;
                }
                case ["build-index", var specPath, var outputIndexPath]:
                {
                    var result = ModIndexBuilder.Build(specPath, outputIndexPath, privateKeyPath: null);
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.IndexCreationResult));
                    return 0;
                }
                case ["build-index", var specPath, var outputIndexPath, var privateKeyPath]:
                {
                    var result = ModIndexBuilder.Build(specPath, outputIndexPath, privateKeyPath);
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.IndexCreationResult));
                    return 0;
                }
                default:
                {
                    await Console.Error.WriteLineAsync(
                        "Usage: Everywhere.Modding.Sdk import <host-directory> <reference-list> [package-folders] | check <package-directory> | pack <pack-directory> | keygen <private-key-pem> <public-key> | build-index <spec-json> <output-index-json> [private-key-pem]");
                    return 2;
                }
            }
        }
        catch (Exception exception)
        {
            var code = exception is ModValidationException validation ? validation.Code : "SdkOperationFailed";
            var result = new SdkErrorResult { Code = code, Message = exception.Message };
            await Console.Error.WriteLineAsync(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.SdkErrorResult));
            return 1;
        }
    }

    private static async Task<int> ExportHostAsync(string host, string output, string? packageFolders)
    {
        var probeDirectories = string.IsNullOrWhiteSpace(packageFolders)
            ? null
            : packageFolders.Split([';', Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var references = new HostReferenceSet(host, probeDirectories);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
        await File.WriteAllLinesAsync(output, references.Assemblies.Values.Order(StringComparer.Ordinal));
        await File.WriteAllLinesAsync(
            output + ".runtime.txt",
            references.RuntimeAssemblies.Values.Concat(references.ResourceAssemblies).Order(StringComparer.Ordinal));
        await File.WriteAllTextAsync(
            output + ".host.json",
            JsonSerializer.Serialize(references.Descriptor, SdkJsonSerializerContext.Default.ModHostDescriptor));
        return 0;
    }
}
