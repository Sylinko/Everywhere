using System.Collections.Concurrent;
using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Hosting.Lifecycle;
using Everywhere.Plugin.Hosting.Loading;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting;

/// <summary>
/// Coordinates validated plugin changes, cooperative lifetimes and collection verification.
/// Use ApplyAsync with an empty set for hot unload, and DisposeAsync only when the host process is exiting.
/// </summary>
public sealed class PluginEngine : IAsyncDisposable
{
    /// <summary>
    /// Gets whether an uncertain lifetime has blocked further changes until process restart.
    /// </summary>
    public bool IsRestartRequired { get; private set; }

    /// <summary>
    /// Gets host-owned snapshots without extending collectible lifetimes.
    /// </summary>
    public IReadOnlyList<PluginInfo> Plugins => _snapshots.Values.OrderBy(info => info.Id, StringComparer.Ordinal).ToArray();

    private readonly HostAssemblyCatalog _host;
    private readonly IServiceProvider _services;
    private readonly PluginEngineOptions _options;
    private readonly AssemblyRegistry _registry;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly ConcurrentDictionary<string, PluginInfo> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginActivation> _activations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unconfirmedOwners = new(StringComparer.OrdinalIgnoreCase);
    private PluginLoadPlan? _plan;

    /// <summary>
    /// Serializes creation of the single disposal task; callbacks run outside this lock.
    /// </summary>
    private readonly Lock _disposeGate = new();

    /// <summary>
    /// Shares completion across repeated disposal calls, including calls through DI service aliases.
    /// </summary>
    private Task? _disposeTask;

    /// <summary>
    /// Rejects new changes once normal process-exit disposal starts.
    /// </summary>
    private volatile bool _isDisposing;

    /// <summary>
    /// Creates the runtime using the real deployment catalog, application services and lifecycle policy.
    /// </summary>
    public PluginEngine(HostAssemblyCatalog host, IServiceProvider services, PluginEngineOptions options)
    {
        _host = host;
        _services = services;
        _options = options;
        _registry = new AssemblyRegistry(host);
    }

    /// <summary>
    /// Applies one desired set. Cancellation is honored while waiting and during preflight, but never abandons
    /// cleanup after mutation begins. Calls from plugin lifecycle callbacks are rejected before waiting.
    /// </summary>
    public async Task<PluginApplyResult> ApplyAsync(
        IReadOnlyList<PluginDefinition> desired,
        CancellationToken cancellationToken = default)
    {
        if (PluginActivation.IsInCallback)
        {
            throw new InvalidOperationException("Plugin lifecycle callbacks cannot recursively apply plugin changes.");
        }

        ObjectDisposedException.ThrowIf(_isDisposing, this);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposing, this);
            if (IsRestartRequired) return Result(PluginApplyStatus.RestartRequired, []);

            PluginLoadPlan next;

            try
            {
                next = new PluginLoadPlan(desired, _host);
            }
            catch (Exception exception) when (exception is PluginValidationException or IOException or BadImageFormatException)
            {
                var code = exception is PluginValidationException validation ? validation.Code : "PreflightFailed";
                return Result(PluginApplyStatus.Rejected, [Diagnostic(code, exception.ToString())]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await ApplyValidatedAsync(next).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Releases remaining plugin instances for process exit, without reversing registrations,
    /// unloading assemblies or checking collection. Await completion before ending host ownership.
    /// This is ordinary asynchronous disposal, not a bounded or UI-dependent shutdown phase.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (PluginActivation.IsInCallback)
        {
            throw new InvalidOperationException("Plugin lifecycle callbacks cannot recursively dispose the engine.");
        }

        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                _isDisposing = true;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// Waits for mutation before releasing consumers ahead of dependencies. A failed consumer leaves
    /// its dependencies intact, while independent plugins are still attempted. Only host-owned error
    /// text escapes this boundary; original plugin exceptions must not become permanent assembly roots.
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_plan is null) return;

            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<string>();

            foreach (var id in _plan.PluginOrder.Reverse())
            {
                if (!_activations.TryGetValue(id, out var activation)) continue;

                if (blocked.Contains(id))
                {
                    blocked.UnionWith(_plan.Dependencies[id]);
                    failures.Add($"{id}: disposal skipped because a consumer failed to dispose.");
                    continue;
                }

                var result = await activation.DisposeForExitAsync().ConfigureAwait(false);
                if (result.Error is null) continue;

                blocked.UnionWith(_plan.Dependencies[id]);
                failures.Add($"{id}: {result.Error}");
            }

            if (failures.Count != 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Performs the non-cancellable mutation phase while preserving unaffected activations and shared owners.
    /// </summary>
    private async Task<PluginApplyResult> ApplyValidatedAsync(PluginLoadPlan next)
    {
        var diagnostics = new List<PluginDiagnostic>();
        var affected = FindAffected(next);

        foreach (var (id, info) in _snapshots)
        {
            _snapshots[id] = info with { IsEnabled = next.Plugins.ContainsKey(id) };
        }

        foreach (var plugin in next.Plugins.Values)
        {
            if (!_activations.ContainsKey(plugin.Id)) SetState(plugin, PluginState.Inactive, true);
        }

        if (_plan is { } previous)
        {
            if (!await StopAsync(previous.PluginOrder.Reverse().Where(affected.Contains), diagnostics).ConfigureAwait(false))
            {
                return RequireRestart(diagnostics);
            }

            var retained = _activations.Keys.SelectMany(id => previous.PluginAssemblies[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var released = previous.AssemblyOrder.Reverse().Where(name => !retained.Contains(name)).ToArray();

            if (!await ReleaseAsync(released, diagnostics).ConfigureAwait(false)) return RequireRestart(diagnostics);
        }

        _plan = next;
        var added = new List<string>();
        var isActivationFailed = false;

        try
        {
            _registry.Prepare(next);

            foreach (var id in next.PluginOrder)
            {
                if (_activations.ContainsKey(id)) continue;

                var definition = next.Plugins[id];
                var entry = next.GetEntry(definition);
                var conditions = entry.TypeRequirements.GetValueOrDefault(definition.Entry.Type, []);

                if (conditions.Any(required => !next.Dependencies[id].Contains(required)))
                {
                    throw new PluginValidationException("UnavailableEntry", $"Entry '{definition.Entry.Type}' requires an unavailable plugin.");
                }

                var catalog = new PluginCatalog(definition.Entry.Dependencies.Select(d => d.Id), next.Dependencies[id], _snapshots);
                var context = new PluginContext(definition, _services, catalog);
                var activation = new PluginActivation(definition, context, _registry, entry.Identity.Name ?? "");
                _activations.Add(id, activation);
                added.Add(id);
                SetState(definition, PluginState.Initializing, true);
                var outcome = await activation.InitializeAsync(_options.CallbackTimeout).ConfigureAwait(false);

                if (!outcome.IsCompleted || outcome.Error is not null)
                {
                    SetState(definition, PluginState.Failed, true);
                    diagnostics.Add(Diagnostic("PluginInitializeFailed", $"{id}: {outcome.Error}"));

                    if (!outcome.IsCompleted) return RequireRestart(diagnostics);
                    isActivationFailed = true;
                    break;
                }

                SetState(definition, PluginState.Active, true);
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add(Diagnostic("PluginActivationFailed", exception.ToString()));
            isActivationFailed = true;
        }

        if (!isActivationFailed) return Result(PluginApplyStatus.Applied, diagnostics);
        if (!await StopAsync(added.AsEnumerable().Reverse(), diagnostics).ConfigureAwait(false)) return RequireRestart(diagnostics);

        var preserved = _activations.Keys.SelectMany(id => next.PluginAssemblies[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failedOwners = next.AssemblyOrder.Reverse().Where(name => !preserved.Contains(name)).ToArray();

        if (!await ReleaseAsync(failedOwners, diagnostics).ConfigureAwait(false)) return RequireRestart(diagnostics);
        return Result(PluginApplyStatus.ActivationFailed, diagnostics);
    }

    /// <summary>
    /// Includes changes to content and optional bindings, then expands through active logical consumers.
    /// </summary>
    private HashSet<string> FindAffected(PluginLoadPlan next)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        if (_plan is not { } previous) return affected;

        var changedAssemblies = previous.Assemblies.Keys.Where(name =>
            !next.Assemblies.TryGetValue(name, out var artifact) || !artifact.HasSameContent(previous.Assemblies[name]) ||
            !previous.AssemblyDependencies[name].SetEquals(next.AssemblyDependencies[name])).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hasAssemblyChanges = true;

        while (hasAssemblyChanges)
        {
            hasAssemblyChanges = false;

            foreach (var (name, dependencies) in previous.AssemblyDependencies)
            {
                if (dependencies.Overlaps(changedAssemblies) && changedAssemblies.Add(name)) hasAssemblyChanges = true;
            }
        }

        foreach (var (id, activation) in _activations)
        {
            if (!next.Plugins.TryGetValue(id, out var replacement) ||
                activation.Definition.Installation.InstallationId != replacement.Installation.InstallationId ||
                activation.Definition.Entry.Type != replacement.Entry.Type ||
                activation.Definition.Entry.Assembly != replacement.Entry.Assembly ||
                !previous.Dependencies[id].SetEquals(next.Dependencies[id]) ||
                previous.PluginAssemblies[id].Overlaps(changedAssemblies))
            {
                affected.Add(id);
            }
        }

        var hasChanges = true;

        while (hasChanges)
        {
            hasChanges = false;

            foreach (var id in _activations.Keys)
            {
                if (previous.Dependencies[id].Overlaps(affected) && affected.Add(id)) hasChanges = true;
            }
        }

        return affected;
    }

    /// <summary>
    /// Stops consumers first, retaining failed activations and all their owners when cleanup is uncertain.
    /// </summary>
    private async Task<bool> StopAsync(IEnumerable<string> ids, List<PluginDiagnostic> diagnostics)
    {
        foreach (var id in ids)
        {
            if (!_activations.TryGetValue(id, out var activation)) continue;

            var isEnabled = _snapshots.TryGetValue(id, out var info) && info.IsEnabled;
            var hasInitializationFailed = info?.State == PluginState.Failed;
            SetState(activation.Definition, PluginState.Stopping, isEnabled);
            var outcome = await activation.StopAsync(_options.CallbackTimeout).ConfigureAwait(false);

            if (!outcome.IsCompleted || outcome.Error is not null)
            {
                SetState(activation.Definition, PluginState.Failed, isEnabled);
                diagnostics.Add(Diagnostic("PluginRetirementFailed", $"{id}: {outcome.Error}"));
                return false;
            }

            _activations.Remove(id);
            SetState(activation.Definition, hasInitializationFailed ? PluginState.Failed : PluginState.Inactive, isEnabled);
        }

        return true;
    }

    /// <summary>
    /// Releases assembly resources before removing registry ownership, then verifies collection through weak references.
    /// </summary>
    private async Task<bool> ReleaseAsync(string[] names, List<PluginDiagnostic> diagnostics)
    {
        var cleanup = await _registry.CleanupAsync(names, _options.CallbackTimeout).ConfigureAwait(false);
        if (!cleanup.IsCompleted || cleanup.Error is not null)
        {
            diagnostics.Add(Diagnostic(cleanup.IsCompleted ? "AssemblyCleanupFailed" : "AssemblyCleanupTimedOut", cleanup.Error ?? ""));
            return false;
        }

        var references = _registry.Release(names);

        for (var attempt = 0; attempt < _options.CollectionAttempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (references.All(reference => !reference.IsAlive)) return true;
            await Task.Delay(_options.CollectionDelay).ConfigureAwait(false);
        }

        _unconfirmedOwners.UnionWith(names);
        diagnostics.Add(Diagnostic("CollectionUnconfirmed", $"Collection was not confirmed for released owners: {string.Join(", ", names)}."));
        return false;
    }

    /// <summary>
    /// Publishes a value-only observation without exposing the activation object.
    /// </summary>
    private void SetState(PluginDefinition definition, PluginState state, bool isEnabled)
    {
        _snapshots[definition.Id] = new PluginInfo
        {
            Id = definition.Id,
            Version = definition.Installation.Manifest.Version,
            IsEnabled = isEnabled,
            State = state
        };
    }

    /// <summary>
    /// Blocks future mutations after uncertain shutdown or collection.
    /// </summary>
    private PluginApplyResult RequireRestart(List<PluginDiagnostic> diagnostics)
    {
        IsRestartRequired = true;
        return Result(PluginApplyStatus.RestartRequired, diagnostics);
    }

    /// <summary>
    /// Captures only scalar state and diagnostic strings for the application boundary.
    /// </summary>
    private PluginApplyResult Result(PluginApplyStatus status, IEnumerable<PluginDiagnostic> diagnostics)
    {
        return new PluginApplyResult
        {
            Status = status,
            ActivePluginIds = _activations.Where(item => item.Value.IsActive).Select(item => item.Key).Order(StringComparer.Ordinal).ToArray(),
            ResidentAssemblyNames = _registry.Names.Concat(_unconfirmedOwners).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray(),
            Diagnostics = diagnostics.ToArray()
        };
    }

    /// <summary>
    /// Creates a diagnostic without retaining the original exception instance.
    /// </summary>
    private static PluginDiagnostic Diagnostic(string code, string detail) => new() { Code = code, Detail = detail };
}