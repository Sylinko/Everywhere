using System.Diagnostics;
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
                    var directory = host.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? await ExportProjectAsync(host) : host;
                    var references = new HostReferenceSet(directory);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)) ?? ".");
                    await File.WriteAllLinesAsync(output, references.Assemblies.Values.Order(StringComparer.Ordinal));
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
                    var framework, var hostRange, var apiRange, var platform, var archive
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
                        archive);
                    var result = new PackageCreationResult { Path = Path.GetFullPath(archive) };
                    Console.WriteLine(JsonSerializer.Serialize(result, SdkJsonSerializerContext.Default.PackageCreationResult));
                    return 0;
                }
                default:
                {
                    await Console.Error.WriteLineAsync(
                        "Usage: Everywhere.Plugin.Sdk import <host-directory|host.csproj> <reference-list> | check <package-directory> | pack <assembly> <reference-list> <payload-list> <id> <version> <entry-type> <framework> <host-range> <api-range> <platform|universal> <archive>");
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

    /// <summary>
    /// Invokes the plugin-enabled host's explicit export target, keeping plugin build properties out of the host build.
    /// </summary>
    private static async Task<string> ExportProjectAsync(string project)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
                 {
                     "msbuild", Path.GetFullPath(project), "-restore", "-target:EverywhereExportPluginHost",
                     "-property:EverywherePluginReferenceBuild=true", "-getTargetResult:EverywhereExportPluginHost",
                     "-nologo", "-verbosity:quiet", "-maxcpucount:1", "-nodeReuse:false"
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the host reference build.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new PluginValidationException("HostExportFailed", output + Environment.NewLine + error);
        }

        using var document = JsonDocument.Parse(output);
        var items = document.RootElement.GetProperty("TargetResults").GetProperty("EverywhereExportPluginHost").GetProperty("Items");
        var descriptor = items[0].GetProperty("Identity").GetString()
            ?? throw new PluginValidationException("HostExportFailed", "The host export returned no descriptor.");
        return Path.GetDirectoryName(descriptor) ?? throw new PluginValidationException("HostExportFailed", "Invalid descriptor path.");
    }
}