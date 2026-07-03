using Everywhere.Common;
using ZLinq;

namespace Everywhere.AI.Prompts;

/// <summary>
/// Describes and resolves one family of prompt placeholders.
/// </summary>
/// <remarks>
/// Placeholder sources are intentionally lightweight rule objects. Runtime values and external
/// dependencies are passed through <see cref="PromptPlaceholderContext"/> so callers can compose
/// sources per feature without registering them in the application container.
/// </remarks>
public interface IPromptPlaceholderSource
{
    /// <summary>
    /// Placeholder definitions shown in editor reference lists.
    /// </summary>
    IReadOnlyList<PromptPlaceholderDefinition> Definitions { get; }

    /// <summary>
    /// Returns whether the placeholder name is known to this source for the supplied context.
    /// </summary>
    bool IsKnown(string name, PromptPlaceholderContext context);

    /// <summary>
    /// Resolves the placeholder value, returning false when this source does not own the name.
    /// </summary>
    bool TryResolve(string name, PromptPlaceholderContext context, out string? value);

    /// <summary>
    /// Adds source-specific diagnostics after common template checks have run.
    /// </summary>
    void CollectDiagnostics(PromptTemplateAnalysisContext context, ICollection<PromptDiagnostic> diagnostics);
}

/// <summary>
/// Describes a prompt placeholder that can be presented in editor help.
/// </summary>
/// <param name="Name">Placeholder name without surrounding braces.</param>
/// <param name="DescriptionKey">Localized description displayed next to the placeholder.</param>
public sealed record PromptPlaceholderDefinition(string Name, IDynamicLocaleKey DescriptionKey);

/// <summary>
/// Runtime data used by placeholder sources while rendering or analyzing a template.
/// </summary>
/// <remarks>
/// The context is deliberately explicit rather than service-backed. A caller that can render skills,
/// a working directory, or strategy variables passes exactly those values for this rendering pass.
/// </remarks>
public sealed record PromptPlaceholderContext(
    Func<string>? SkillsPromptResolver = null,
    Func<string>? WorkingDirectoryResolver = null,
    string? Argument = null,
    IReadOnlyDictionary<string, string>? Variables = null)
{
    /// <summary>
    /// Context used by read-only Prompt Manager previews when no chat-specific values exist.
    /// </summary>
    public static PromptPlaceholderContext Preview { get; } = new();
}

/// <summary>
/// Inputs available to placeholder sources when collecting diagnostics.
/// </summary>
public sealed record PromptTemplateAnalysisContext(
    string Template,
    IReadOnlyList<PromptPlaceholderToken> Placeholders,
    PromptPlaceholderContext PlaceholderContext);

/// <summary>
/// Combines placeholder sources using ordered first-match resolution.
/// </summary>
/// <remarks>
/// This is used by contextual features such as Strategy rendering, where local variables should win
/// before falling back to the shared system-prompt placeholders.
/// </remarks>
public sealed class CompositePromptPlaceholderSource(IReadOnlyList<IPromptPlaceholderSource> sources) : IPromptPlaceholderSource
{
    public IReadOnlyList<PromptPlaceholderDefinition> Definitions { get; } =
        sources.AsValueEnumerable()
            .SelectMany(static source => source.Definitions)
            .ToList();

    public bool IsKnown(string name, PromptPlaceholderContext context) =>
        sources.AsValueEnumerable().Any(source => source.IsKnown(name, context));

    public bool TryResolve(string name, PromptPlaceholderContext context, out string? value)
    {
        foreach (var source in sources)
        {
            if (source.TryResolve(name, context, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    public void CollectDiagnostics(PromptTemplateAnalysisContext context, ICollection<PromptDiagnostic> diagnostics)
    {
        foreach (var source in sources)
        {
            source.CollectDiagnostics(context, diagnostics);
        }
    }
}

/// <summary>
/// Placeholders available to normal system prompt templates.
/// </summary>
public sealed class SystemPromptPlaceholderSource : IPromptPlaceholderSource
{
    public const string DefaultSystemPromptName = "DefaultSystemPrompt";
    public const string SkillsPromptName = "SkillsPrompt";
    public const string DateName = "Date";
    public const string TimeName = "Time";
    public const string OSName = "OS";
    public const string SystemLanguageName = "SystemLanguage";
    public const string WorkingDirectoryName = "WorkingDirectory";

    private static readonly IReadOnlyDictionary<string, PlaceholderDescriptor> Descriptors =
        Enum.GetValues<SystemPromptPlaceholder>()
            .Select(static placeholder => CreateDescriptor(placeholder))
            .ToDictionary(static descriptor => descriptor.Name, StringComparer.Ordinal);

    public static SystemPromptPlaceholderSource Instance { get; } = new();

    public IReadOnlyList<PromptPlaceholderDefinition> Definitions { get; } =
        Descriptors.Values
            .AsValueEnumerable()
            .Select(static descriptor => new PromptPlaceholderDefinition(
                descriptor.Name,
                new DynamicLocaleKey(descriptor.DescriptionKey)))
            .ToList();

    public bool IsKnown(string name, PromptPlaceholderContext context) =>
        Descriptors.ContainsKey(name);

    public bool TryResolve(string name, PromptPlaceholderContext context, out string? value)
    {
        value = name switch
        {
            DefaultSystemPromptName => DefaultPrompts.DefaultSystemPrompt,
            SkillsPromptName => context.SkillsPromptResolver?.Invoke() ?? string.Empty,
            DateName => DateTime.Now.ToString("D"),
            TimeName => DateTime.Now.ToString("F"),
            OSName => Environment.OSVersion.ToString(),
            SystemLanguageName => LocaleManager.CurrentLocale.ToEnglishName(),
            WorkingDirectoryName => context.WorkingDirectoryResolver?.Invoke() ?? RuntimeConstants.WritableFolderPath,
            _ => null
        };

        return Descriptors.ContainsKey(name);
    }

    public void CollectDiagnostics(PromptTemplateAnalysisContext context, ICollection<PromptDiagnostic> diagnostics)
    {
        var hasDefaultSystemPrompt = Contains(context.Placeholders, DefaultSystemPromptName);
        var hasSkillsPrompt = Contains(context.Placeholders, SkillsPromptName);
        var hasDate = Contains(context.Placeholders, DateName);
        var timeToken = First(context.Placeholders, TimeName);
        var hasTime = timeToken is not null;

        if (!hasDate && !hasTime)
        {
            diagnostics.Add(
                new PromptDiagnostic(
                    PromptDiagnosticCode.MissingDate,
                    PromptDiagnosticSeverity.Info,
                    new DynamicLocaleKey(LocaleKey.PromptDiagnostic_MissingDate),
                    ActionId: "insert-date"));
        }

        if (!hasDate && timeToken is not null)
        {
            diagnostics.Add(
                new PromptDiagnostic(
                    PromptDiagnosticCode.TimeMayReduceCacheHitRate,
                    PromptDiagnosticSeverity.Warning,
                    new DynamicLocaleKey(LocaleKey.PromptDiagnostic_TimeMayReduceCacheHitRate),
                    timeToken.Value.Span,
                    "replace-time-with-date"));
        }

        if (hasDefaultSystemPrompt || hasSkillsPrompt)
        {
            return;
        }

        diagnostics.Add(
            new PromptDiagnostic(
                PromptDiagnosticCode.MissingSkillsPrompt,
                PromptDiagnosticSeverity.Warning,
                new DynamicLocaleKey(LocaleKey.PromptDiagnostic_MissingSkillsPrompt),
                ActionId: "insert-skills-prompt"));
    }

    private static bool Contains(IReadOnlyList<PromptPlaceholderToken> placeholders, string name) =>
        placeholders.AsValueEnumerable().Any(placeholder => StringComparer.Ordinal.Equals(placeholder.Name, name));

    private static PromptPlaceholderToken? First(IReadOnlyList<PromptPlaceholderToken> placeholders, string name)
    {
        foreach (var placeholder in placeholders)
        {
            if (StringComparer.Ordinal.Equals(placeholder.Name, name))
            {
                return placeholder;
            }
        }

        return null;
    }

    private static PlaceholderDescriptor CreateDescriptor(SystemPromptPlaceholder placeholder) =>
        placeholder switch
        {
            SystemPromptPlaceholder.DefaultSystemPrompt =>
                new PlaceholderDescriptor(DefaultSystemPromptName, LocaleKey.PromptEditor_Placeholder_DefaultSystemPrompt_Description),
            SystemPromptPlaceholder.SkillsPrompt =>
                new PlaceholderDescriptor(SkillsPromptName, LocaleKey.PromptEditor_Placeholder_SkillsPrompt_Description),
            SystemPromptPlaceholder.Date =>
                new PlaceholderDescriptor(DateName, LocaleKey.PromptEditor_Placeholder_Date_Description),
            SystemPromptPlaceholder.Time =>
                new PlaceholderDescriptor(TimeName, LocaleKey.PromptEditor_Placeholder_Time_Description),
            SystemPromptPlaceholder.OS =>
                new PlaceholderDescriptor(OSName, LocaleKey.PromptEditor_Placeholder_OS_Description),
            SystemPromptPlaceholder.SystemLanguage =>
                new PlaceholderDescriptor(SystemLanguageName, LocaleKey.PromptEditor_Placeholder_SystemLanguage_Description),
            SystemPromptPlaceholder.WorkingDirectory =>
                new PlaceholderDescriptor(WorkingDirectoryName, LocaleKey.PromptEditor_Placeholder_WorkingDirectory_Description),
            _ => throw new ArgumentOutOfRangeException(nameof(placeholder), placeholder, null)
        };

    private sealed record PlaceholderDescriptor(string Name, object DescriptionKey);

    private enum SystemPromptPlaceholder
    {
        DefaultSystemPrompt,
        SkillsPrompt,
        Date,
        Time,
        OS,
        SystemLanguage,
        WorkingDirectory
    }
}

/// <summary>
/// Placeholders available only while rendering Strategy user prompts.
/// </summary>
public sealed class StrategyPromptPlaceholderSource : IPromptPlaceholderSource
{
    public const string ArgumentName = "Argument";

    private static readonly IReadOnlyList<PromptPlaceholderDefinition> EmptyDefinitions = [];

    public static StrategyPromptPlaceholderSource Instance { get; } = new();

    public IReadOnlyList<PromptPlaceholderDefinition> Definitions => EmptyDefinitions;

    public bool IsKnown(string name, PromptPlaceholderContext context) =>
        StringComparer.Ordinal.Equals(name, ArgumentName) ||
        context.Variables?.ContainsKey(name) == true;

    public bool TryResolve(string name, PromptPlaceholderContext context, out string? value)
    {
        if (StringComparer.Ordinal.Equals(name, ArgumentName))
        {
            value = context.Argument ?? string.Empty;
            return true;
        }

        if (context.Variables?.TryGetValue(name, out value) == true)
        {
            return true;
        }

        value = null;
        return false;
    }

    public void CollectDiagnostics(PromptTemplateAnalysisContext context, ICollection<PromptDiagnostic> diagnostics) { }
}