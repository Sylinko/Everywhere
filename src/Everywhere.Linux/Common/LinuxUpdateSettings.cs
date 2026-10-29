using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Configuration.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Linux.Common;

/// <summary>
/// Reads the application's update and network settings without starting observers or migrating user data.
/// </summary>
public sealed record LinuxUpdateSettings(UpdateChannel Channel, ProxySettings Proxy)
{
    /// <summary>
    /// Loads only the settings relevant to an explicit terminal update, preserving the application's defaults.
    /// </summary>
    public static LinuxUpdateSettings Load(string path, bool shouldReadChannel = true, bool shouldReadProxy = true)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var common = new CommonSettings(services);
        var proxy = new ProxySettings(services);
        if (File.Exists(path))
        {
            var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject ?? throw new InvalidDataException("Expected a settings object.");
            var binder = new SettingsPatchBinder();
            if (shouldReadChannel && root["Common"] is not null and not JsonObject ||
                shouldReadProxy && root["Proxy"] is not null and not JsonObject)
                throw new InvalidDataException("Invalid update or proxy settings section.");
            if (shouldReadChannel && root["Common"] is JsonObject commonObject && commonObject.TryGetPropertyValue("UpdateChannel", out var channel))
                binder.Patch(new JsonObject { ["UpdateChannel"] = channel?.DeepClone() }, common);
            if (shouldReadProxy && root["Proxy"] is JsonObject proxyObject)
            {
                binder.Patch(proxyObject, proxy);
            }
            if (binder.Diagnostics.Any(diagnostic => diagnostic.Kind != SettingsEngineDiagnosticKind.UnknownMember && diagnostic.Severity >= SettingsEngineDiagnosticSeverity.Warning) ||
                !Enum.IsDefined(common.UpdateChannel))
                throw new InvalidDataException("Invalid saved update or proxy settings. Correct them in Everywhere before retrying.");
        }
        return new LinuxUpdateSettings(SoftwareUpdater.ResolveUpdateChannel(common.UpdateChannel), proxy);
    }
}