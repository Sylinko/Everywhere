using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Security;
using System.Text;
using Avalonia.Controls.Notifications;
using DynamicData;
using DynamicData.Binding;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Skills;

/// <summary>
/// Discovers, tracks, and resolves physical and virtual skills and their resources.
/// </summary>
public sealed class SkillManager : ISkillManager, ISkillPromptProvider, IAsyncInitializer, IDisposable
{
    public IReadOnlyBindableList<SkillSourceGroup> SourceGroups { get; }

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings + 2;

    private readonly PersistentState _persistentState;
    private readonly SkillSource _skillSource;
    private readonly IReadOnlyList<IVirtualSkillProvider> _virtualSkillProviders;
    private readonly ILogger<SkillManager> _logger;
    private readonly SourceList<SkillSourceGroup> _sourceGroupsSource = new();
    private readonly CompositeDisposable _disposables = new(1);
    private readonly CompositeDisposable _skillDescriptorSubscriptions = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private SkillCatalogSnapshot _snapshot = SkillCatalogSnapshot.Empty;

    public SkillManager(
        PersistentState persistentState,
        SkillSource skillSource,
        IEnumerable<IVirtualSkillProvider> providers,
        ILogger<SkillManager> logger)
    {
        _persistentState = persistentState;
        _skillSource = skillSource;
        _virtualSkillProviders = [.. providers];
        _logger = logger;
        _skillSource.Changed += HandleSkillSourceChanged;

        SourceGroups = _sourceGroupsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _disposables.Add(_sourceGroupsSource);
    }

    /// <inheritdoc />
    public Task InitializeAsync() => RefreshAsync();

    /// <inheritdoc />
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <inheritdoc />
    public SkillResolutionResult ResolveSkillReference(string reference)
    {
        var snapshot = CurrentSnapshot;
        return SkillReferenceResolver.Resolve(reference, snapshot.SkillsById);
    }

    /// <inheritdoc />
    public string GetPrompt()
    {
        var enabledSkills = CurrentSnapshot.SkillsById.Values
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
            builder.Append("<file>skill://").Append(SecurityElement.Escape(skill.Id)).AppendLine("/SKILL.md</file>");
            builder.AppendLine("</skill>");
        }
        builder.AppendLine("</skills>");

        return builder.TrimEnd().ToString();
    }

    /// <summary>
    /// Resolves a <c>skill://</c> URI whose host is a complete source-qualified skill ID.
    /// </summary>
    public SkillResource ResolveResource(string reference)
    {
        if (!TryParseResourceReference(reference, out var skill, out var relativePath, out var error))
        {
            throw new FileNotFoundException(error ?? $"Skill resource '{reference}' was not found.");
        }

        var snapshot = CurrentSnapshot;
        if (skill is null || !snapshot.ResourceStoresById.TryGetValue(skill.Id, out var resourceStore))
        {
            throw new FileNotFoundException($"Skill resource '{reference}' was not found.");
        }

        return resourceStore.Resolve(skill.Id, relativePath);
    }

    /// <summary>
    /// Enumerates the files and directories beneath a skill resource.
    /// </summary>
    public IEnumerable<SkillResource> EnumerateResources(string reference, bool recurseSubdirectories)
    {
        var root = ResolveResource(reference);
        if (!root.IsDirectory)
        {
            yield return root;
            yield break;
        }

        if (!TryParseResourceReference(root.Uri, out var skill, out _, out _)) yield break;

        var snapshot = CurrentSnapshot;
        if (skill is null || !snapshot.ResourceStoresById.TryGetValue(skill.Id, out var resourceStore)) yield break;

        foreach (var resource in resourceStore.Enumerate(skill.Id, root.RelativePath, recurseSubdirectories))
        {
            yield return resource;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        _skillDescriptorSubscriptions.Clear();

        var overrides = GetSkillEnabledOverrides();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allSkills = new List<SkillDescriptor>();
        var entriesById = new Dictionary<string, SkillEntry>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<SkillSourceGroup>();

        foreach (var root in _skillSource.Roots)
        {
            var skills = new BindableList<SkillDescriptor>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            foreach (var filePath in EnumerateSkillFiles(root.DirectoryPath, deadline))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = CreatePhysicalEntry(root, filePath, seenIds);
                if (entry is null) continue;

                var descriptor = CreateSkillDescriptor(entry, overrides);
                skills.Add(descriptor);
                allSkills.Add(descriptor);
                entriesById.Add(entry.Id, entry);
            }

            groups.Add(new SkillSourceGroup(root.Root, root.Name, root.DirectoryPath, skills));
        }

        await foreach (var virtualSkill in EnumerateVirtualSkillsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(virtualSkill.Id))
            {
                _logger.LogWarning("Skipping virtual skill provider item without an id.");
                continue;
            }

            if (!seenIds.Add(virtualSkill.Id))
            {
                _logger.LogWarning("Skipping virtual skill with duplicate id {SkillId}.", virtualSkill.Id);
                continue;
            }

            SkillEntry? entry;
            try
            {
                entry = CreateVirtualEntry(virtualSkill);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping malformed virtual skill.");
                continue;
            }

            if (entry is null)
            {
                _logger.LogWarning("Skipping virtual skill without a valid id.");
                continue;
            }

            var descriptor = CreateSkillDescriptor(entry, overrides);
            allSkills.Add(descriptor);
            entriesById.Add(entry.Id, entry);
        }

        var skillsById = allSkills.ToDictionary(skill => skill.Id, StringComparer.OrdinalIgnoreCase);
        var resourceStoresById = entriesById.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ResourceStore,
            StringComparer.OrdinalIgnoreCase);

        var cleanedOverrides = overrides
            .Where(kvp =>
                skillsById.TryGetValue(kvp.Key, out var skill) &&
                skill.SourceRoot != SkillSourceRoot.BuiltIn &&
                kvp.Value != SkillSource.IsDefaultEnabled(skill.SourceRoot))
            .ToDictionary(StringComparer.OrdinalIgnoreCase);
        if (!DictionaryEquals(overrides, cleanedOverrides))
        {
            _persistentState.SkillEnabledOverrides = ToOrderedStateDictionary(cleanedOverrides);
        }

        Interlocked.Exchange(
            ref _snapshot,
            new SkillCatalogSnapshot(skillsById, resourceStoresById));
        _sourceGroupsSource.Reset(groups);
    }

    private async IAsyncEnumerable<VirtualSkill> EnumerateVirtualSkillsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var provider in _virtualSkillProviders)
        {
            var skills = new List<VirtualSkill>();
            try
            {
                await foreach (var skill in provider.ListAsync(cancellationToken).ConfigureAwait(false))
                {
                    skills.Add(skill);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate virtual skills from {ProviderType}.", provider.GetType().FullName);
            }

            foreach (var skill in skills)
            {
                yield return skill;
            }
        }
    }

    private SkillEntry? CreatePhysicalEntry(SkillRoot root, string filePath, HashSet<string> seenIds)
    {
        var skillDirectory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(skillDirectory)) return null;

        var folderName = new DirectoryInfo(skillDirectory).Name;
        var id = SkillId.FromFolder(root.Root, folderName);

        // Case-sensitive file systems may contain folders that differ only by casing.
        // They intentionally collapse to the same id; ignore later duplicates to keep ids stable.
        if (!seenIds.Add(id)) return null;

        var markdownContent = string.Empty;
        Exception? readException = null;
        try
        {
            markdownContent = File.ReadAllText(filePath);
        }
        catch (Exception ex)
        {
            readException = ex;
            _logger.LogWarning(ex, "Failed to read skill file {FilePath}", filePath);
        }

        return new SkillEntry(
            id,
            folderName,
            Path.GetFullPath(filePath),
            root.DirectoryPath,
            root.Name,
            root.Root,
            markdownContent,
            new LocalSkillResourceStore(skillDirectory),
            readException);
    }

    private static SkillEntry? CreateVirtualEntry(VirtualSkill virtualSkill)
    {
        if (!SkillId.IsFull(virtualSkill.Id)) return null;

        var folderName = virtualSkill.Id.Split('.').LastOrDefault();
        if (string.IsNullOrWhiteSpace(folderName)) return null;

        return new SkillEntry(
            virtualSkill.Id,
            folderName,
            $"skill://{virtualSkill.Id}/SKILL.md",
            $"skill://{virtualSkill.Id}/",
            "Built-in",
            SkillSourceRoot.BuiltIn,
            virtualSkill.MarkdownContent,
            new VirtualSkillResourceStore(virtualSkill),
            null);
    }

    private SkillDescriptor CreateSkillDescriptor(SkillEntry entry, Dictionary<string, bool> skillEnabledOverrides)
    {
        var diagnostics = new BindableList<SkillDiagnostic>();
        SkillParseResult? parseResult = null;
        try
        {
            if (entry.ReadException is not null) throw entry.ReadException;
            parseResult = SkillParser.Parse(entry.FilePath, entry.FolderName, entry.MarkdownContent);
            diagnostics.AddRange(parseResult.Diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new SkillDiagnostic("skill.read_failed", ex.GetFriendlyMessage(), NotificationType.Error));
        }

        var isValid = diagnostics.AsValueEnumerable().All(diagnostic => diagnostic.Type != NotificationType.Error);
        var isDefaultEnabled = SkillSource.IsDefaultEnabled(entry.SourceRoot);
        var isEnabled = isValid &&
            (entry.SourceRoot == SkillSourceRoot.BuiltIn || skillEnabledOverrides.GetValueOrDefault(entry.Id, isDefaultEnabled));

        var descriptor = new SkillDescriptor
        {
            Id = entry.Id,
            Name = parseResult?.FrontmatterName ?? parseResult?.HeadingName ?? entry.FolderName,
            Description = parseResult?.FrontmatterDescription ?? parseResult?.FirstParagraph,
            FilePath = entry.FilePath,
            MarkdownContent = entry.MarkdownContent,
            MarkdownBody = parseResult?.MarkdownBody ?? string.Empty,
            Metadata = parseResult?.Metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            SourceRoot = entry.SourceRoot,
            SourceName = entry.SourceName,
            SourceDirectoryPath = entry.SourceDirectoryPath,
            IsValid = isValid,
            IsEnabled = isEnabled,
            Diagnostics = diagnostics
        };

        if (entry.SourceRoot != SkillSourceRoot.BuiltIn)
        {
            descriptor.WhenPropertyChanged(x => x.IsEnabled, false).Subscribe(x =>
            {
                var skill = x.Sender;
                var overrides = GetSkillEnabledOverrides();
                if (x.Value == SkillSource.IsDefaultEnabled(skill.SourceRoot)) overrides.Remove(skill.Id);
                else overrides[skill.Id] = x.Value;
                _persistentState.SkillEnabledOverrides = ToOrderedStateDictionary(overrides);
            }).DisposeWith(_skillDescriptorSubscriptions);
        }

        return descriptor;
    }

    private bool TryParseResourceReference(string reference, out SkillDescriptor? skill, out string relativePath, out string? error)
    {
        skill = null;
        relativePath = string.Empty;
        error = null;
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("skill", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Length == 0 ||
            uri.Port != -1 ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0)
        {
            error = $"'{reference}' is not a skill URI.";
            return false;
        }

        var snapshot = CurrentSnapshot;
        var skillReference = Uri.UnescapeDataString(uri.Host);
        if (!SkillId.IsFull(skillReference))
        {
            error = "Skill URI must use a complete skill ID, such as 'builtin.officecli'.";
            return false;
        }

        skill = snapshot.SkillsById.GetValueOrDefault(skillReference);
        if (skill is null)
        {
            error = $"Skill '{skillReference}' was not found.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var decoded = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var value = Uri.UnescapeDataString(segment);
            if (value is "." or ".." || value.Contains('/') || value.Contains('\\') || Path.IsPathRooted(value))
            {
                error = "Skill resource path contains an invalid segment.";
                return false;
            }

            decoded.Add(value);
        }

        relativePath = string.Join('/', decoded);
        return true;
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

    private SkillCatalogSnapshot CurrentSnapshot => Volatile.Read(ref _snapshot);

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
        _refreshGate.Dispose();
    }

    private void HandleSkillSourceChanged(object? sender, SkillSourceChangedEventArgs args)
    {
        RefreshAsync().Detach(_logger.ToExceptionHandler());
    }

    /// <summary>
    /// Holds the immutable manager view produced by one refresh.
    /// </summary>
    private sealed record SkillCatalogSnapshot(
        IReadOnlyDictionary<string, SkillDescriptor> SkillsById,
        IReadOnlyDictionary<string, SkillResourceStore> ResourceStoresById
    )
    {
        public static SkillCatalogSnapshot Empty { get; } = new(
            new Dictionary<string, SkillDescriptor>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, SkillResourceStore>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Carries the parsed source data and resource store needed to build one descriptor.
    /// </summary>
    private sealed record SkillEntry(
        string Id,
        string FolderName,
        string FilePath,
        string SourceDirectoryPath,
        string SourceName,
        SkillSourceRoot SourceRoot,
        string MarkdownContent,
        SkillResourceStore ResourceStore,
        Exception? ReadException
    );
}
