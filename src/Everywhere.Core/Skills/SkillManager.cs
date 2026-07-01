using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls.Notifications;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Skills;

public sealed partial class SkillManager : ISkillManager, ISkillPromptProvider, IAsyncInitializer, IDisposable
{
    public IReadOnlyBindableList<SkillSourceGroup> SourceGroups { get; }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings + 1;

    private readonly PersistentState _persistentState;
    private readonly SkillSource _skillSource;
    private readonly ILogger<SkillManager> _logger;
    private readonly SourceList<SkillSourceGroup> _sourceGroupsSource = new();
    private readonly CompositeDisposable _disposables = new(1);
    private readonly CompositeDisposable _skillDescriptorSubscriptions = new();
    private readonly Lock _refreshLock = new();
    private Dictionary<string, SkillDescriptor> _skillsById = new(StringComparer.OrdinalIgnoreCase);

    public SkillManager(
        PersistentState persistentState,
        SkillSource skillSource,
        ILogger<SkillManager> logger)
    {
        _persistentState = persistentState;
        _skillSource = skillSource;
        _logger = logger;
        _skillSource.Changed += HandleSkillSourceChanged;

        SourceGroups = _sourceGroupsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_sourceGroupsSource);
    }

    public Task InitializeAsync()
    {
        Task.Run(RefreshCore).Detach(_logger.ToExceptionHandler());
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                RefreshCore();
            },
            cancellationToken);
    }

    public SkillResolutionResult ResolveSkillReference(string reference) =>
        SkillReferenceResolver.Resolve(reference, _skillsById.Values);

    public string GetPrompt()
    {
        var enabledSkills =
            _skillsById.Values
                .AsValueEnumerable()
                .Where(skill => skill is { IsEnabled: true, IsValid: true })
                .OrderBy(skill => skill.SourceRoot)
                .ThenBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        if (enabledSkills.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        // From vscode copilot prompt template, with modifications to fit our markdown-based skill format and instructions.
        builder.AppendLine("<skills>");
        builder.AppendLine("Here is a list of skills that contain domain specific knowledge on a variety of topics.");
        builder.AppendLine("Each skill comes with a description of the topic and a file path that contains the detailed instructions.");
        builder.AppendLine(
            "When a user asks you to perform a task that falls within the domain of a skill, use the 'read_file' tool to acquire the full instructions from the file URI.");
        foreach (var skill in enabledSkills)
        {
            builder.AppendLine("<skill>");
            builder.Append("<name>").Append(SecurityElement.Escape(skill.Name)).AppendLine("</name>");
            builder.Append("<description>").Append(SecurityElement.Escape(skill.Description ?? string.Empty)).AppendLine("</description>");
            builder.Append("<file>").Append(SecurityElement.Escape(skill.FilePath)).AppendLine("</file>");
            builder.AppendLine("</skill>");
        }
        builder.AppendLine("</skills>");

        return builder.TrimEnd().ToString();
    }

    private void RefreshCore()
    {
        lock (_refreshLock)
        {
            _skillDescriptorSubscriptions.Clear();
            var overrides = GetSkillEnabledOverrides();
            var groups = DiscoverSkillGroups(overrides);
            var skills = groups.SelectMany(group => group.Skills).ToList();
            var defaultEnabledById = skills.ToDictionary(
                skill => skill.Id,
                skill => SkillSource.IsDefaultEnabled(skill.SourceRoot),
                StringComparer.OrdinalIgnoreCase);
            var cleanedOverrides = overrides
                .Where(kvp => defaultEnabledById.TryGetValue(kvp.Key, out var isDefaultEnabled) && kvp.Value != isDefaultEnabled)
                .ToDictionary(StringComparer.OrdinalIgnoreCase);
            if (!DictionaryEquals(overrides, cleanedOverrides))
            {
                _persistentState.SkillEnabledOverrides = ToOrderedStateDictionary(cleanedOverrides);
            }

            _skillsById = skills.ToDictionary(skill => skill.Id, StringComparer.OrdinalIgnoreCase);
            _sourceGroupsSource.Reset(groups);
        }
    }

    private List<SkillSourceGroup> DiscoverSkillGroups(Dictionary<string, bool> skillEnabledOverrides)
    {
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return SkillSource.GetRoots()
            .AsValueEnumerable()
            .Select(root => DiscoverSkillGroup(root, seenIds, skillEnabledOverrides))
            .ToList();
    }

    private SkillSourceGroup DiscoverSkillGroup(
        SkillRoot root,
        HashSet<string> seenIds,
        Dictionary<string, bool> skillEnabledOverrides)
    {
        var skills = new BindableList<SkillDescriptor>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        foreach (var filePath in EnumerateSkillFiles(root.DirectoryPath, deadline))
        {
            var skill = CreateSkillDescriptor(root, filePath, seenIds, skillEnabledOverrides);
            if (skill is not null) skills.Add(skill);
        }

        return new SkillSourceGroup(root.Root, root.Name, root.DirectoryPath, skills);
    }

    private SkillDescriptor? CreateSkillDescriptor(
        SkillRoot root,
        string filePath,
        HashSet<string> seenIds,
        Dictionary<string, bool> skillEnabledOverrides)
    {
        var folderName = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty).Name;
        var id = $"{SkillSource.GetSourceId(root.Root)}.{NormalizeId(folderName)}";

        // Case-sensitive file systems may contain folders that differ only by casing.
        // They intentionally collapse to the same id; ignore later duplicates to keep ids stable.
        if (!seenIds.Add(id)) return null;

        var diagnostics = new BindableList<SkillDiagnostic>();
        SkillParseResult? parseResult = null;
        var markdownContent = string.Empty;
        try
        {
            markdownContent = File.ReadAllText(filePath);
            parseResult = SkillParser.Parse(filePath, folderName, markdownContent);
            foreach (var diagnostic in parseResult.Diagnostics)
            {
                diagnostics.Add(diagnostic);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read skill file {FilePath}", filePath);
            diagnostics.Add(new SkillDiagnostic("skill.read_failed", ex.GetFriendlyMessage(), NotificationType.Error));
        }

        var isValid = diagnostics.AsValueEnumerable().All(d => d.Type != NotificationType.Error);
        var isDefaultEnabled = SkillSource.IsDefaultEnabled(root.Root);
        var isEnabled = skillEnabledOverrides.GetValueOrDefault(id, isDefaultEnabled);
        var displayName = parseResult?.FrontmatterName ?? parseResult?.HeadingName ?? folderName;
        var description = parseResult?.FrontmatterDescription ?? parseResult?.FirstParagraph;
        var directoryName = parseResult?.DirectoryName ?? folderName;

        var skillDescriptor = new SkillDescriptor
        {
            Id = id,
            Name = displayName,
            Description = description,
            DirectoryName = directoryName,
            FilePath = Path.GetFullPath(filePath),
            MarkdownContent = markdownContent,
            MarkdownBody = parseResult?.MarkdownBody ?? string.Empty,
            Metadata = parseResult?.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SourceRoot = root.Root,
            SourceName = root.Name,
            SourceDirectoryPath = root.DirectoryPath,
            IsValid = isValid,
            IsEnabled = isValid && isEnabled,
            Diagnostics = diagnostics
        };
        skillDescriptor.WhenPropertyChanged(x => x.IsEnabled, false).Subscribe(x =>
        {
            var skill = x.Sender;
            var overrides = GetSkillEnabledOverrides();
            if (x.Value == SkillSource.IsDefaultEnabled(skill.SourceRoot)) overrides.Remove(skill.Id);
            else overrides[skill.Id] = x.Value;
            _persistentState.SkillEnabledOverrides = ToOrderedStateDictionary(overrides);
        }).DisposeWith(_skillDescriptorSubscriptions);

        return skillDescriptor;
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root, DateTimeOffset deadline)
    {
        if (!Directory.Exists(root)) yield break;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(
                root,
                "*",
                new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = true
                });
        }
        catch
        {
            yield break;
        }

        foreach (var directory in directories.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (DateTimeOffset.UtcNow >= deadline) yield break;

            var file = Path.Combine(directory, "SKILL.md");
            if (File.Exists(file)) yield return file;
        }
    }

    private static string NormalizeId(string value)
    {
        var normalized = IdInvalidCharacterRegex()
            .Replace(value.Trim().ToLowerInvariant(), "-")
            .Trim('-', '.', '_');
        return normalized.Length == 0 ? "skill" : normalized;
    }

    private Dictionary<string, bool> GetSkillEnabledOverrides()
    {
        var overrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (_persistentState.SkillEnabledOverrides is null) return overrides;

        foreach (var (id, isEnabled) in _persistentState.SkillEnabledOverrides)
        {
            overrides[id] = isEnabled;
        }

        return overrides;
    }

    private static Dictionary<string, bool>? ToOrderedStateDictionary(Dictionary<string, bool> overrides) =>
        overrides.Count == 0 ?
            null :
            overrides
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

    private static bool DictionaryEquals(Dictionary<string, bool> left, Dictionary<string, bool> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var rightValue) || value != rightValue) return false;
        }

        return true;
    }

    public void Dispose()
    {
        _skillSource.Changed -= HandleSkillSourceChanged;
        _sourceGroupsSource.Dispose();
        _disposables.Dispose();
        _skillDescriptorSubscriptions.Dispose();
    }

    private void HandleSkillSourceChanged(object? sender, SkillSourceChangedEventArgs args)
    {
        Task.Run(RefreshCore).Detach(_logger.ToExceptionHandler());
    }

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex IdInvalidCharacterRegex();
}