using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Everywhere.Common;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Hosting.Sources;
using Everywhere.Modding.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Everywhere.Modding;

/// <summary>
/// Manages mod repository sources, offline dependency resolution, immutable package storage, license confirmation,
/// cold-start reconciliation, <c>N=1</c> rollback, and live <see cref="ModEngine"/> activations for the host application.
/// </summary>
public sealed class ModManager : IModManager, IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// Reserved source identifier for mods embedded inside the host distribution.
    /// </summary>
    public const string BuiltInSourceId = "builtin";

    /// <summary>
    /// Reserved source identifier for the build-configured official remote repository.
    /// </summary>
    public const string OfficialSourceId = "official";

    /// <summary>
    /// Named <see cref="HttpClient"/> registered in host DI for mod catalog and artifact downloads.
    /// </summary>
    public const string HttpClientName = "Everywhere.Modding";

    /// <inheritdoc />
    public ModHostEnvironment? HostEnvironment { get; private set; }

    /// <inheritdoc />
    public bool IsRestartRequired => _engine?.IsRestartRequired ?? false;

    /// <inheritdoc />
    public IReadOnlyList<ModDiagnostic> RestartDiagnostics => Volatile.Read(ref _restartDiagnostics);

    /// <inheritdoc />
    public IReadOnlyList<ModSummary> InstalledMods => Volatile.Read(ref _installedMods);

    /// <inheritdoc />
    public IReadOnlyList<ModSourceInfo> Sources => Volatile.Read(ref _sources);

    /// <inheritdoc />
    static AsyncInitializerIndex IAsyncInitializer.Index => AsyncInitializerIndex.Startup;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    private readonly IServiceProvider _services;
    private readonly ModManagerOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ModPackageStore _packageStore;
    private readonly ModStateStore _stateStore;
    private readonly Dictionary<string, EmbeddedModSource> _embeddedSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IModRepositorySource> _runtimeSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ModDiagnostic>> _modDiagnostics = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _operation = new(1, 1);

    private ModEngine? _engine;
    private IReadOnlyList<ModSummary> _installedMods = [];
    private IReadOnlyList<InstalledModSnapshot> _installedSnapshots = [];
    private IReadOnlyList<ModSourceInfo> _sources = [];
    private IReadOnlyList<ModDiagnostic> _restartDiagnostics = [];
    private bool _isInitialized;
    private int _disposeState;

    /// <summary>
    /// Initializes a <see cref="ModManager"/> bound to host services, optional built-in embedded sources, and path/environment overrides.
    /// </summary>
    public ModManager(
        IServiceProvider services,
        ModManagerOptions? options = null,
        IEnumerable<IModRepositorySource>? builtInSources = null,
        HttpClient? httpClient = null
    )
    {
        _services = services;
        _options = options ?? new ModManagerOptions();

        if (httpClient is not null)
        {
            _httpClient = httpClient;
        }
        else if (services.GetService<IHttpClientFactory>() is { } factory)
        {
            _httpClient = factory.CreateClient(HttpClientName);
        }
        else
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            _ownsHttpClient = true;
        }

        var packageRoot = _options.PackageStoreDirectory ?? RuntimeConstants.EnsureCacheFolderPath("mods");
        var dataRoot = _options.DataDirectory ?? Path.Combine(RuntimeConstants.ConfigurationFolderPath, "mods-data");
        var stagingRoot = _options.StagingDirectory ?? RuntimeConstants.EnsureCacheFolderPath("mods", "staging");
        var statePath = _options.StateFilePath ?? Path.Combine(RuntimeConstants.ConfigurationFolderPath, ModStateStore.DefaultFileName);

        _packageStore = new ModPackageStore(packageRoot, dataRoot, stagingRoot);
        _stateStore = new ModStateStore(statePath, _packageStore);

        if (builtInSources is not null)
        {
            foreach (var source in builtInSources)
            {
                if (source is EmbeddedModSource embedded)
                {
                    _embeddedSources[embedded.Id] = embedded;
                }
                else
                {
                    _runtimeSources[source.Id] = source;
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var shouldRaiseChanged = false;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isInitialized) return;

            TryLoadStateStore();
            var startupDiagnostics = await _stateStore.ReconcileStartupAsync(cancellationToken).ConfigureAwait(false);
            RecordDiagnostics(startupDiagnostics);

            if (TryResolveHostCatalog(out var hostEnvironment, out var hostCatalog))
            {
                HostEnvironment = hostEnvironment;
                _engine = new ModEngine(hostCatalog, _services, _options.EngineOptions);
            }

            RebuildSourcesLocked();

            if (_engine is not null)
            {
                await ReconcileEmbeddedSourcesLockedAsync(cancellationToken).ConfigureAwait(false);
                var applyResult = await _engine.ApplyAsync(BuildDesiredDefinitionsLocked(), cancellationToken).ConfigureAwait(false);
                RecordApplyResult("startup", applyResult);
            }

            _isInitialized = true;
            RefreshSnapshotsLocked();
            shouldRaiseChanged = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log.ForContext<ModManager>().Error("Mod startup failed: {Detail}", exception.ToString());
        }
        finally
        {
            _operation.Release();
        }

        if (shouldRaiseChanged)
        {
            OnStateChanged();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModSourceCatalogSnapshot>> FetchCatalogsAsync(
        bool shouldForceRefresh = false,
        CancellationToken cancellationToken = default
    )
    {
        KeyValuePair<ModSourceInfo, IModRepositorySource>[] targets;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            targets = _sources
                .Where(info => info.IsEnabled && _runtimeSources.ContainsKey(info.Id))
                .Select(info => new KeyValuePair<ModSourceInfo, IModRepositorySource>(info, _runtimeSources[info.Id]))
                .ToArray();
        }
        finally
        {
            _operation.Release();
        }

        var results = new ModSourceCatalogSnapshot[targets.Length];
        await Task.WhenAll(
                targets.Select(async (pair, index) =>
                {
                    try
                    {
                        var indexDocument = await pair.Value.FetchIndexAsync(shouldForceRefresh, cancellationToken).ConfigureAwait(false);
                        results[index] = new ModSourceCatalogSnapshot(pair.Key, indexDocument, null);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        results[index] = new ModSourceCatalogSnapshot(pair.Key, null, exception.Message);
                    }
                }))
            .ConfigureAwait(false);

        return results;
    }

    /// <inheritdoc />
    public async Task<string?> ReadDocumentAsync(string sourceId, string documentReference, CancellationToken cancellationToken = default)
    {
        IModRepositorySource? source;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            _runtimeSources.TryGetValue(sourceId, out source);
        }
        finally
        {
            _operation.Release();
        }

        return source is null
            ? null
            : await source.ReadTextDocumentAsync(documentReference, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> ReadInstalledDocumentAsync(string modId, string relativePath, CancellationToken cancellationToken = default)
    {
        string? resolvedPath = null;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_stateStore.TryOpenActiveInstallation(modId, out var installation))
            {
                resolvedPath = PackagePath.Resolve(installation.Directory, relativePath);
            }
        }
        finally
        {
            _operation.Release();
        }

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            return null;
        }

        const long maxDocumentBytes = 1024 * 1024;
        await using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, useAsync: true);
        if (stream.Length > maxDocumentBytes)
        {
            throw new ModValidationException("DocumentTooLarge", $"Installed document '{relativePath}' exceeds {maxDocumentBytes} bytes.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> PlanInstallAsync(
        string modId,
        string? preferredSourceId = null,
        string? versionRange = null,
        string? channel = null,
        CancellationToken cancellationToken = default
    )
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            var plan = await PlanTargetAsync(
                    modId,
                    preferredSourceId,
                    versionRange,
                    channel,
                    acceptedLicenses: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return plan.ImmediateFailure ?? CreatePlanResult(plan);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <inheritdoc />
    public Task<ModOperationResult> InstallAsync(
        string modId,
        string? preferredSourceId = null,
        string? versionRange = null,
        string? channel = null,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        return InstallOrUpdateCoreAsync(
            modId,
            preferredSourceId,
            versionRange,
            channel,
            acceptedLicenses,
            progress,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> UpdateAsync(
        string modId,
        string? versionRange = null,
        string? channel = null,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        string? preferredSourceId;
        string? savedChannel;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!_stateStore.State.Mods.TryGetValue(modId, out var existing))
            {
                return Failed($"Mod '{modId}' is not installed.");
            }

            if (existing.SourceId is ModStateRecord.LocalZipSourceId or ModStateRecord.LocalDirectorySourceId)
            {
                return Failed($"Mod '{modId}' is locally imported and cannot be updated from a repository source.");
            }

            preferredSourceId = existing.SourceId;
            savedChannel = channel ?? existing.PreferredChannel ?? "stable";
            versionRange ??= existing.PinnedVersion is not null ? $"[{existing.PinnedVersion}]" : null;
        }
        finally
        {
            _operation.Release();
        }

        return await InstallOrUpdateCoreAsync(
                modId,
                preferredSourceId,
                versionRange,
                savedChannel,
                acceptedLicenses,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> ImportZipAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(zipPath);
        if (!File.Exists(fullPath))
        {
            return Failed($"Mod archive '{fullPath}' does not exist.");
        }

        ModOperationResult result;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            await using var stream = File.OpenRead(fullPath);
            var installation = await _packageStore.ImportAsync(stream, cancellationToken).ConfigureAwait(false);
            result = await CommitLocalInstallationLockedAsync(
                    installation,
                    ModStateRecord.LocalZipSourceId,
                    localDirectoryPath: null,
                    localDirectoryFingerprint: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ModValidationException or IOException or UnauthorizedAccessException)
        {
            return Failed(exception.Message);
        }
        finally
        {
            _operation.Release();
        }

        if (result.Status is ModOperationStatus.Applied or ModOperationStatus.CompletedWithErrors or ModOperationStatus.RestartRequired)
        {
            OnStateChanged();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> ImportDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(fullPath))
        {
            return Failed($"Mod directory '{fullPath}' does not exist.");
        }

        ModOperationResult result;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            var fingerprint = await ModPackageStore.ComputeDirectoryFingerprintAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var installation = await _packageStore.ImportDirectoryAsync(fullPath, cancellationToken).ConfigureAwait(false);
            result = await CommitLocalInstallationLockedAsync(
                    installation,
                    ModStateRecord.LocalDirectorySourceId,
                    fullPath,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ModValidationException or IOException or UnauthorizedAccessException)
        {
            return Failed(exception.Message);
        }
        finally
        {
            _operation.Release();
        }

        if (result.Status is ModOperationStatus.Applied or ModOperationStatus.CompletedWithErrors or ModOperationStatus.RestartRequired)
        {
            OnStateChanged();
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> ReloadLocalDirectoryAsync(string modId, CancellationToken cancellationToken = default)
    {
        string? trackedDirectory;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!_stateStore.State.Mods.TryGetValue(modId, out var record) ||
                record.SourceId != ModStateRecord.LocalDirectorySourceId ||
                string.IsNullOrWhiteSpace(record.LocalDirectoryPath))
            {
                return Failed($"Mod '{modId}' is not tracked as an unpacked local development directory.");
            }

            trackedDirectory = record.LocalDirectoryPath;
        }
        finally
        {
            _operation.Release();
        }

        return await ImportDirectoryAsync(trackedDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> SetEnabledAsync(
        string modId,
        bool isEnabled,
        bool shouldCascadeDependents = false,
        bool shouldCascadeDependencies = false,
        CancellationToken cancellationToken = default
    )
    {
        if (isEnabled)
        {
            return await EnableModAsync(modId, shouldCascadeDependencies, cancellationToken).ConfigureAwait(false);
        }

        ModOperationResult result;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            if (!_stateStore.State.Mods.TryGetValue(modId, out var record))
            {
                return Failed($"Mod '{modId}' is not installed.");
            }

            if (!record.IsEnabled)
            {
                return new ModOperationResult { Status = ModOperationStatus.Applied };
            }

            var snapshots = BuildInstalledSnapshotsLocked();
            var dependents = ModDependencySolver.GetReverseDependents(modId, snapshots, shouldIncludeDisabled: false);
            if (dependents.Count > 0 && !shouldCascadeDependents)
            {
                return new ModOperationResult
                {
                    Status = ModOperationStatus.ConfirmationRequired,
                    AffectedDependents = dependents,
                    Message = $"Disabling '{modId}' also requires disabling dependent mod(s): {string.Join(", ", dependents)}."
                };
            }

            record.IsEnabled = false;
            foreach (var dependentId in dependents)
            {
                if (_stateStore.State.Mods.TryGetValue(dependentId, out var depRecord))
                {
                    depRecord.IsEnabled = false;
                }
            }

            _stateStore.Save();

            if (_engine is not null && !_engine.IsRestartRequired)
            {
                var applyResult = await _engine.ApplyAsync(BuildDesiredDefinitionsLocked(), cancellationToken).ConfigureAwait(false);
                RecordApplyResult("disable", applyResult);
                RefreshSnapshotsLocked();
                result = FromApplyResult(applyResult);
            }
            else
            {
                RefreshSnapshotsLocked();
                result = _engine?.IsRestartRequired == true
                    ? RestartRequiredResult()
                    : new ModOperationResult { Status = ModOperationStatus.Applied };
            }
        }
        finally
        {
            _operation.Release();
        }

        OnStateChanged();
        return result;
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> UninstallAsync(
        string modId,
        bool shouldDeleteData = false,
        bool shouldCascadeDependents = false,
        bool shouldRemoveOrphanDependencies = false,
        CancellationToken cancellationToken = default
    )
    {
        ModOperationResult result;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            if (!_stateStore.State.Mods.TryGetValue(modId, out _))
            {
                return Failed($"Mod '{modId}' is not installed.");
            }

            var snapshots = BuildInstalledSnapshotsLocked();
            var dependents = ModDependencySolver.GetReverseDependents(modId, snapshots, shouldIncludeDisabled: true);
            if (dependents.Count > 0 && !shouldCascadeDependents)
            {
                return new ModOperationResult
                {
                    Status = ModOperationStatus.ConfirmationRequired,
                    AffectedDependents = dependents,
                    Message = $"Uninstalling '{modId}' also requires uninstalling dependent mod(s): {string.Join(", ", dependents)}."
                };
            }

            var uninstallIds = new List<string> { modId };
            foreach (var dependentId in dependents)
            {
                if (!uninstallIds.Contains(dependentId, StringComparer.Ordinal))
                {
                    uninstallIds.Add(dependentId);
                }
            }

            if (shouldRemoveOrphanDependencies)
            {
                var orphans = ModDependencySolver.GetOrphanAutoDependencies(uninstallIds.ToHashSet(StringComparer.Ordinal), snapshots);
                foreach (var orphanId in orphans)
                {
                    if (!uninstallIds.Contains(orphanId, StringComparer.Ordinal))
                    {
                        uninstallIds.Add(orphanId);
                    }
                }
            }

            var wasAnyActive = uninstallIds.Any(id =>
                _stateStore.State.Mods.TryGetValue(id, out var record) && record.IsEnabled);

            ModApplyResult? applyResult = null;
            var isRestartRequired = _engine?.IsRestartRequired ?? false;
            if (wasAnyActive && _engine is not null && !isRestartRequired)
            {
                var uninstallSet = uninstallIds.ToHashSet(StringComparer.Ordinal);
                var desired = BuildDesiredDefinitionsLocked(excludedModIds: uninstallSet);
                applyResult = await _engine.ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
                RecordApplyResult("uninstall", applyResult);
                isRestartRequired = applyResult.Status == ModApplyStatus.RestartRequired || _engine.IsRestartRequired;
            }

            foreach (var targetId in uninstallIds)
            {
                var deleteThisData = shouldDeleteData &&
                    (string.Equals(targetId, modId, StringComparison.Ordinal) ||
                        _stateStore.State.Mods.TryGetValue(targetId, out var r) && r.Intent == ModInstallIntent.AutoDependency);
                _stateStore.RecordUninstall(targetId, isRestartRequired: isRestartRequired, shouldDeleteData: deleteThisData);
            }

            RefreshSnapshotsLocked();

            if (isRestartRequired)
            {
                result = new ModOperationResult
                {
                    Status = ModOperationStatus.RestartRequired,
                    Diagnostics = applyResult?.Diagnostics ?? [],
                    Message = $"Mod '{modId}' could not be unloaded while active and has been scheduled for removal on next restart."
                };
            }
            else
            {
                result = applyResult is null
                    ? new ModOperationResult { Status = ModOperationStatus.Applied }
                    : FromApplyResult(applyResult);
            }
        }
        finally
        {
            _operation.Release();
        }

        OnStateChanged();
        return result;
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> RollbackAsync(string modId, CancellationToken cancellationToken = default)
    {
        ModOperationResult result;
        bool shouldRaiseChanged;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            if (!_stateStore.State.Mods.TryGetValue(modId, out var record) || record.PreviousInstallation is not { } previousSlot)
            {
                return Failed($"Mod '{modId}' has no retained previous installation to roll back to.");
            }

            var previousDirectory = _stateStore.ResolveSlotDirectory(previousSlot);
            if (!_packageStore.TryOpen(previousDirectory, out var previousInstallation))
            {
                _stateStore.TryRollback(modId, isRestartRequired: false);
                RefreshSnapshotsLocked();
                shouldRaiseChanged = true;
                result = Failed($"Rollback slot for '{modId}' is missing or corrupted and was cleared.");
            }
            else
            {
                ModApplyResult? applyResult = null;
                var isRestartRequired = _engine?.IsRestartRequired ?? false;
                if (record.IsEnabled && _engine is not null && !isRestartRequired)
                {
                    var desired = BuildDesiredDefinitionsLocked(
                        overrides: new Dictionary<string, ModInstallation>(StringComparer.Ordinal) { [modId] = previousInstallation });
                    applyResult = await _engine.ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
                    RecordApplyResult("rollback", applyResult);
                    isRestartRequired = applyResult.Status == ModApplyStatus.RestartRequired || _engine.IsRestartRequired;
                }

                if (!_stateStore.TryRollback(modId, isRestartRequired))
                {
                    shouldRaiseChanged = applyResult is not null;
                    if (shouldRaiseChanged) RefreshSnapshotsLocked();
                    result = Failed($"Failed to roll back '{modId}'.");
                }
                else
                {
                    RefreshSnapshotsLocked();
                    shouldRaiseChanged = true;

                    if (isRestartRequired)
                    {
                        result = new ModOperationResult
                        {
                            Status = ModOperationStatus.RestartRequired,
                            Diagnostics = applyResult?.Diagnostics ?? [],
                            Message = $"Rollback for '{modId}' requires a restart and has been queued for the next launch."
                        };
                    }
                    else
                    {
                        result = applyResult is null
                            ? new ModOperationResult { Status = ModOperationStatus.Applied }
                            : FromApplyResult(applyResult);
                    }
                }
            }
        }
        finally
        {
            _operation.Release();
        }

        if (shouldRaiseChanged) OnStateChanged();
        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetReverseDependents(string modId, bool shouldIncludeDisabled = false) =>
        ModDependencySolver.GetReverseDependents(modId, Volatile.Read(ref _installedSnapshots), shouldIncludeDisabled);

    /// <inheritdoc />
    public IReadOnlyList<string> GetOrphanAutoDependencies(IReadOnlySet<string> removingModIds) =>
        ModDependencySolver.GetOrphanAutoDependencies(removingModIds, Volatile.Read(ref _installedSnapshots));

    /// <inheritdoc />
    public async Task<ModOperationResult> AddCustomSourceAsync(
        string urlOrPath,
        string? id = null,
        string[]? mirrors = null,
        bool isUntrustedConfirmed = false,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
        {
            return Failed("Source URL or directory path is required.");
        }

        var trimmed = urlOrPath.Trim();
        var isHttp = trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        string normalizedUrl;
        string[] normalizedMirrors;
        string sourceId;
        IModRepositorySource probeSource;

        try
        {
            if (isHttp)
            {
                ModValidationRules.ValidateHttpUri(trimmed);
                normalizedUrl = trimmed;
                normalizedMirrors = (mirrors ?? [])
                    .Where(static m => !string.IsNullOrWhiteSpace(m))
                    .Select(static m =>
                    {
                        var candidate = m.Trim();
                        ModValidationRules.ValidateHttpUri(candidate);
                        return candidate;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                sourceId = string.IsNullOrWhiteSpace(id) ? GenerateSourceId("http", normalizedUrl) : id.Trim().ToLowerInvariant();
                probeSource = CreateHttpSource(sourceId, normalizedUrl, normalizedMirrors, isSignatureRequired: false, trustedPublicKey: null);
            }
            else
            {
                var directoryPath = Uri.TryCreate(trimmed, UriKind.Absolute, out var fileUri) && fileUri.IsFile
                    ? Path.GetFullPath(fileUri.LocalPath)
                    : Path.GetFullPath(trimmed);
                if (!Directory.Exists(directoryPath))
                {
                    return Failed($"Repository directory '{directoryPath}' does not exist.");
                }

                normalizedUrl = directoryPath;
                normalizedMirrors = [];
                sourceId = string.IsNullOrWhiteSpace(id) ? GenerateSourceId("local", directoryPath) : id.Trim().ToLowerInvariant();
                probeSource = new LocalDirectoryModSource(sourceId, directoryPath, isSignatureRequired: false, trustedPublicKey: null);
            }

            if (sourceId is BuiltInSourceId or OfficialSourceId or ModStateRecord.LocalZipSourceId or ModStateRecord.LocalDirectorySourceId ||
                !ModValidationRules.IsValidIdentifier(sourceId))
            {
                return Failed($"Source identifier '{sourceId}' is invalid or reserved.");
            }
        }
        catch (ModValidationException exception)
        {
            return Failed(exception.Message);
        }

        ModIndex fetchedIndex;
        try
        {
            fetchedIndex = await probeSource.FetchIndexAsync(shouldForceRefresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed($"Failed to validate repository source '{normalizedUrl}': {exception.Message}");
        }

        var isSigned = !string.IsNullOrWhiteSpace(fetchedIndex.PublicKey) && !string.IsNullOrWhiteSpace(fetchedIndex.Signature);
        if (!isSigned && !isUntrustedConfirmed)
        {
            return new ModOperationResult
            {
                Status = ModOperationStatus.ConfirmationRequired,
                Message = $"Repository '{normalizedUrl}' is unsigned. Confirm adding an untrusted source to continue."
            };
        }

        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_embeddedSources.ContainsKey(sourceId) ||
                string.Equals(sourceId, OfficialSourceId, StringComparison.Ordinal) ||
                _stateStore.State.Sources.Any(existing =>
                    string.Equals(existing.Id, sourceId, StringComparison.Ordinal) &&
                    existing.IsBuiltIn))
            {
                return Failed($"Built-in source '{sourceId}' cannot be overwritten.");
            }

            var existingIndex = _stateStore.State.Sources.FindIndex(s => string.Equals(s.Id, sourceId, StringComparison.Ordinal));
            var record = new ModSourceRecord
            {
                Id = sourceId,
                Url = normalizedUrl,
                Mirrors = normalizedMirrors,
                IsEnabled = true,
                IsBuiltIn = false,
                IsUntrustedConfirmed = !isSigned && isUntrustedConfirmed,
                PublicKey = isSigned ? fetchedIndex.PublicKey : null
            };

            if (existingIndex >= 0)
            {
                _stateStore.State.Sources[existingIndex] = record;
            }
            else
            {
                _stateStore.State.Sources.Add(record);
            }

            RebuildSourcesLocked();
            _stateStore.Save();
            RefreshSnapshotsLocked();
        }
        finally
        {
            _operation.Release();
        }

        OnStateChanged();
        return new ModOperationResult { Status = ModOperationStatus.Applied };
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> RemoveCustomSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_embeddedSources.ContainsKey(sourceId) || string.Equals(sourceId, OfficialSourceId, StringComparison.Ordinal))
            {
                return Failed($"Built-in source '{sourceId}' cannot be removed.");
            }

            var index = _stateStore.State.Sources.FindIndex(s => string.Equals(s.Id, sourceId, StringComparison.Ordinal));
            if (index < 0)
            {
                return Failed($"Repository source '{sourceId}' was not found.");
            }

            if (_stateStore.State.Sources[index].IsBuiltIn)
            {
                return Failed($"Built-in source '{sourceId}' cannot be removed.");
            }

            _stateStore.State.Sources.RemoveAt(index);
            _runtimeSources.Remove(sourceId);
            RebuildSourcesLocked();
            _stateStore.Save();
            RefreshSnapshotsLocked();
        }
        finally
        {
            _operation.Release();
        }

        OnStateChanged();
        return new ModOperationResult { Status = ModOperationStatus.Applied };
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> SetSourceEnabledAsync(string sourceId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (_embeddedSources.ContainsKey(sourceId))
            {
                return Failed($"Built-in source '{sourceId}' is always enabled.");
            }

            var record = _stateStore.State.Sources.FirstOrDefault(s => string.Equals(s.Id, sourceId, StringComparison.Ordinal));
            if (record is null)
            {
                return Failed($"Repository source '{sourceId}' was not found.");
            }

            if (record.IsEnabled == isEnabled)
            {
                return new ModOperationResult { Status = ModOperationStatus.Applied };
            }

            record.IsEnabled = isEnabled;
            RebuildSourcesLocked();
            _stateStore.Save();
            RefreshSnapshotsLocked();
        }
        finally
        {
            _operation.Release();
        }

        OnStateChanged();
        return new ModOperationResult { Status = ModOperationStatus.Applied };
    }

    /// <inheritdoc />
    public async Task<ModOperationResult> ReorderSourcesAsync(IReadOnlyList<string> orderedSourceIds, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
            var existingById = _stateStore.State.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
            var reordered = new List<ModSourceRecord>(_stateStore.State.Sources.Count);
            var added = new HashSet<string>(StringComparer.Ordinal);

            foreach (var id in orderedSourceIds)
            {
                if (existingById.TryGetValue(id, out var record) && added.Add(id))
                {
                    reordered.Add(record);
                }
            }

            foreach (var remaining in _stateStore.State.Sources)
            {
                if (added.Add(remaining.Id))
                {
                    reordered.Add(remaining);
                }
            }

            _stateStore.State.Sources = reordered;
            RebuildSourcesLocked();
            _stateStore.Save();
            RefreshSnapshotsLocked();
        }
        finally
        {
            _operation.Release();
        }

        OnStateChanged();
        return new ModOperationResult { Status = ModOperationStatus.Applied };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_engine is not null)
            {
                try
                {
                    await _engine.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Failure in an extension must not prevent DI from disposing remaining host services.
                    Log.ForContext<ModManager>().Error("Mod disposal failed: {Detail}", exception.ToString());
                }
            }

            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<ModOperationResult> InstallOrUpdateCoreAsync(
        string modId,
        string? preferredSourceId,
        string? versionRange,
        string? channel,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    )
    {
        ModOperationResult result;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            var plan = await PlanTargetAsync(
                    modId,
                    preferredSourceId,
                    versionRange,
                    channel,
                    acceptedLicenses,
                    cancellationToken)
                .ConfigureAwait(false);

            if (plan.ImmediateFailure is not null)
            {
                return plan.ImmediateFailure;
            }

            if (plan.PendingLicenses.Count > 0)
            {
                return CreatePlanResult(plan);
            }

            result = await ExecuteSolvedPlanLockedAsync(
                    plan,
                    preferredChannel: channel,
                    pinnedVersion: null,
                    acceptedLicenses,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }

        if (result.Status is ModOperationStatus.Applied or ModOperationStatus.CompletedWithErrors or ModOperationStatus.RestartRequired)
        {
            OnStateChanged();
        }

        return result;
    }

    private async Task<ModOperationResult> EnableModAsync(
        string modId,
        bool shouldCascadeDependencies,
        CancellationToken cancellationToken
    )
    {
        ModOperationResult result;
        bool shouldRaiseChanged;
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureEngineInitializedLockedAsync(cancellationToken).ConfigureAwait(false);
            if (IsRestartRequired)
            {
                return RestartRequiredResult();
            }

            if (!_stateStore.State.Mods.TryGetValue(modId, out var record))
            {
                return Failed($"Mod '{modId}' is not installed.");
            }

            if (record.IsEnabled)
            {
                return new ModOperationResult { Status = ModOperationStatus.Applied };
            }

            if (HostEnvironment is not { } hostEnvironment || _engine is not { } engine)
            {
                return Failed("Mod runtime host descriptor could not be resolved.");
            }

            var target = new ModTargetRequirement(
                modId,
                VersionRange: null,
                PreferredChannel: null,
                PreferredSourceId: record.SourceId,
                Intent: record.Intent);

            // Attempt offline resolution first using already-installed dependencies before querying network sources.
            var installed = BuildInstalledSnapshotsLocked();
            var offlineSolve = ModDependencySolver.Solve(
                new ModSolveRequest
                {
                    Host = hostEnvironment,
                    Sources = [],
                    Installed = installed,
                    Targets = [target],
                    ShouldKeepInstalledVersions = true
                });

            if (offlineSolve.IsSuccess)
            {
                var disabledDependencies = CollectDisabledDependenciesLocked(offlineSolve, modId);
                if (disabledDependencies.Length > 0 && !shouldCascadeDependencies)
                {
                    return new ModOperationResult
                    {
                        Status = ModOperationStatus.ConfirmationRequired,
                        AffectedDependencies = disabledDependencies,
                        SolveResult = offlineSolve,
                        Message = $"Enabling '{modId}' also requires enabling dependency mod(s): {string.Join(", ", disabledDependencies)}."
                    };
                }

                foreach (var pkg in offlineSolve.Packages)
                {
                    if (_stateStore.State.Mods.TryGetValue(pkg.Id, out var pkgRecord))
                    {
                        pkgRecord.IsEnabled = true;
                    }
                }

                _stateStore.Save();
                var applyResult = await engine.ApplyAsync(BuildDesiredDefinitionsLocked(), cancellationToken).ConfigureAwait(false);
                RecordApplyResult("enable", applyResult);
                RefreshSnapshotsLocked();
                shouldRaiseChanged = true;
                result = FromApplyResult(applyResult);
            }
            else
            {
                var plan = await PlanTargetAsync(
                        modId,
                        preferredSourceId: record.SourceId,
                        versionRange: null,
                        channel: null,
                        acceptedLicenses: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (plan.ImmediateFailure is not null)
                {
                    return plan.ImmediateFailure;
                }

                var solve = plan.SolveResult;
                var disabledDependencies = solve is null ? [] : CollectDisabledDependenciesLocked(solve, modId);
                var hasDownloads = solve?.Packages.Any(static pkg => !pkg.IsAlreadyInstalledVersion) == true;
                if (hasDownloads || plan.PendingLicenses.Count > 0 || disabledDependencies.Length > 0 && !shouldCascadeDependencies)
                {
                    return new ModOperationResult
                    {
                        Status = ModOperationStatus.ConfirmationRequired,
                        AffectedDependencies = disabledDependencies,
                        PendingLicenses = plan.PendingLicenses,
                        SolveResult = solve,
                        Message = hasDownloads
                            ? $"Enabling '{modId}' requires downloading missing dependencies."
                            : plan.PendingLicenses.Count > 0
                                ? "One or more required mods need license acceptance before enabling."
                                : $"Enabling '{modId}' also requires enabling dependency mod(s): {string.Join(", ", disabledDependencies)}."
                    };
                }

                result = await ExecuteSolvedPlanLockedAsync(
                        plan,
                        preferredChannel: record.PreferredChannel,
                        pinnedVersion: record.PinnedVersion,
                        acceptedLicenses: null,
                        progress: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                shouldRaiseChanged =
                    result.Status is ModOperationStatus.Applied or ModOperationStatus.CompletedWithErrors or ModOperationStatus.RestartRequired;
            }
        }
        finally
        {
            _operation.Release();
        }

        if (shouldRaiseChanged) OnStateChanged();
        return result;
    }

    private string[] CollectDisabledDependenciesLocked(ModSolveResult solve, string targetModId) =>
    [
        .. solve.Packages
            .Select(static pkg => pkg.Id)
            .Where(id =>
                !string.Equals(id, targetModId, StringComparison.Ordinal) &&
                _stateStore.State.Mods.TryGetValue(id, out var depRecord) &&
                !depRecord.IsEnabled)
            .Distinct(StringComparer.Ordinal)
    ];

    private async Task<PlannedOperation> PlanTargetAsync(
        string modId,
        string? preferredSourceId,
        string? versionRange,
        string? channel,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses,
        CancellationToken cancellationToken
    )
    {
        if (HostEnvironment is null)
        {
            return new PlannedOperation(null, [], [], Failed("Mod runtime host metadata is unavailable."));
        }

        var catalogs = await FetchEnabledCatalogsLockedAsync(shouldForceRefresh: false, cancellationToken).ConfigureAwait(false);
        var installed = BuildInstalledSnapshotsLocked();
        var solveResult = ModDependencySolver.Solve(
            new ModSolveRequest
            {
                Host = HostEnvironment,
                Sources = catalogs,
                Installed = installed,
                Targets = [new ModTargetRequirement(modId, versionRange, channel, preferredSourceId)],
                ShouldKeepInstalledVersions = versionRange is null && channel is null
            });

        if (!solveResult.IsSuccess)
        {
            return new PlannedOperation(
                solveResult,
                [],
                [],
                new ModOperationResult
                {
                    Status = ModOperationStatus.Failed,
                    SolveResult = solveResult,
                    Message = solveResult.FailureSummary ?? $"Dependency resolution failed for '{modId}'."
                });
        }

        var pendingLicenses = CollectPendingLicenses(solveResult.Packages, acceptedLicenses);
        var affectedDependencies = CollectDisabledDependenciesLocked(solveResult, modId);
        return new PlannedOperation(solveResult, pendingLicenses, affectedDependencies, null);
    }

    private IReadOnlyList<ModLicenseRequirement> CollectPendingLicenses(
        IReadOnlyList<ResolvedModPackage> packages,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses
    )
    {
        var pending = new List<ModLicenseRequirement>();
        foreach (var package in packages)
        {
            var license = package.IndexEntry?.License;
            if (license is null || !license.IsAcceptanceRequired)
            {
                continue;
            }

            if (acceptedLicenses is not null &&
                acceptedLicenses.TryGetValue(package.Id, out var supplied) &&
                string.Equals(supplied.Spdx, license.Spdx, StringComparison.Ordinal))
            {
                continue;
            }

            _stateStore.State.Mods.TryGetValue(package.Id, out var existingRecord);
            if (!ModStateStore.RequiresLicenseAcceptance(existingRecord, license.Spdx, license.IsAcceptanceRequired))
            {
                continue;
            }

            pending.Add(
                new ModLicenseRequirement(
                    package.Id,
                    package.Version,
                    package.SourceId,
                    license.Spdx,
                    package.IndexVersion?.LicenseUrl));
        }

        return pending;
    }

    private async Task<ModOperationResult> ExecuteSolvedPlanLockedAsync(
        PlannedOperation plan,
        string? preferredChannel,
        string? pinnedVersion,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    )
    {
        if (plan.SolveResult is not { } solve)
        {
            return Failed("Missing dependency resolution plan.");
        }

        var packagesToInstall = solve.Packages.Where(pkg => !pkg.IsAlreadyInstalledVersion).ToArray();
        var imported = new Dictionary<string, ModInstallation>(StringComparer.Ordinal);
        var overrides = new Dictionary<string, ModInstallation>(StringComparer.Ordinal);

        try
        {
            for (var i = 0; i < packagesToInstall.Length; i++)
            {
                var pkg = packagesToInstall[i];
                if (!_runtimeSources.TryGetValue(pkg.SourceId, out var source) || pkg.IndexVersion is null)
                {
                    CleanupImported(imported.Values);
                    return Failed($"Source '{pkg.SourceId}' is unavailable for '{pkg.Id}@{pkg.Version}'.");
                }

                var packageIndex = i;
                var stepProgress = progress is null
                    ? null
                    : new Progress<double>(fraction =>
                    {
                        var scaled = (packageIndex + Math.Clamp(fraction, 0d, 1d)) / Math.Max(packagesToInstall.Length, 1);
                        progress.Report(scaled);
                    });

                await using var artifactStream = await source.OpenArtifactAsync(pkg.Id, pkg.IndexVersion, stepProgress, cancellationToken)
                    .ConfigureAwait(false);
                var installation = await _packageStore.ImportAsync(artifactStream, cancellationToken).ConfigureAwait(false);
                imported[pkg.Id] = installation;
                overrides[pkg.Id] = installation;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CleanupImported(imported.Values);
            throw;
        }
        catch (Exception exception)
        {
            CleanupImported(imported.Values);
            return Failed($"Failed to install package: {exception.Message}");
        }

        var extraEnabled = solve.Packages.Select(pkg => pkg.Id).ToHashSet(StringComparer.Ordinal);
        ModApplyResult? applyResult = null;
        var isRestartRequired = _engine?.IsRestartRequired ?? false;

        if (_engine is not null && !isRestartRequired)
        {
            var desired = BuildDesiredDefinitionsLocked(overrides: overrides, additionalEnabledIds: extraEnabled);
            applyResult = await _engine.ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
            RecordApplyResult("install", applyResult);
            isRestartRequired = applyResult.Status == ModApplyStatus.RestartRequired || _engine.IsRestartRequired;
        }

        foreach (var pkg in solve.Packages)
        {
            if (imported.TryGetValue(pkg.Id, out var installation))
            {
                var wasAlreadyActive = _stateStore.State.Mods.TryGetValue(pkg.Id, out var prevRecord) && prevRecord.ActiveInstallation is not null;
                _stateStore.RecordInstallOrUpdate(
                    pkg.Id,
                    installation,
                    pkg.SourceId,
                    pkg.Intent,
                    isRestartRequired: isRestartRequired && wasAlreadyActive,
                    channel: pkg.Intent == ModInstallIntent.Explicit ? preferredChannel : null,
                    pinnedVersion: pkg.Intent == ModInstallIntent.Explicit ? pinnedVersion : null);
            }
            else if (_stateStore.State.Mods.TryGetValue(pkg.Id, out var existing))
            {
                existing.IsEnabled = true;
                if (pkg.Intent == ModInstallIntent.Explicit)
                {
                    existing.Intent = ModInstallIntent.Explicit;
                }
            }

            if (acceptedLicenses is not null && acceptedLicenses.TryGetValue(pkg.Id, out var license))
            {
                _stateStore.RecordLicenseAcceptance(
                    pkg.Id,
                    license.Spdx,
                    license.TextSha256,
                    license.AcceptedAtUnixSeconds > 0 ? license.AcceptedAtUnixSeconds : null);
            }
        }

        _stateStore.Save();
        RefreshSnapshotsLocked();

        if (isRestartRequired)
        {
            return new ModOperationResult
            {
                Status = ModOperationStatus.RestartRequired,
                Diagnostics = applyResult?.Diagnostics ?? [],
                SolveResult = solve,
                Message = "Changes were staged and require a restart to finish unloading replaced mods."
            };
        }

        return applyResult is null
            ? new ModOperationResult { Status = ModOperationStatus.Applied, SolveResult = solve }
            : FromApplyResult(applyResult, solve);
    }

    private async Task<ModOperationResult> CommitLocalInstallationLockedAsync(
        ModInstallation installation,
        string sourceId,
        string? localDirectoryPath,
        string? localDirectoryFingerprint,
        CancellationToken cancellationToken
    )
    {
        if (HostEnvironment is not { } hostEnvironment || _engine is not { } engine)
        {
            _packageStore.TryRemove(installation);
            return Failed("Mod runtime host descriptor could not be resolved.");
        }

        var modId = installation.Manifest.Id;
        var selectedEntry = ModPlatformSelector.Select(installation.Manifest, hostEnvironment.RuntimeIdentifier);

        if (selectedEntry is not null && selectedEntry.Dependencies.Any(static dep => !dep.IsOptional))
        {
            var installed = BuildInstalledSnapshotsLocked();
            var virtualSnapshot = new InstalledModSnapshot(
                modId,
                installation.Manifest.Version,
                sourceId,
                IsEnabled: true,
                Intent: ModInstallIntent.Explicit,
                Entry: selectedEntry);
            var candidateInstalled = installed.Where(item => !string.Equals(item.Id, modId, StringComparison.Ordinal))
                .Append(virtualSnapshot)
                .ToArray();
            var catalogs = await FetchEnabledCatalogsLockedAsync(shouldForceRefresh: false, cancellationToken).ConfigureAwait(false);
            var solve = ModDependencySolver.Solve(
                new ModSolveRequest
                {
                    Host = hostEnvironment,
                    Sources = catalogs,
                    Installed = candidateInstalled,
                    Targets = [new ModTargetRequirement(modId, PreferredSourceId: sourceId, Intent: ModInstallIntent.Explicit)],
                    ShouldKeepInstalledVersions = true
                });

            if (!solve.IsSuccess)
            {
                _packageStore.TryRemove(installation);
                return new ModOperationResult
                {
                    Status = ModOperationStatus.Failed,
                    SolveResult = solve,
                    Message = solve.FailureSummary ?? $"Unsatisfied dependencies for '{modId}'."
                };
            }

            var missingPackages = solve.Packages
                .Where(pkg => !pkg.IsAlreadyInstalledVersion && !string.Equals(pkg.Id, modId, StringComparison.Ordinal))
                .Select(static pkg => $"{pkg.Id} v{pkg.Version}")
                .ToArray();
            if (missingPackages.Length > 0)
            {
                _packageStore.TryRemove(installation);
                return new ModOperationResult
                {
                    Status = ModOperationStatus.Failed,
                    SolveResult = solve,
                    Message =
                        $"Mod '{modId}' requires additional missing dependencies that are not installed yet: {string.Join(", ", missingPackages)}."
                };
            }
        }

        var overrides = new Dictionary<string, ModInstallation>(StringComparer.Ordinal) { [modId] = installation };
        var enabledSet = new HashSet<string>(StringComparer.Ordinal) { modId };
        var desired = BuildDesiredDefinitionsLocked(overrides: overrides, additionalEnabledIds: enabledSet);

        var applyResult = await engine.ApplyAsync(desired, cancellationToken).ConfigureAwait(false);
        RecordApplyResult("import", applyResult);
        var isRestartRequired = applyResult.Status == ModApplyStatus.RestartRequired || engine.IsRestartRequired;
        var wasAlreadyActive = _stateStore.State.Mods.TryGetValue(modId, out var existingRecord) && existingRecord.ActiveInstallation is not null;

        _stateStore.RecordInstallOrUpdate(
            modId,
            installation,
            sourceId,
            ModInstallIntent.Explicit,
            isRestartRequired: isRestartRequired && wasAlreadyActive,
            localDirectoryPath: localDirectoryPath,
            localDirectoryFingerprint: localDirectoryFingerprint);

        RefreshSnapshotsLocked();

        return isRestartRequired
            ? new ModOperationResult
            {
                Status = ModOperationStatus.RestartRequired,
                Diagnostics = applyResult.Diagnostics,
                Message = $"Updated local package for '{modId}' will activate on next launch."
            }
            : FromApplyResult(applyResult);
    }

    private async Task EnsureStateLoadedLockedAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized) return;

        TryLoadStateStore();
        var diagnostics = await _stateStore.ReconcileStartupAsync(cancellationToken).ConfigureAwait(false);
        RecordDiagnostics(diagnostics);

        if (_engine is null && TryResolveHostCatalog(out var hostEnvironment, out var hostCatalog))
        {
            HostEnvironment = hostEnvironment;
            _engine = new ModEngine(hostCatalog, _services, _options.EngineOptions);
        }

        RebuildSourcesLocked();
        _isInitialized = true;
        RefreshSnapshotsLocked();
    }

    private async Task EnsureEngineInitializedLockedAsync(CancellationToken cancellationToken)
    {
        await EnsureStateLoadedLockedAsync(cancellationToken).ConfigureAwait(false);
        if (_engine is null || HostEnvironment is null)
        {
            throw new InvalidOperationException("Mod runtime host descriptor could not be resolved.");
        }
    }

    private void TryLoadStateStore()
    {
        try
        {
            _stateStore.Load();
        }
        catch (Exception exception) when (exception is IOException or JsonException or ModValidationException)
        {
            Log.ForContext<ModManager>().Warning("Failed to load mods-state.json; starting with empty state: {Detail}", exception.Message);
        }
    }

    private bool TryResolveHostCatalog([NotNullWhen(true)] out ModHostEnvironment? environment, [NotNullWhen(true)] out HostAssemblyCatalog? catalog)
    {
        if (_options.HostEnvironment is { } explicitEnv && _options.HostAssemblyCatalog is { } explicitCatalog)
        {
            environment = explicitEnv;
            catalog = explicitCatalog;
            return true;
        }

        try
        {
            var references = new HostReferenceSet(AppContext.BaseDirectory);
            var descriptor = references.Descriptor;
            environment = new ModHostEnvironment(
                descriptor.RuntimeIdentifier,
                descriptor.HostVersion,
                descriptor.ModApiVersion,
                descriptor.Framework);
            catalog = new HostAssemblyCatalog(references.Assemblies.Values);
            return true;
        }
        catch (Exception exception) when (exception is ModValidationException or IOException)
        {
            Log.ForContext<ModManager>().Warning("Mod host descriptor is unavailable; mod loading disabled: {Detail}", exception.Message);
            environment = null;
            catalog = null;
            return false;
        }
    }

    private void RebuildSourcesLocked()
    {
        _runtimeSources.Clear();

        var builtInRecords = new Dictionary<string, ModSourceRecord>(StringComparer.Ordinal);
        foreach (var (id, embedded) in _embeddedSources)
        {
            _runtimeSources[id] = embedded;
            builtInRecords[id] = new ModSourceRecord
            {
                Id = id,
                Url = $"embedded://{id}",
                IsEnabled = true,
                IsBuiltIn = true,
                IsUntrustedConfirmed = false,
                PublicKey = embedded.TrustedPublicKey
            };
        }

        var officialSource = TryCreateOfficialHttpSource(out var officialRecord);
        if (officialSource is not null && officialRecord is not null)
        {
            var existingOfficial = _stateStore.State.Sources.FirstOrDefault(s => string.Equals(s.Id, OfficialSourceId, StringComparison.Ordinal));
            if (existingOfficial is not null)
            {
                officialRecord.IsEnabled = existingOfficial.IsEnabled;
            }

            _runtimeSources[OfficialSourceId] = officialSource;
            builtInRecords[OfficialSourceId] = officialRecord;
        }

        var reconciled = new List<ModSourceRecord>(_stateStore.State.Sources.Count + builtInRecords.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        // Preserve user-saved priority order for active built-in and custom sources.
        foreach (var stored in _stateStore.State.Sources)
        {
            if (builtInRecords.TryGetValue(stored.Id, out var refreshedBuiltIn))
            {
                if (seenIds.Add(stored.Id))
                {
                    reconciled.Add(refreshedBuiltIn);
                }

                continue;
            }

            if (stored.IsBuiltIn || !seenIds.Add(stored.Id))
            {
                // Built-in source that is currently absent/disabled by build config (e.g. unconfigured official URL/key) is hidden cleanly.
                continue;
            }

            if (TryCreateCustomSource(stored, out var customSource))
            {
                _runtimeSources[stored.Id] = customSource;
                reconciled.Add(stored);
            }
        }

        // Prepend newly discovered built-in sources that were not yet present in mods-state.json.
        var insertIndex = 0;
        foreach (var id in _embeddedSources.Keys.Order(StringComparer.Ordinal))
        {
            if (seenIds.Add(id))
            {
                reconciled.Insert(insertIndex++, builtInRecords[id]);
            }
        }

        if (builtInRecords.TryGetValue(OfficialSourceId, out var newOfficial) && seenIds.Add(OfficialSourceId))
        {
            reconciled.Insert(insertIndex, newOfficial);
        }

        _stateStore.State.Sources = reconciled;

        _sources = reconciled
            .Where(record => _runtimeSources.ContainsKey(record.Id))
            .Select(record => new ModSourceInfo
            {
                Id = record.Id,
                Url = record.Url,
                Mirrors = record.Mirrors,
                IsEnabled = record.IsEnabled,
                IsBuiltIn = record.IsBuiltIn,
                IsSignatureRequired = _runtimeSources[record.Id].IsSignatureRequired,
                IsUntrustedConfirmed = record.IsUntrustedConfirmed,
                PublicKey = record.PublicKey
            })
            .ToArray();
    }

    private HttpModSource? TryCreateOfficialHttpSource(out ModSourceRecord? record)
    {
        record = null;
        var url = (_options.OfficialSourceUrl ?? ModdingConstants.OfficialSourceUrl).Trim();
        var publicKey = (_options.OfficialSourcePublicKey ?? ModdingConstants.OfficialSourcePublicKey).Trim();
        var mirrorsRaw = _options.OfficialSourceMirrors ?? ModdingConstants.OfficialSourceMirrors;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(publicKey))
        {
            return null;
        }

        var mirrors = mirrorsRaw
            .Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        try
        {
            var source = CreateHttpSource(OfficialSourceId, url, mirrors, isSignatureRequired: true, trustedPublicKey: publicKey);
            record = new ModSourceRecord
            {
                Id = OfficialSourceId,
                Url = url,
                Mirrors = mirrors,
                IsEnabled = true,
                IsBuiltIn = true,
                IsUntrustedConfirmed = false,
                PublicKey = publicKey
            };
            return source;
        }
        catch (Exception exception) when (exception is ModValidationException or UriFormatException)
        {
            Log.ForContext<ModManager>().Warning("Official remote mod source configuration is invalid and was disabled: {Detail}", exception.Message);
            return null;
        }
    }

    private bool TryCreateCustomSource(ModSourceRecord record, [NotNullWhen(true)] out IModRepositorySource? source)
    {
        try
        {
            var isSignatureRequired = record.PublicKey is not null || !record.IsUntrustedConfirmed;
            var isHttp = record.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                record.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            if (isHttp)
            {
                source = CreateHttpSource(record.Id, record.Url, record.Mirrors, isSignatureRequired, record.PublicKey);
                return true;
            }

            source = new LocalDirectoryModSource(record.Id, record.Url, isSignatureRequired, record.PublicKey);
            return true;
        }
        catch (Exception exception) when (exception is ModValidationException or UriFormatException)
        {
            Log.ForContext<ModManager>().Warning("Ignoring invalid custom mod source '{SourceId}': {Detail}", record.Id, exception.Message);
            source = null;
            return false;
        }
    }

    private HttpModSource CreateHttpSource(
        string id,
        string primaryUrl,
        IReadOnlyList<string> mirrors,
        bool isSignatureRequired,
        string? trustedPublicKey
    )
    {
        var mirrorUris = mirrors.Select(static mirror => new Uri(mirror, UriKind.Absolute)).ToArray();
        var cacheDirectory = _options.HttpCacheDirectory ?? RuntimeConstants.EnsureCacheFolderPath("mods", "http-cache");
        return new HttpModSource(
            _httpClient,
            new HttpModSourceOptions
            {
                Id = id,
                PrimaryIndexUri = new Uri(primaryUrl, UriKind.Absolute),
                MirrorIndexUris = mirrorUris,
                CacheDirectory = cacheDirectory,
                IsSignatureRequired = isSignatureRequired,
                TrustedPublicKey = trustedPublicKey
            });
    }

    private async Task ReconcileEmbeddedSourcesLockedAsync(CancellationToken cancellationToken)
    {
        if (HostEnvironment is null) return;

        foreach (var (_, source) in _embeddedSources.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            ModIndex index;
            try
            {
                index = await source.FetchIndexAsync(shouldForceRefresh: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ModValidationException or IOException)
            {
                Log.ForContext<ModManager>().Warning("Failed to read built-in mod index '{SourceId}': {Detail}", source.Id, exception.Message);
                continue;
            }

            var indexById = index.Mods.ToDictionary(item => item.Id, StringComparer.Ordinal);
            var newlyClaimed = _stateStore.ClaimDefaultEnabledMods(index);
            foreach (var modId in newlyClaimed)
            {
                if (_stateStore.State.Mods.ContainsKey(modId) || !indexById.TryGetValue(modId, out var entry))
                {
                    continue;
                }

                if (SelectBestCompatibleVersion(entry, HostEnvironment) is not { } version)
                {
                    continue;
                }

                if (ModStateStore.RequiresLicenseAcceptance(null, entry.License?.Spdx, entry.License?.IsAcceptanceRequired ?? false))
                {
                    continue;
                }

                await using var stream = await source.OpenArtifactAsync(modId, version, progress: null, cancellationToken).ConfigureAwait(false);
                var installation = await _packageStore.ImportAsync(stream, cancellationToken).ConfigureAwait(false);
                _stateStore.RecordInstallOrUpdate(
                    modId,
                    installation,
                    source.Id,
                    ModInstallIntent.Explicit,
                    isRestartRequired: false,
                    channel: "stable");
            }

            foreach (var (modId, record) in _stateStore.State.Mods.ToArray())
            {
                if (!string.Equals(record.SourceId, source.Id, StringComparison.Ordinal) ||
                    record.PendingOperation is not null ||
                    !indexById.TryGetValue(modId, out var entry))
                {
                    continue;
                }

                if (SelectBestCompatibleVersion(entry, HostEnvironment, record.PinnedVersion) is not { } targetVersion)
                {
                    continue;
                }

                var shouldUpgrade = !_stateStore.TryOpenActiveInstallation(modId, out var currentInstall) ||
                    ModCompatibility.ParseVersion(targetVersion.Version) > ModCompatibility.ParseVersion(currentInstall.Manifest.Version);

                if (!shouldUpgrade)
                {
                    continue;
                }

                var wasEnabled = record.IsEnabled;
                var intent = record.Intent;
                await using var stream =
                    await source.OpenArtifactAsync(modId, targetVersion, progress: null, cancellationToken).ConfigureAwait(false);
                var upgradedInstall = await _packageStore.ImportAsync(stream, cancellationToken).ConfigureAwait(false);
                _stateStore.RecordInstallOrUpdate(
                    modId,
                    upgradedInstall,
                    source.Id,
                    intent,
                    isRestartRequired: false,
                    channel: record.PreferredChannel);

                if (!wasEnabled && _stateStore.State.Mods.TryGetValue(modId, out var updatedRecord))
                {
                    // Preserve explicit user disablement across automatic built-in upgrades and cache repairs.
                    updatedRecord.IsEnabled = false;
                    _stateStore.Save();
                }
            }
        }
    }

    private static ModIndexVersion? SelectBestCompatibleVersion(ModIndexEntry entry, ModHostEnvironment host, string? pinnedVersion = null)
    {
        var hostVersion = ModCompatibility.ParseVersion(host.HostVersion);
        var modApiVersion = ModCompatibility.ParseVersion(host.ModApiVersion);
        entry.Channels.TryGetValue("stable", out var stableVersion);

        return entry.Versions
            .Where(version => pinnedVersion is null || string.Equals(version.Version, pinnedVersion, StringComparison.Ordinal))
            .Where(version => IsVersionHostCompatible(version, host.RuntimeIdentifier, hostVersion, modApiVersion, host.HostFramework))
            .OrderByDescending(version => stableVersion is not null && string.Equals(version.Version, stableVersion, StringComparison.Ordinal))
            .ThenByDescending(version => ModCompatibility.ParseVersion(version.Version))
            .FirstOrDefault();
    }

    private static bool IsVersionHostCompatible(
        ModIndexVersion version,
        string runtimeIdentifier,
        SemanticVersion hostVersion,
        SemanticVersion modApiVersion,
        string hostFramework
    )
    {
        var selected = ModPlatformSelector.Select(version.Entry, runtimeIdentifier);
        if (selected is null) return false;

        if (!SemanticVersionRange.TryParse(selected.ModApiVersion, out var apiRange) || !apiRange.Contains(modApiVersion))
        {
            return false;
        }

        if (selected.HostVersion is not null &&
            (!SemanticVersionRange.TryParse(selected.HostVersion, out var hostRange) || !hostRange.Contains(hostVersion)))
        {
            return false;
        }

        return ModCompatibility.IsFrameworkCompatible(hostFramework, selected.Framework);
    }

    private async Task<IReadOnlyList<ModSourceCatalogEntry>> FetchEnabledCatalogsLockedAsync(
        bool shouldForceRefresh,
        CancellationToken cancellationToken
    )
    {
        var active = _stateStore.State.Sources
            .Where(record => record.IsEnabled && _runtimeSources.ContainsKey(record.Id))
            .Select(record => (record.Id, Source: _runtimeSources[record.Id]))
            .ToArray();

        var entries = new ModSourceCatalogEntry?[active.Length];
        await Task.WhenAll(
                active.Select(async (item, index) =>
                {
                    try
                    {
                        var fetched = await item.Source.FetchIndexAsync(shouldForceRefresh, cancellationToken).ConfigureAwait(false);
                        entries[index] = new ModSourceCatalogEntry(item.Id, fetched);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Log.ForContext<ModManager>().Warning("Failed to fetch index from source '{SourceId}': {Detail}", item.Id, exception.Message);
                    }
                }))
            .ConfigureAwait(false);

        return entries.OfType<ModSourceCatalogEntry>().ToArray();
    }

    private IReadOnlyList<ModDefinition> BuildDesiredDefinitionsLocked(
        IReadOnlyDictionary<string, ModInstallation>? overrides = null,
        IReadOnlySet<string>? additionalEnabledIds = null,
        IReadOnlySet<string>? excludedModIds = null
    )
    {
        var host = HostEnvironment;
        if (host is null) return [];

        var definitions = new List<ModDefinition>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (modId, record) in _stateStore.State.Mods.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (excludedModIds is not null && excludedModIds.Contains(modId))
            {
                continue;
            }

            var isEnabled = record.IsEnabled || additionalEnabledIds is not null && additionalEnabledIds.Contains(modId);
            if (!isEnabled)
            {
                continue;
            }

            ModInstallation? installation;
            if (overrides is not null && overrides.TryGetValue(modId, out var overrideInstall))
            {
                installation = overrideInstall;
            }
            else if (!_stateStore.TryOpenActiveInstallation(modId, out installation))
            {
                RecordDiagnostics(
                [
                    new ModDiagnostic
                    {
                        ModId = modId,
                        Code = "InstallationMissing",
                        Detail = $"Cached installation for enabled mod '{modId}' could not be opened."
                    }
                ]);
                continue;
            }

            definitions.Add(
                new ModDefinition(
                    installation,
                    _packageStore.GetDataDirectory(installation),
                    host.RuntimeIdentifier,
                    host.HostVersion,
                    host.ModApiVersion,
                    host.HostFramework));
            emitted.Add(modId);
        }

        if (overrides is not null)
        {
            foreach (var (modId, overrideInstall) in overrides)
            {
                if (excludedModIds is not null && excludedModIds.Contains(modId) || !emitted.Add(modId))
                {
                    continue;
                }

                definitions.Add(
                    new ModDefinition(
                        overrideInstall,
                        _packageStore.GetDataDirectory(overrideInstall),
                        host.RuntimeIdentifier,
                        host.HostVersion,
                        host.ModApiVersion,
                        host.HostFramework));
            }
        }

        return definitions;
    }

    private IReadOnlyList<InstalledModSnapshot> BuildInstalledSnapshotsLocked()
    {
        var host = HostEnvironment;
        var snapshots = new List<InstalledModSnapshot>(_stateStore.State.Mods.Count);
        foreach (var (modId, record) in _stateStore.State.Mods)
        {
            var version = record.ActiveInstallation?.Version ?? "0.0.0";
            UniversalModEntry? selected = null;

            if (_stateStore.TryOpenActiveInstallation(modId, out var installation))
            {
                version = installation.Manifest.Version;
                if (host is not null)
                {
                    selected = ModPlatformSelector.Select(installation.Manifest, host.RuntimeIdentifier);
                }
            }

            snapshots.Add(
                new InstalledModSnapshot(
                    modId,
                    version,
                    record.SourceId,
                    record.IsEnabled,
                    record.Intent,
                    selected));
        }

        return snapshots;
    }

    private void RefreshSnapshotsLocked()
    {
        var runtimeById = _engine?.Mods.ToDictionary(info => info.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, ModInfo>(StringComparer.Ordinal);

        var summaries = new List<ModSummary>(_stateStore.State.Mods.Count);
        foreach (var (modId, record) in _stateStore.State.Mods.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var hasInstall = _stateStore.TryOpenActiveInstallation(modId, out var installation);
            var runtime = runtimeById.GetValueOrDefault(modId);
            var version = installation?.Manifest.Version ?? record.ActiveInstallation?.Version ?? "0.0.0";
            var manifest = installation?.Manifest;

            string? iconPath = null;
            if (hasInstall && installation is not null && manifest?.Icon is not null)
            {
                var candidate = PackagePath.Resolve(installation.Directory, manifest.Icon);
                if (File.Exists(candidate))
                {
                    iconPath = candidate;
                }
            }

            summaries.Add(
                new ModSummary
                {
                    Id = modId,
                    Version = version,
                    SourceId = record.SourceId,
                    IsEnabled = record.IsEnabled,
                    Intent = record.Intent,
                    State = runtime?.State ?? ModState.Inactive,
                    SkipReason = runtime?.SkipReason,
                    Name = manifest?.Name ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Description = manifest?.Description ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Readme = manifest?.Readme ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    Author = manifest?.Author,
                    IconPath = iconPath,
                    Homepage = manifest?.Homepage,
                    Repository = manifest?.Repository,
                    License = manifest?.License,
                    HasRollback = record.PreviousInstallation is not null,
                    PreviousVersion = record.PreviousInstallation?.Version,
                    PendingOperation = record.PendingOperation,
                    LocalDirectoryPath = record.LocalDirectoryPath,
                    PreferredChannel = record.PreferredChannel,
                    PinnedVersion = record.PinnedVersion,
                    Diagnostics = _modDiagnostics.TryGetValue(modId, out var diagnostics) ? [.. diagnostics] : []
                });
        }

        Volatile.Write(ref _installedSnapshots, BuildInstalledSnapshotsLocked());
        Volatile.Write(ref _installedMods, summaries.ToArray());
    }

    private void RecordApplyResult(string operation, ModApplyResult result)
    {
        var logger = Log.ForContext<ModManager>();
        logger.Information("Mod {Operation}: {Status}; active mods: {Mods}", operation, result.Status, result.ActiveModIds);

        _modDiagnostics.Clear();
        RecordDiagnostics(result.Diagnostics);
        if (result.Status == ModApplyStatus.RestartRequired)
        {
            Volatile.Write(ref _restartDiagnostics, [.. result.Diagnostics]);
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            logger.Warning("Mod {ModId} {Code}: {Detail}", diagnostic.ModId, diagnostic.Code, diagnostic.Detail);
        }

        if (_engine is not null)
        {
            foreach (var mod in _engine.Mods.Where(mod => mod.SkipReason is not null))
            {
                logger.Information("Mod {ModId} skipped: {Reason}.", mod.Id, mod.SkipReason);
            }
        }
    }

    private void RecordDiagnostics(IReadOnlyList<ModDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.ModId is not { Length: > 0 } modId)
            {
                continue;
            }

            if (!_modDiagnostics.TryGetValue(modId, out var list))
            {
                list = [];
                _modDiagnostics.Add(modId, list);
            }

            list.Add(diagnostic);
        }
    }

    private void CleanupImported(IEnumerable<ModInstallation> installations)
    {
        foreach (var installation in installations)
        {
            _packageStore.TryRemove(installation);
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string GenerateSourceId(string prefix, string value)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return $"{prefix}.{hash}";
    }

    private static ModOperationResult CreatePlanResult(PlannedOperation plan)
    {
        if (plan.PendingLicenses.Count > 0 || plan.AffectedDependencies.Count > 0)
        {
            return new ModOperationResult
            {
                Status = ModOperationStatus.ConfirmationRequired,
                AffectedDependencies = plan.AffectedDependencies,
                PendingLicenses = plan.PendingLicenses,
                SolveResult = plan.SolveResult,
                Message = plan.PendingLicenses.Count > 0
                    ? "One or more selected mods require explicit license acceptance before installation."
                    : $"Proceeding requires enabling dependency mod(s): {string.Join(", ", plan.AffectedDependencies)}."
            };
        }

        return new ModOperationResult
        {
            Status = ModOperationStatus.Applied,
            SolveResult = plan.SolveResult
        };
    }

    private static ModOperationResult FromApplyResult(ModApplyResult applyResult, ModSolveResult? solveResult = null)
    {
        var status = applyResult.Status switch
        {
            ModApplyStatus.Applied => ModOperationStatus.Applied,
            ModApplyStatus.RestartRequired => ModOperationStatus.RestartRequired,
            _ => ModOperationStatus.CompletedWithErrors
        };

        return new ModOperationResult
        {
            Status = status,
            Diagnostics = applyResult.Diagnostics,
            SolveResult = solveResult
        };
    }

    private static ModOperationResult Failed(string message) =>
        new()
        {
            Status = ModOperationStatus.Failed,
            Message = message
        };

    private ModOperationResult RestartRequiredResult() =>
        new()
        {
            Status = ModOperationStatus.RestartRequired,
            Diagnostics = RestartDiagnostics,
            Message = "A restart is required before further mod activations or removals can take effect."
        };

    private sealed record PlannedOperation(
        ModSolveResult? SolveResult,
        IReadOnlyList<ModLicenseRequirement> PendingLicenses,
        IReadOnlyList<string> AffectedDependencies,
        ModOperationResult? ImmediateFailure
    );
}
