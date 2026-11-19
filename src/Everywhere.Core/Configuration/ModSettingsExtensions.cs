using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using Everywhere.Modding.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Configuration;

/// <summary>
/// Opens mod-owned settings through the application's persistence and migration services.
/// </summary>
public static class ModSettingsExtensions
{
    /// <summary>
    /// Opens the activation's standard settings file, committing validated migrations before observation starts.
    /// Metadata, factories and migration registrations are supplied explicitly and are not added to host globals.
    /// </summary>
    /// <remarks>
    /// The mod entry owns the returned engine and must await its disposal after stopping model writers.
    /// Opening settings does not register automatic reversal or transfer ownership to the mod context.
    /// This application-level extension does not add members or dependencies to the neutral mod abstractions.
    /// </remarks>
    /// <typeparam name="TSettings">The observable settings model type.</typeparam>
    /// <param name="context">The activation supplying its data directory and borrowed logging service.</param>
    /// <param name="settings">The mod-owned model patched in place from the document.</param>
    /// <param name="version">The current settings format version, independent of the mod package version.</param>
    /// <param name="serializerOptions">Explicit metadata and converters for the model and serialized values.</param>
    /// <param name="descriptorProvider">The provider describing the model's patchable properties.</param>
    /// <param name="settingsFileName">The settings file name relative to the activation's data directory.</param>
    /// <param name="configureMigrations">Optional registrations owned by the temporary migration provider.</param>
    /// <param name="cancellationToken">Cancels opening before the settings document is committed.</param>
    /// <returns>The engine whose observation and persistence lifetime is owned by the caller.</returns>
    public static async Task<SettingsEngine<TSettings>> OpenSettingsAsync<TSettings>(
        this IModContext context,
        TSettings settings,
        SemanticVersion version,
        JsonSerializerOptions serializerOptions,
        ISettingsDescriptorProvider descriptorProvider,
        string settingsFileName = "settings.json",
        Action<IServiceCollection>? configureMigrations = null,
        CancellationToken cancellationToken = default)
        where TSettings : class, INotifyPropertyChanged
    {
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        var path = Path.Combine(context.DataDirectory, settingsFileName);
        var binder = new SettingsPatchBinder(descriptorProvider, serializerOptions);
        var descriptor = binder.GetDescriptor(typeof(TSettings));
        if (descriptor.Properties.AsValueEnumerable().Any(property => property.JsonName == SettingsEngineJson.VersionPropertyName))
        {
            throw new InvalidOperationException("Version is reserved for settings format metadata.");
        }

        await SettingsMigrator.InitializeAsync(
            path,
            version,
            CreateDefaults,
            Validate,
            configureMigrations,
            loggerFactory,
            cancellationToken).ConfigureAwait(false);
        var storage = JsonSettingsStorage.Load(path, loggerFactory.CreateLogger<JsonSettingsStorage>(), serializerOptions);
        try
        {
            return new SettingsEngine<TSettings>(settings, storage, binder);
        }
        catch
        {
            await storage.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        JsonObject CreateDefaults()
        {
            return JsonSerializer.SerializeToNode(settings, serializerOptions) as JsonObject ??
                throw new InvalidDataException("The settings model must serialize to a JSON object.");
        }

        void Validate(JsonObject root)
        {
            root.Remove(SettingsEngineJson.VersionPropertyName);
            binder.Patch(root, settings);
            var failures = binder.Diagnostics
                .AsValueEnumerable()
                .Where(diagnostic =>
                    diagnostic.Severity >= SettingsEngineDiagnosticSeverity.Error ||
                    diagnostic.Kind is
                        SettingsEngineDiagnosticKind.ScalarConversionFailure or
                        SettingsEngineDiagnosticKind.SerializedSubtreeFailure or
                        SettingsEngineDiagnosticKind.UnsupportedShape
                ).ToArray();
            if (failures.Length > 0)
            {
                throw new InvalidDataException(
                    "Invalid settings: " + string.Join(", ", failures.Select(diagnostic => $"{diagnostic.Kind} at '{diagnostic.Path}'")));
            }
        }
    }
}