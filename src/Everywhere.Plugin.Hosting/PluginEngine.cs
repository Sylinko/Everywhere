using System.Collections.Concurrent;
using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Hosting.Lifecycle;
using Everywhere.Plugin.Hosting.Loading;
using Everywhere.Plugin.Hosting.Services;
using Microsoft.Extensions.DependencyInjection;
using ZLinq;

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
    private readonly IPluginServiceBoundaryFactory _services;
    private readonly PluginEngineOptions _options;
    private readonly AssemblyRegistry _registry;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly ConcurrentDictionary<string, PluginInfo> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginActivation> _activations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unconfirmedOwners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Retains native ownership after unconfirmed collection. These handles remain resident until process exit.
    /// </summary>
    private readonly List<AssemblyRetirement> _unconfirmedRetirements = [];

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
    /// Build application services with HostServiceContainer so activation scope ownership is available.
    /// </summary>
    public PluginEngine(HostAssemblyCatalog host, IServiceProvider services, PluginEngineOptions options)
    {
        _host = host;
        _services = services.GetRequiredService<IPluginServiceBoundaryFactory>();
        _options = options;
        _registry = new AssemblyRegistry(host);
    }

    /// <summary>
    /// Applies one desired set in retirement and activation stages. Cancellation is honored before mutation, but never abandons
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

            var selection = new PluginSelection(desired, _host);
            cancellationToken.ThrowIfCancellationRequested();
            return await ApplyChangesAsync(selection).ConfigureAwait(false);
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
    /// Finishes the predetermined retirement scope before validating or loading new activations.
    /// Explicit removals take effect even if the desired replacement graph is invalid. Survivors
    /// constrain the new graph; a new conflict never starts another shutdown pass.
    /// </summary>
    private async Task<PluginApplyResult> ApplyChangesAsync(PluginSelection selection)
    {
        var diagnostics = new List<PluginDiagnostic>();
        foreach (var (id, info) in _snapshots)
        {
            _snapshots[id] = info with
            {
                IsEnabled = selection.Requested.ContainsKey(id),
                State = info.State == PluginState.Skipped ? PluginState.Inactive : info.State,
                SkipReason = null
            };
        }

        if (_plan is { } previous)
        {
            var retirement = new PluginRetirementPlan(selection, previous, _activations.Keys);
            if (!await StopAsync(retirement.PluginOrder, diagnostics).ConfigureAwait(false) ||
                !await ReleaseAsync(retirement.AssemblyOrder, diagnostics).ConfigureAwait(false))
            {
                return RequireRestart(diagnostics);
            }
        }

        var next = new PluginLoadPlan(selection, _host, _plan, _activations.Keys.ToHashSet(StringComparer.Ordinal));
        diagnostics.AddRange(next.Diagnostics);
        foreach (var plugin in next.Plugins.Values)
        {
            if (!_activations.ContainsKey(plugin.Id)) SetState(plugin, PluginState.Inactive, true);
        }

        foreach (var plugin in next.RequestedPlugins.Values.AsValueEnumerable().Where(plugin => plugin.SkipReason is not null))
        {
            if (!_activations.ContainsKey(plugin.Id)) SetState(plugin, PluginState.Skipped, true);
        }

        foreach (var (id, state) in next.Failures)
        {
            if (!_activations.ContainsKey(id)) SetState(next.RequestedPlugins[id], state, true);
        }

        _plan = next;
        var assemblyFailures = _registry.Prepare(next);
        foreach (var id in next.PluginOrder)
        {
            if (_activations.ContainsKey(id)) continue;
            var definition = next.Plugins[id];
            var unavailable = next.Dependencies[id]
                .AsValueEnumerable()
                .Where(dependency => !_activations.TryGetValue(dependency, out var activation) || !activation.IsActive)
                .ToArray();
            if (unavailable.Length != 0)
            {
                SetState(definition, PluginState.Blocked, true);
                diagnostics.Add(Diagnostic("DependencyFailed", $"Selected dependencies are not active: {string.Join(", ", unavailable)}.", id));
                continue;
            }

            var failedAssemblies = next.PluginAssemblies[id].Where(assemblyFailures.ContainsKey).ToArray();
            if (failedAssemblies.Length != 0)
            {
                SetState(definition, PluginState.Failed, true);
                diagnostics.Add(
                    Diagnostic(
                        "AssemblyPreparationFailed",
                        string.Join(
                            Environment.NewLine,
                            failedAssemblies.Select(name => name + ": " + assemblyFailures[name])),
                        id));
                continue;
            }

            var outcome = await InitializeAsync(next, definition).ConfigureAwait(false);
            if (outcome is { IsCompleted: true, Error: null })
            {
                SetState(definition, PluginState.Active, true);
                continue;
            }

            SetState(definition, PluginState.Failed, true);
            diagnostics.Add(Diagnostic("PluginInitializeFailed", outcome.Error ?? "Initialization timed out.", id));
            if (!outcome.IsCompleted || !await StopAsync([id], diagnostics).ConfigureAwait(false))
            {
                return RequireRestart(diagnostics);
            }
        }

        var retainedOwners = _activations.Keys.SelectMany(id => next.PluginAssemblies[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedOwners = next.AssemblyOrder.Reverse().Where(name => !retainedOwners.Contains(name)).ToArray();
        if (!await ReleaseAsync(unusedOwners, diagnostics).ConfigureAwait(false)) return RequireRestart(diagnostics);
        return Result(diagnostics.Count == 0 ? PluginApplyStatus.Applied : PluginApplyStatus.CompletedWithErrors, diagnostics);
    }

    /// <summary>
    /// Contains activation setup and exception conversion outside the cleanup/collection stack.
    /// The engine retains an activation before invoking any plugin code so partial initialization
    /// always follows the same disposal path as a successful instance.
    /// </summary>
    private async Task<PluginActivation.CallbackResult> InitializeAsync(PluginLoadPlan plan, PluginDefinition definition)
    {
        try
        {
            var entry = plan.GetEntry(definition);
            var catalog = new PluginCatalog(
                definition.Entry.Dependencies.Select(dependency => dependency.Id),
                plan.Dependencies[definition.Id],
                _snapshots);
            var entryName = entry.Identity.Name ?? string.Empty;
            var boundary = _services.CreateBoundary(_registry.GetContext(entryName));
            var context = new PluginContext(definition, boundary, catalog);
            var activation = new PluginActivation(definition, context, _registry, entryName, boundary);
            _activations.Add(definition.Id, activation);
            SetState(definition, PluginState.Initializing, true);
            return await activation.InitializeAsync(_options.CallbackTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new PluginActivation.CallbackResult(true, exception.ToString());
        }
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
                diagnostics.Add(Diagnostic("PluginRetirementFailed", outcome.Error ?? "Retirement timed out.", id));
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
        if (references.Length == 0) return true;

        // TODO: is this efficient enough? It may be better to use a single GC.Collect and then wait for a while, rather than doing multiple collections with delays.
        for (var attempt = 0; attempt < _options.CollectionAttempts; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            if (references.All(reference => !reference.Context.IsAlive))
            {
                foreach (var reference in references) reference.NativeLibraries.ReleaseAfterCollection();
                return true;
            }
            await Task.Delay(_options.CollectionDelay).ConfigureAwait(false);
        }

        _unconfirmedOwners.UnionWith(names);
        _unconfirmedRetirements.AddRange(references);
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
            State = state,
            SkipReason = state == PluginState.Skipped ? definition.SkipReason : null
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
            ActivePluginIds = _activations
                .AsValueEnumerable()
                .Where(item => item.Value.IsActive)
                .Select(item => item.Key).Order(StringComparer.Ordinal)
                .ToArray(),
            ResidentAssemblyNames = _registry.Names
                .AsValueEnumerable()
                .Concat(_unconfirmedOwners)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Diagnostics = [.. diagnostics]
        };
    }

    /// <summary>
    /// Creates a diagnostic without retaining the original exception instance.
    /// </summary>
    private static PluginDiagnostic Diagnostic(string code, string detail, string? pluginId = null)
    {
        return new PluginDiagnostic { PluginId = pluginId, Code = code, Detail = detail };
    }
}