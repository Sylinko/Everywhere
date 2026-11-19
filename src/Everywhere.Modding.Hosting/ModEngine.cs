using System.Collections.Concurrent;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting.Lifecycle;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Microsoft.Extensions.DependencyInjection;
using ZLinq;

namespace Everywhere.Modding.Hosting;

/// <summary>
/// Coordinates validated mod changes, cooperative lifetimes and collection verification.
/// Use ApplyAsync with an empty set for hot unload, and DisposeAsync only when the host process is exiting.
/// </summary>
public sealed class ModEngine : IAsyncDisposable
{
    /// <summary>
    /// Gets whether an uncertain lifetime has blocked further changes until process restart.
    /// </summary>
    public bool IsRestartRequired { get; private set; }

    /// <summary>
    /// Gets host-owned snapshots without extending collectible lifetimes.
    /// </summary>
    public IReadOnlyList<ModInfo> Mods => _snapshots.Values.AsValueEnumerable().OrderBy(info => info.Id, StringComparer.Ordinal).ToArray();

    private readonly HostAssemblyCatalog _host;
    private readonly IModServiceBoundaryFactory _services;
    private readonly ModEngineOptions _options;
    private readonly AssemblyRegistry _registry;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly ConcurrentDictionary<string, ModInfo> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModActivation> _activations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unconfirmedOwners = new(StringComparer.OrdinalIgnoreCase);

    private ModLoadPlan? _plan;

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
    public ModEngine(HostAssemblyCatalog host, IServiceProvider services, ModEngineOptions options)
    {
        _host = host;
        _services = services.GetRequiredService<IModServiceBoundaryFactory>();
        _options = options;
        _registry = new AssemblyRegistry(host);
    }

    /// <summary>
    /// Applies one desired set in retirement and activation stages. Cancellation is honored before mutation, but never abandons
    /// cleanup after mutation begins. Calls from mod lifecycle callbacks are rejected before waiting.
    /// </summary>
    public async Task<ModApplyResult> ApplyAsync(
        IReadOnlyList<ModDefinition> desired,
        CancellationToken cancellationToken = default)
    {
        if (ModActivation.IsInCallback)
        {
            throw new InvalidOperationException("Mod lifecycle callbacks cannot recursively apply mod changes.");
        }

        ObjectDisposedException.ThrowIf(_isDisposing, this);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_isDisposing, this);
            if (IsRestartRequired) return Result(ModApplyStatus.RestartRequired, []);

            var selection = new ModSelection(desired, _host);
            cancellationToken.ThrowIfCancellationRequested();
            return await ApplyChangesAsync(selection).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Releases remaining mod instances for process exit, without reversing registrations,
    /// unloading assemblies or checking collection. Await completion before ending host ownership.
    /// This is ordinary asynchronous disposal, not a bounded or UI-dependent shutdown phase.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (ModActivation.IsInCallback)
        {
            throw new InvalidOperationException("Mod lifecycle callbacks cannot recursively dispose the engine.");
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
    /// its dependencies intact, while independent mods are still attempted. Only host-owned error
    /// text escapes this boundary; original mod exceptions must not become permanent assembly roots.
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_plan is null) return;

            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var failures = new List<string>();

            foreach (var id in _plan.ModOrder.Reverse())
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
    private async Task<ModApplyResult> ApplyChangesAsync(ModSelection selection)
    {
        var diagnostics = new List<ModDiagnostic>();
        foreach (var (id, info) in _snapshots)
        {
            _snapshots[id] = info with
            {
                IsEnabled = selection.Requested.ContainsKey(id),
                State = info.State == ModState.Skipped ? ModState.Inactive : info.State,
                SkipReason = null
            };
        }

        if (_plan is { } previous)
        {
            var retirement = new ModRetirementPlan(selection, previous, _activations.Keys);
            if (!await StopAsync(retirement.ModOrder, diagnostics).ConfigureAwait(false) ||
                !await ReleaseAsync(retirement.AssemblyOrder, diagnostics).ConfigureAwait(false))
            {
                return RequireRestart(diagnostics);
            }
        }

        var next = new ModLoadPlan(selection, _host, _plan, _activations.Keys.ToHashSet(StringComparer.Ordinal));
        diagnostics.AddRange(next.Diagnostics);
        foreach (var mod in next.Mods.Values)
        {
            if (!_activations.ContainsKey(mod.Id)) SetState(mod, ModState.Inactive, true);
        }

        foreach (var mod in next.RequestedMods.Values.AsValueEnumerable().Where(mod => mod.SkipReason is not null))
        {
            if (!_activations.ContainsKey(mod.Id)) SetState(mod, ModState.Skipped, true);
        }

        foreach (var (id, state) in next.Failures)
        {
            if (!_activations.ContainsKey(id)) SetState(next.RequestedMods[id], state, true);
        }

        _plan = next;
        var assemblyFailures = _registry.Prepare(next);
        foreach (var id in next.ModOrder)
        {
            if (_activations.ContainsKey(id)) continue;
            var definition = next.Mods[id];
            var unavailable = next.Dependencies[id]
                .AsValueEnumerable()
                .Where(dependency => !_activations.TryGetValue(dependency, out var activation) || !activation.IsActive)
                .ToArray();
            if (unavailable.Length != 0)
            {
                SetState(definition, ModState.Blocked, true);
                diagnostics.Add(Diagnostic("DependencyFailed", $"Selected dependencies are not active: {string.Join(", ", unavailable)}.", id));
                continue;
            }

            var failedAssemblies = next.ModAssemblies[id].Where(assemblyFailures.ContainsKey).ToArray();
            if (failedAssemblies.Length != 0)
            {
                SetState(definition, ModState.Failed, true);
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
                SetState(definition, ModState.Active, true);
                continue;
            }

            SetState(definition, ModState.Failed, true);
            diagnostics.Add(Diagnostic("ModInitializeFailed", outcome.Error ?? "Initialization timed out.", id));
            if (!outcome.IsCompleted || !await StopAsync([id], diagnostics).ConfigureAwait(false))
            {
                return RequireRestart(diagnostics);
            }
        }

        var retainedOwners = _activations.Keys.SelectMany(id => next.ModAssemblies[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unusedOwners = next.AssemblyOrder.Reverse().Where(name => !retainedOwners.Contains(name)).ToArray();
        if (!await ReleaseAsync(unusedOwners, diagnostics).ConfigureAwait(false)) return RequireRestart(diagnostics);
        return Result(diagnostics.Count == 0 ? ModApplyStatus.Applied : ModApplyStatus.CompletedWithErrors, diagnostics);
    }

    /// <summary>
    /// Contains activation setup and exception conversion outside the cleanup/collection stack.
    /// The engine retains an activation before invoking any mod code so partial initialization
    /// always follows the same disposal path as a successful instance.
    /// </summary>
    private async Task<ModActivation.CallbackResult> InitializeAsync(ModLoadPlan plan, ModDefinition definition)
    {
        try
        {
            var entry = plan.GetEntry(definition);
            var catalog = new ModCatalog(
                definition.Entry.Dependencies.Select(dependency => dependency.Id),
                plan.Dependencies[definition.Id],
                _snapshots);
            var entryName = entry.Identity.Name ?? string.Empty;
            var boundary = _services.CreateBoundary(_registry.GetContext(entryName));
            var context = new ModContext(definition, boundary, catalog);
            var activation = new ModActivation(definition, context, _registry, entryName, boundary);
            _activations.Add(definition.Id, activation);
            SetState(definition, ModState.Initializing, true);
            return await activation.InitializeAsync(_options.CallbackTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new ModActivation.CallbackResult(true, exception.ToString());
        }
    }

    /// <summary>
    /// Stops consumers first, retaining failed activations and all their owners when cleanup is uncertain.
    /// </summary>
    private async Task<bool> StopAsync(IEnumerable<string> ids, List<ModDiagnostic> diagnostics)
    {
        foreach (var id in ids)
        {
            if (!_activations.TryGetValue(id, out var activation)) continue;

            var isEnabled = _snapshots.TryGetValue(id, out var info) && info.IsEnabled;
            var hasInitializationFailed = info?.State == ModState.Failed;
            SetState(activation.Definition, ModState.Stopping, isEnabled);
            var outcome = await activation.StopAsync(_options.CallbackTimeout).ConfigureAwait(false);

            if (!outcome.IsCompleted || outcome.Error is not null)
            {
                SetState(activation.Definition, ModState.Failed, isEnabled);
                diagnostics.Add(Diagnostic("ModRetirementFailed", outcome.Error ?? "Retirement timed out.", id));
                return false;
            }

            _activations.Remove(id);
            SetState(activation.Definition, hasInitializationFailed ? ModState.Failed : ModState.Inactive, isEnabled);
        }

        return true;
    }

    /// <summary>
    /// Releases assembly resources before removing registry ownership, then verifies collection through weak references.
    /// </summary>
    private async Task<bool> ReleaseAsync(string[] names, List<ModDiagnostic> diagnostics)
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

        // Host-acquired native references intentionally remain loaded until process exit.
        _unconfirmedOwners.UnionWith(names);
        diagnostics.Add(Diagnostic("CollectionUnconfirmed", $"Collection was not confirmed for released owners: {string.Join(", ", names)}."));
        return false;
    }

    /// <summary>
    /// Publishes a value-only observation without exposing the activation object.
    /// </summary>
    private void SetState(ModDefinition definition, ModState state, bool isEnabled)
    {
        _snapshots[definition.Id] = new ModInfo
        {
            Id = definition.Id,
            Version = definition.Installation.Manifest.Version,
            IsEnabled = isEnabled,
            State = state,
            SkipReason = state == ModState.Skipped ? definition.SkipReason : null
        };
    }

    /// <summary>
    /// Blocks future mutations after uncertain shutdown or collection.
    /// </summary>
    private ModApplyResult RequireRestart(List<ModDiagnostic> diagnostics)
    {
        IsRestartRequired = true;
        return Result(ModApplyStatus.RestartRequired, diagnostics);
    }

    /// <summary>
    /// Captures only scalar state and diagnostic strings for the application boundary.
    /// </summary>
    private ModApplyResult Result(ModApplyStatus status, IEnumerable<ModDiagnostic> diagnostics)
    {
        return new ModApplyResult
        {
            Status = status,
            ActiveModIds = _activations
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
    private static ModDiagnostic Diagnostic(string code, string detail, string? modId = null)
    {
        return new ModDiagnostic { ModId = modId, Code = code, Detail = detail };
    }
}
