using System.Collections.Immutable;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Everywhere.SourceGeneration;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using AnalysisContext = Everywhere.SourceGeneration.AnalysisContext;

namespace Everywhere.I18N.SourceGenerator;

/// <summary>
/// Generates localization constants and documentation from the neutral resource file.
/// </summary>
[Generator]
public sealed class I18NSourceGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Only the first neutral resource defines the strongly typed key API.
        var resources = context.AdditionalTextsProvider
            .Where(static file => Path.GetFileName(file.Path).Equals("Strings.resx", StringComparison.OrdinalIgnoreCase))
            .Collect()
            .Select(static (files, _) => files.FirstOrDefault())
            .Select(static (file, token) => ReadResource(file, token));

        context.RegisterSourceOutput(
            resources,
            static (production, result) =>
            {
                foreach (var diagnostic in result.Diagnostics)
                {
                    production.ReportDiagnostic(diagnostic);
                }
            });

        var models = resources.Select(static (result, _) => result.Model).WithTrackingName("I18NModel");
        var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => GetNamespace(provider));
        var sources = models.Combine(options)
            .Select(static (input, token) =>
                input.Left is { } model ? GenerateLocaleKeyClass(input.Right, model.FileName, model.Entries, token) : null)
            .WithTrackingName("I18NSource");

        context.RegisterSourceOutput(
            sources,
            static (production, source) =>
            {
                if (source is not null)
                {
                    production.AddSource("LocaleKey.g.cs", SourceText.From(source, Encoding.UTF8));
                }
            });
    }

    /// <summary>
    /// Projects build options to the only value that affects generated source.
    /// </summary>
    private static string GetNamespace(AnalyzerConfigOptionsProvider provider)
    {
        var globalOptions = provider.GlobalOptions;
        globalOptions.TryGetValue("build_property.EverywhereI18NNamespace", out var configuredNamespace);
        globalOptions.TryGetValue("build_property.AssemblyName", out var assemblyName);
        globalOptions.TryGetValue("build_property.RootNamespace", out var rootNamespace);

        var baseNamespace =
            !string.IsNullOrWhiteSpace(configuredNamespace) ? configuredNamespace! :
            !string.IsNullOrWhiteSpace(assemblyName) ? assemblyName! + ".I18N" :
            !string.IsNullOrWhiteSpace(rootNamespace) ? rootNamespace! + ".I18N" :
            "Everywhere.I18N.Generated";

        return baseNamespace;
    }

    /// <summary>
    /// Reads resource values before namespace options are combined, so option changes do not reparse XML.
    /// </summary>
    private static AnalysisResult<ResourceModel> ReadResource(AdditionalText? file, CancellationToken token)
    {
        var analysis = new AnalysisContext(token);
        if (file is null)
        {
            return analysis.Complete<ResourceModel>(null);
        }

        try
        {
            var content = file.GetText(token)?.ToString();
            if (content is not { Length: > 0 })
            {
                return analysis.Complete<ResourceModel>(null);
            }

            var entries = ParseResxEntries(content, token);
            return analysis.Complete(
                entries.Count == 0 ?
                    null :
                    new ResourceModel(
                        Path.GetFileName(file.Path),
                        new EquatableArray<KeyValuePair<string, string>>(entries.ToImmutableArray())));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            analysis.ReportDiagnostic(
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "I18N002",
                        "I18N Generation Error",
                        $"Error generating I18N code: {exception.Message}",
                        "I18N",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    Location.None));
            return analysis.Complete<ResourceModel>(null);
        }
    }

    private static Dictionary<string, string> ParseResxEntries(string resxContent, CancellationToken token)
    {
        var entries = new Dictionary<string, string>();

        try
        {
            token.ThrowIfCancellationRequested();
            var doc = XDocument.Parse(resxContent);
            var dataNodes = doc.Root?.Elements("data");
            if (dataNodes == null) return entries;

            foreach (var dataNode in dataNodes)
            {
                token.ThrowIfCancellationRequested();
                var nameAttr = dataNode.Attribute("name");
                var valueNode = dataNode.Element("value");

                if (nameAttr != null && valueNode != null)
                {
                    entries[nameAttr.Value] = valueNode.Value;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Silently fail and return an empty dictionary
            return new Dictionary<string, string>();
        }

        return entries;
    }

    private static string GenerateLocaleKeyClass(
        string ns,
        string resxPath,
        EquatableArray<KeyValuePair<string, string>> entries,
        CancellationToken token)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace {{ns}};

              /// <summary>
              ///     Provides strongly-typed keys for localized strings.
              /// </summary>
              public static partial class LocaleKey
              {
                  /// <summary>
                  /// An empty string constant for special use.
                  /// </summary>
                  public const string Empty = "";
              """);

        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            AppendSummary(sb, entry.Value);
            var escapedKey = EscapeVariableName(entry.Key);
            sb.AppendLine($"    public const string {escapedKey} = {ToCSharpString(entry.Key)};");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendSummary(StringBuilder sb, string value)
    {
        var escapedSummary = SecurityElement.Escape(value);
        if (escapedSummary is not null && escapedSummary.Contains('\n'))
        {
            sb.AppendLine("    /// <summary>");
            foreach (var summaryLine in escapedSummary.Split('\n'))
            {
                sb.AppendLine($"    /// {summaryLine}");
            }
            sb.AppendLine("    /// </summary>");
        }
        else
        {
            sb.AppendLine($"    /// <summary>{escapedSummary}</summary>");
        }
    }

    private static string EscapeVariableName(string s)
    {
        var escaped = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (escaped.Length > 0 && char.IsDigit(escaped[0]))
        {
            escaped = "_" + escaped;
        }
        return escaped;
    }

    private static string ToCSharpString(string value)
    {
        return "\"" + value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r\n", "\\n")
            .Replace("\r", "\\n")
            .Replace("\n", "\\n") + "\"";
    }

    /// <summary>
    /// Contains only resource values that affect generated constants and their documentation.
    /// </summary>
    private sealed class ResourceModel(string fileName, EquatableArray<KeyValuePair<string, string>> entries) : IEquatable<ResourceModel>
    {
        /// <summary>
        /// Gets the resource name used in the generated file header.
        /// </summary>
        public string FileName { get; } = fileName;

        /// <summary>
        /// Gets keys and values in their original output order.
        /// </summary>
        public EquatableArray<KeyValuePair<string, string>> Entries { get; } = entries;

        /// <inheritdoc />
        public bool Equals(ResourceModel? other) => other is not null && FileName == other.FileName && Entries.Equals(other.Entries);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is ResourceModel other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => unchecked(FileName.GetHashCode() * 31 + Entries.GetHashCode());
    }
}
