using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Skills;

/// <summary>
/// Manages the sources of skills by watching specified directories for changes and notifying subscribers when changes occur.
/// </summary>
public sealed class SkillSource : IDisposable
{
    /// <summary>
    /// Raised when changes are detected in the skill source directories, providing details about the changes.
    /// </summary>
    public event EventHandler<SkillSourceChangedEventArgs>? Changed;

#if WINDOWS
    private static StringComparer PathComparer => StringComparer.OrdinalIgnoreCase;
#else
    private static StringComparer PathComparer => StringComparer.Ordinal;
#endif

    private readonly ILogger<SkillSource> _logger;
    private readonly Func<IEnumerable<SkillRoot>> _rootProvider;
    private readonly Lock _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Dictionary<string, SkillRoot> _rootsByPath = new(PathComparer);
    private readonly HashSet<SkillSourceChange> _pendingChanges = [];
    private readonly DebounceExecutor<SkillSource, ThreadingTimerImpl> _changedDebounce;

    public SkillSource(ILogger<SkillSource> logger) : this(logger, GetRoots)
    {
    }

    /// <summary>
    /// For unit test
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="rootProvider"></param>
    internal SkillSource(ILogger<SkillSource> logger, Func<IEnumerable<SkillRoot>> rootProvider)
    {
        _logger = logger;
        _rootProvider = rootProvider;
        _changedDebounce = new DebounceExecutor<SkillSource, ThreadingTimerImpl>(
            () => this,
            static source => source.FlushChanges(),
            TimeSpan.FromSeconds(1));

        Task.Run(InitializeWatchers).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    public static IEnumerable<SkillRoot> GetRoots()
    {
        yield return new SkillRoot(
            SkillSourceRoot.Everywhere,
            "Everywhere",
            RuntimeConstants.EnsureConfigurationFolderPath("skills"));

        foreach (var root in GetOptionalRoots())
        {
            if (Directory.Exists(root.DirectoryPath))
            {
                yield return root;
            }
        }
    }

    /// <summary>
    /// Determines whether the given skill source kind is enabled by default.
    /// </summary>
    public static bool IsDefaultEnabled(SkillSourceRoot root) =>
        root is SkillSourceRoot.Everywhere or SkillSourceRoot.Agents;

    /// <summary>
    /// Gets a stable source ID for the given skill source kind.
    /// </summary>
    public static string GetSourceId(SkillSourceRoot root) =>
        root.ToString().ToLowerInvariant();

    private void InitializeWatchers()
    {
        lock (_lock)
        {
            if (_watchers.Count > 0)
            {
                throw new InvalidOperationException("Watchers have already been initialized.");
            }

            foreach (var root in _rootProvider().AsValueEnumerable().Where(r => IsDefaultEnabled(r.Root)))
            {
                var normalizedRoot = NormalizePath(root.DirectoryPath);
                if (!_rootsByPath.TryAdd(normalizedRoot, root) || !Directory.Exists(normalizedRoot)) continue;

                TryAddRootWatcher(root, normalizedRoot);
            }
        }
    }

    private void TryAddRootWatcher(SkillRoot root, string path)
    {
        try
        {
            var watcher = new FileSystemWatcher(path, "*")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            watcher.Created += (_, args) => HandleRootChange(root, args);
            watcher.Changed += (_, args) => HandleRootChange(root, args);
            watcher.Deleted += (_, args) => HandleRootChange(root, args);
            watcher.Renamed += (_, args) => HandleRootRename(root, args);
            watcher.Error += (_, args) => HandleWatcherError(root, args);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch skill source path {Path}", path);
        }
    }

    private void HandleRootChange(SkillRoot root, FileSystemEventArgs args)
    {
        if (TryCreateChange(root, args.FullPath, out var change)) QueueChange(change);
    }

    private void HandleRootRename(SkillRoot root, RenamedEventArgs args)
    {
        if (TryCreateChange(root, args.OldFullPath, out var oldChange)) QueueChange(oldChange);
        if (TryCreateChange(root, args.FullPath, out var newChange)) QueueChange(newChange);
    }

    private void HandleWatcherError(SkillRoot root, ErrorEventArgs args)
    {
        _logger.LogWarning(args.GetException(), "Skill source watcher failed");
        QueueChange(new SkillSourceChange(root.DirectoryPath, null));
    }

    private void QueueChange(SkillSourceChange change)
    {
        lock (_lock)
        {
            _pendingChanges.Add(change);
        }

        _changedDebounce.Trigger();
    }

    private void FlushChanges()
    {
        List<SkillSourceChange> changes;
        lock (_lock)
        {
            if (_pendingChanges.Count == 0) return;

            changes = [.. _pendingChanges];
            _pendingChanges.Clear();
        }

        Changed?.Invoke(this, new SkillSourceChangedEventArgs(changes));
    }

    private static bool TryCreateChange(SkillRoot root, string path, out SkillSourceChange change)
    {
        change = default;

        try
        {
            var normalizedPath = NormalizePath(path);
            var skillDirectoryPath = TryGetSkillDirectoryPath(root.DirectoryPath, normalizedPath);
            if (skillDirectoryPath is null) return false;

            var filePath = IsSkillFilePath(skillDirectoryPath, normalizedPath) ? normalizedPath : null;
            change = new SkillSourceChange(skillDirectoryPath, filePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetSkillDirectoryPath(string rootPath, string path)
    {
        if (IsDirectChild(rootPath, path))
        {
            // Deleted directory events no longer have a directory to inspect.
            // Treat non-existing direct children as possible skill folders, but ignore
            // direct child files that still exist under the root.
            return Directory.Exists(path) || !File.Exists(path) ? path : null;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is not null && IsSkillFilePath(parent, path) && IsDirectChild(rootPath, parent))
        {
            return parent;
        }

        return null;
    }

    private static bool IsSkillFilePath(string skillDirectoryPath, string path) =>
        PathComparer.Equals(Path.GetFileName(path), "SKILL.md") &&
        PathComparer.Equals(NormalizePath(Path.GetDirectoryName(path) ?? string.Empty), NormalizePath(skillDirectoryPath));

    private static bool IsDirectChild(string rootPath, string childPath)
    {
        var parent = Path.GetDirectoryName(NormalizePath(childPath));
        return parent is not null && PathComparer.Equals(NormalizePath(parent), NormalizePath(rootPath));
    }

    private static IEnumerable<SkillRoot> GetOptionalRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) yield break;

        yield return new SkillRoot(SkillSourceRoot.Agents, "Agents", Path.Combine(userProfile, ".agents", "skills"));
        yield return new SkillRoot(SkillSourceRoot.Claude, "Claude", Path.Combine(userProfile, ".claude", "skills"));
        yield return new SkillRoot(SkillSourceRoot.Codex, "Codex", Path.Combine(userProfile, ".codex", "skills"));
        yield return new SkillRoot(SkillSourceRoot.Copilot, "Copilot", Path.Combine(userProfile, ".copilot", "skills"));
        yield return new SkillRoot(SkillSourceRoot.Cursor, "Cursor", Path.Combine(userProfile, ".cursor", "skills"));
        yield return new SkillRoot(SkillSourceRoot.Gemini, "Gemini", Path.Combine(userProfile, ".gemini", "skills"));

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            yield return new SkillRoot(SkillSourceRoot.Codex, "Codex", Path.Combine(codexHome, "skills"));
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private void DisposeWatchers()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeWatchers();
            _pendingChanges.Clear();
        }

        _changedDebounce.Dispose();
    }
}

public sealed class SkillSourceChangedEventArgs(IReadOnlyList<SkillSourceChange> changes) : EventArgs
{
    public IReadOnlyList<SkillSourceChange> Changes { get; } = changes;
}

public readonly record struct SkillSourceChange(string? SkillDirectoryPath, string? SkillFilePath);

public readonly record struct SkillRoot(SkillSourceRoot Root, string Name, string DirectoryPath);
