using System.Collections.Immutable;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Everywhere.I18N.SourceGenerator;

[Generator]
public sealed class I18NSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the additional file provider for RESX files
        var resxFiles = context.AdditionalTextsProvider
            .Where(file =>
            {
                var fileName = Path.GetFileName(file.Path);
                return Path.GetExtension(file.Path).Equals(".resx", StringComparison.OrdinalIgnoreCase) &&
                    (fileName.Equals("Strings.resx", StringComparison.OrdinalIgnoreCase) ||
                        (fileName.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase) &&
                            fileName.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)));
            })
            .Collect();

        var options = context.AnalyzerConfigOptionsProvider.Select(static (provider, _) => BuildOptions(provider));

        context.RegisterSourceOutput(
            resxFiles.Combine(options),
            static (context, tuple) =>
                GenerateI18NCode(context, tuple.Left, tuple.Right));
    }

    private static GeneratorOptions BuildOptions(AnalyzerConfigOptionsProvider provider)
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

        return new GeneratorOptions(baseNamespace);
    }

    private static void GenerateI18NCode(SourceProductionContext context, ImmutableArray<AdditionalText> resxFiles, GeneratorOptions options)
    {
        if (resxFiles.Length == 0)
        {
            return;
        }

        try
        {
            // Group RESX files by base name and locale
            var defaultResxFile = resxFiles.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("Strings.resx", StringComparison.OrdinalIgnoreCase));
            if (defaultResxFile == null)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "I18N001",
                            "Missing Default RESX File",
                            "Could not find the default Strings.resx file",
                            "I18N",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true),
                        Location.None));
                return;
            }

            // Parse the default RESX to get all keys
            var defaultContent = defaultResxFile.GetText(context.CancellationToken)?.ToString();
            if (string.IsNullOrEmpty(defaultContent))
            {
                return;
            }

            // Parse default RESX for keys and values
            var defaultEntries = ParseResxEntries(defaultContent!);
            if (defaultEntries.Count == 0)
            {
                return;
            }

            var localeNamesMap = new Dictionary<string, string>
            {
                { "En", "default" },
            };

            var localeSources = new List<string>();
            foreach (var resxFile in resxFiles.OrderBy(f => Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase))
            {
                var content = resxFile.GetText(context.CancellationToken)?.ToString();
                if (content is not { Length: > 0 }) continue;

                if (Path.GetFileName(resxFile.Path).Equals("Strings.resx", StringComparison.OrdinalIgnoreCase))
                {
                    localeSources.Add(GenerateLocaleClass(options.Namespace, resxFile.Path, "default", ParseResxEntries(content)));
                    continue;
                }

                var fileName = Path.GetFileNameWithoutExtension(resxFile.Path);
                var localeName = fileName.Substring(fileName.IndexOf('.') + 1);
                var enumName = ToLocaleEnumName(localeName);
                localeNamesMap[enumName] = localeName;
                localeSources.Add(GenerateLocaleClass(options.Namespace, resxFile.Path, localeName.Replace('-', '_'), ParseResxEntries(content)));
            }

            context.AddSource(
                "LocaleKey.g.cs",
                SourceText.From(GenerateLocaleKeyClass(options.Namespace, defaultResxFile.Path, defaultEntries), Encoding.UTF8));
            context.AddSource(
                "LocaleResolver.g.cs",
                SourceText.From(GenerateLocaleResolverClass(options.Namespace, defaultResxFile.Path, defaultEntries), Encoding.UTF8));

            foreach (var localeSource in localeSources)
            {
                var hintName = localeSource.Split('\n').FirstOrDefault(l => l.StartsWith("// HintName:", StringComparison.Ordinal))?
                    .Substring("// HintName:".Length)
                    .Trim();
                context.AddSource(hintName ?? Guid.NewGuid().ToString("N") + ".g.cs", SourceText.From(localeSource, Encoding.UTF8));
            }

            context.AddSource("LocaleProvider.g.cs", SourceText.From(GenerateLocaleProvider(options.Namespace, localeNamesMap), Encoding.UTF8));
        }
        catch (Exception ex)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "I18N002",
                        "I18N Generation Error",
                        $"Error generating I18N code: {ex.Message}",
                        "I18N",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    Location.None));
        }
    }

    private static Dictionary<string, string> ParseResxEntries(string resxContent)
    {
        var entries = new Dictionary<string, string>();

        try
        {
            var doc = XDocument.Parse(resxContent);
            var dataNodes = doc.Root?.Elements("data");

            if (dataNodes == null) return entries;

            foreach (var dataNode in dataNodes)
            {
                var nameAttr = dataNode.Attribute("name");
                var valueNode = dataNode.Element("value");

                if (nameAttr != null && valueNode != null)
                {
                    entries[nameAttr.Value] = valueNode.Value;
                }
            }
        }
        catch
        {
            // Silently fail and return an empty dictionary
            return new Dictionary<string, string>();
        }

        return entries;
    }

    private static string GenerateLocaleKeyClass(string ns, string resxPath, Dictionary<string, string> entries)
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
            AppendSummary(sb, entry.Value);
            var escapedKey = EscapeVariableName(entry.Key);
            sb.AppendLine($"    public const string {escapedKey} = {ToCSharpString(entry.Key)};");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateLocaleResolverClass(string ns, string resxPath, Dictionary<string, string> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace {{ns}};

              /// <summary>
              ///     Provides strongly-typed access to localized strings.
              /// </summary>
              public static partial class LocaleResolver
              {
                  /// <summary>
                  /// An empty string constant for special use.
                  /// </summary>
                  public const string Empty = "";

                  [global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
                  [global::System.Diagnostics.DebuggerNonUserCode]
                  [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                  private static string Resolve(string key)
                  {
                      return global::Everywhere.I18N.DynamicResourceKey.Resolve(key);
                  }

              """);

        foreach (var entry in entries)
        {
            AppendSummary(sb, entry.Value);
            var escapedKey = EscapeVariableName(entry.Key);
            sb.AppendLine($"    public static string {escapedKey} => Resolve({ToCSharpString(entry.Key)});");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateLocaleClass(string ns, string resxPath, string escapedLocaleName, Dictionary<string, string> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// HintName: Locale_{escapedLocaleName}.g.cs");
        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace {{ns}};

              internal sealed class __{{escapedLocaleName}} : global::Avalonia.Controls.ResourceDictionary
              {
                  public __{{escapedLocaleName}}()
                  {
                      SetItems([
              """);

        foreach (var entry in entries)
        {
            sb.AppendLine(
                $"            new global::System.Collections.Generic.KeyValuePair<object, object?>({ToCSharpString(entry.Key)}, {ToCSharpString(entry.Value)}),");
        }

        sb.AppendLine(
            """
                    ]);
                }
            }
            """);

        return sb.ToString();
    }

    private static string GenerateLocaleProvider(string ns, Dictionary<string, string> localeNamesMap)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually

              #nullable enable

              namespace {{ns}};

              internal sealed class __I18NLocaleProvider : global::Everywhere.I18N.ILocaleResourceProvider
              {
                  public static readonly __I18NLocaleProvider Shared = new();

                  private static readonly global::System.Collections.Generic.Dictionary<global::Everywhere.I18N.LocaleName, global::Avalonia.Controls.ResourceDictionary> Locales = new()
                  {
              """);

        foreach (var kvp in localeNamesMap)
        {
            var escapedLocaleName = kvp.Value.Replace("-", "_");
            sb.AppendLine($"        {{ global::Everywhere.I18N.LocaleName.{kvp.Key}, new __{escapedLocaleName}() }},");
        }

        sb.AppendLine(
            """
                };

                public global::Avalonia.Controls.ResourceDictionary GetResources(global::Everywhere.I18N.LocaleName locale)
                {
                    if (Locales.TryGetValue(locale, out var resources)) return resources;
                    return Locales.TryGetValue(global::Everywhere.I18N.LocaleName.En, out resources) ? resources : global::System.Linq.Enumerable.First(Locales).Value;
                }
            }

            internal static class __I18NModuleInitializer
            {
                [global::System.Runtime.CompilerServices.ModuleInitializer]
                internal static void Register()
                {
                    global::Everywhere.I18N.LocaleManager.RegisterProvider(static () => __I18NLocaleProvider.Shared);
                }
            }
            """);

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

    private static string ToLocaleEnumName(string localeName)
    {
        return string.Join("", localeName.Split('-').Select(p => p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1)));
    }

    private sealed class GeneratorOptions
    {
        public GeneratorOptions(string ns)
        {
            Namespace = ns;
        }

        public string Namespace { get; }
    }
}
