using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Management;

/// <summary>
/// Manages atomic single-file persistence (<c>mods-state.json</c>), deferred cold-start operations, <c>N=1</c> rollback
/// slots, local development directory synchronization, default-enable tracking, and deferred directory cleanup.
/// </summary>
public sealed class ModStateStore
{
    /// <summary>
    /// Gets the canonical filename of the unified mod state file.
    /// </summary>
    public const string DefaultFileName = "mods-state.json";

    /// <summary>
    /// Gets the full path to <c>mods-state.json</c>.
    /// </summary>
    public string StateFilePath { get; }

    /// <summary>
    /// Gets the current in-memory state document. Call <see cref="Load"/> before reading persisted entries.
    /// </summary>
    public ModStateDocument State { get; private set; } = new();

    private readonly ModPackageStore _packageStore;

    /// <summary>
    /// Initializes a state store bound to <paramref name="stateFilePath"/> and <paramref name="packageStore"/>.
    /// </summary>
    public ModStateStore(string stateFilePath, ModPackageStore packageStore)
    {
        StateFilePath = Path.GetFullPath(stateFilePath);
        _packageStore = packageStore;
    }

    /// <summary>
    /// Loads <c>mods-state.json</c> from disk if present, or resets to an empty v1 state document.
    /// </summary>
    public void Load()
    {
        if (!File.Exists(StateFilePath))
        {
            State = new ModStateDocument();
            return;
        }

        using var stream = File.OpenRead(StateFilePath);
        var document = JsonSerializer.Deserialize(stream, HostingJsonSerializerContext.DefaultOptions.ModStateDocument);
        if (document is null || document.SchemaVersion != 1)
        {
            throw new ModValidationException("InvalidModState", $"Unsupported or empty mod state document at '{StateFilePath}'.");
        }

        State = new ModStateDocument
        {
            SchemaVersion = document.SchemaVersion,
            Sources = document.Sources,
            Mods = new Dictionary<string, ModStateRecord>(document.Mods, StringComparer.Ordinal),
            SeenDefaultModIds = new HashSet<string>(document.SeenDefaultModIds, StringComparer.Ordinal),
            RetiredDirectories = new HashSet<string>(document.RetiredDirectories, StringComparer.Ordinal),
            RetiredDataModIds = new HashSet<string>(document.RetiredDataModIds, StringComparer.Ordinal)
        };
    }

    /// <summary>
    /// Writes <see cref="State"/> atomically via a temporary sibling file.
    /// </summary>
    public void Save()
    {
        var directory = Path.GetDirectoryName(StateFilePath) ?? ".";
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{StateFilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, State, HostingJsonSerializerContext.DefaultOptions.ModStateDocument);
            }

            File.Move(temporaryPath, StateFilePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    /// <summary>
    /// Resolves a stored <see cref="ModInstallationSlot"/> to an absolute path under <see cref="ModPackageStore.RootDirectory"/>.
    /// </summary>
    public string ResolveSlotDirectory(ModInstallationSlot slot) => ResolveRelativePackagePath(slot.RelativeDirectory);

    /// <summary>
    /// Attempts to reopen the currently active cached installation for <paramref name="modId"/>.
    /// </summary>
    public bool TryOpenActiveInstallation(string modId, [NotNullWhen(true)] out ModInstallation? installation)
    {
        if (!State.Mods.TryGetValue(modId, out var record) || record.ActiveInstallation is not { } slot)
        {
            installation = null;
            return false;
        }

        var path = ResolveRelativePackagePath(slot.RelativeDirectory);
        return _packageStore.TryOpen(path, out installation);
    }

    /// <summary>
    /// Runs cold-start reconciliation before activating any mods: sweeps retired directories, commits pending
    /// restart operations, synchronizes changed unpacked development folders, and persists updated state.
    /// </summary>
    public async Task<IReadOnlyList<ModDiagnostic>> ReconcileStartupAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<ModDiagnostic>();
        var isDirty = SweepRetiredDirectories() || SweepRetiredDataDirectories();

        foreach (var (modId, record) in State.Mods.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (record.PendingOperation is { } pending)
            {
                switch (pending.Kind)
                {
                    case ModPendingOperationKind.Uninstall:
                    {
                        RetireSlot(record.ActiveInstallation);
                        RetireSlot(record.PreviousInstallation);
                        RetireSlot(pending.TargetInstallation);
                        if (pending.ShouldDeleteData)
                        {
                            RetireDataDirectory(modId);
                        }

                        State.Mods.Remove(modId);
                        isDirty = true;
                        continue;
                    }
                    case ModPendingOperationKind.Update when pending.TargetInstallation is { } targetSlot:
                    {
                        var targetPath = ResolveRelativePackagePath(targetSlot.RelativeDirectory);
                        if (_packageStore.TryOpen(targetPath, out _))
                        {
                            RetireSlot(record.PreviousInstallation);
                            record.PreviousInstallation = record.ActiveInstallation;
                            record.ActiveInstallation = targetSlot;
                        }
                        else
                        {
                            RetireSlot(targetSlot);
                            diagnostics.Add(
                                new ModDiagnostic
                                {
                                    ModId = modId,
                                    Code = "PendingUpdateMissing",
                                    Detail = $"Staged update installation '{targetSlot.RelativeDirectory}' could not be reopened."
                                });
                        }

                        record.PendingOperation = null;
                        isDirty = true;
                        break;
                    }
                }
            }

            if (record.PreviousInstallation is { } previousSlot &&
                !_packageStore.TryOpen(ResolveRelativePackagePath(previousSlot.RelativeDirectory), out _))
            {
                RetireSlot(previousSlot);
                record.PreviousInstallation = null;
                isDirty = true;
            }

            if (record.LocalDirectoryPath is not { } devPath) continue;

            if (!Directory.Exists(devPath))
            {
                diagnostics.Add(
                    new ModDiagnostic
                    {
                        ModId = modId,
                        Code = "LocalDirectoryMissing",
                        Detail = $"Tracked development directory '{devPath}' was not found; keeping last cached package."
                    });
                continue;
            }

            try
            {
                var fingerprint = await ModPackageStore.ComputeDirectoryFingerprintAsync(devPath, cancellationToken).ConfigureAwait(false);
                var isCacheIntact = TryOpenActiveInstallation(modId, out _);
                if (!isCacheIntact || !string.Equals(fingerprint, record.LocalDirectoryFingerprint, StringComparison.Ordinal))
                {
                    var imported = await _packageStore.ImportDirectoryAsync(devPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(imported.Manifest.Id, modId, StringComparison.Ordinal))
                    {
                        _packageStore.TryRemove(imported);
                        diagnostics.Add(
                            new ModDiagnostic
                            {
                                ModId = modId,
                                Code = "LocalDirectoryIdMismatch",
                                Detail = $"Development directory '{devPath}' changed mod ID to '{imported.Manifest.Id}'."
                            });
                        continue;
                    }

                    RetireSlot(record.ActiveInstallation);
                    record.ActiveInstallation = CreateSlot(imported);
                    record.LocalDirectoryFingerprint = fingerprint;
                    isDirty = true;
                }
            }
            catch (Exception exception) when (exception is ModValidationException or IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(
                    new ModDiagnostic
                    {
                        ModId = modId,
                        Code = exception is ModValidationException validation ? validation.Code : "LocalDirectorySyncFailed",
                        Detail = exception.Message
                    });
            }
        }

        if (SweepRetiredDirectories() | SweepRetiredDataDirectories())
        {
            isDirty = true;
        }

        if (isDirty)
        {
            Save();
        }

        return diagnostics;
    }

    /// <summary>
    /// Records a newly imported or updated installation, maintaining the <c>N=1</c> rollback slot or queueing a pending
    /// cold-start swap when hot replacement cannot complete without restarting.
    /// </summary>
    public void RecordInstallOrUpdate(
        string modId,
        ModInstallation installation,
        string sourceId,
        ModInstallIntent intent,
        bool isRestartRequired = false,
        string? channel = null,
        string? pinnedVersion = null,
        string? localDirectoryPath = null,
        string? localDirectoryFingerprint = null
    )
    {
        var slot = CreateSlot(installation);
        if (!State.Mods.TryGetValue(modId, out var record))
        {
            record = new ModStateRecord
            {
                Id = modId,
                SourceId = sourceId,
                Intent = intent
            };
            State.Mods.Add(modId, record);
        }

        record.SourceId = sourceId;
        record.Intent = intent == ModInstallIntent.Explicit ? ModInstallIntent.Explicit : record.Intent;
        record.IsEnabled = true;

        if (channel is not null) record.PreferredChannel = channel;
        if (pinnedVersion is not null) record.PinnedVersion = pinnedVersion;
        if (localDirectoryPath is not null) record.LocalDirectoryPath = Path.GetFullPath(localDirectoryPath);
        if (localDirectoryFingerprint is not null) record.LocalDirectoryFingerprint = localDirectoryFingerprint;

        if (isRestartRequired && record.ActiveInstallation is not null)
        {
            if (record.PendingOperation?.TargetInstallation is { } previousPending)
            {
                RetireSlot(previousPending);
            }

            record.PendingOperation = new ModPendingOperation
            {
                Kind = ModPendingOperationKind.Update,
                TargetInstallation = slot
            };
        }
        else
        {
            if (record.PendingOperation?.TargetInstallation is { } previousPending)
            {
                RetireSlot(previousPending);
            }

            record.PendingOperation = null;

            var canKeepPreviousActive = sourceId != ModStateRecord.LocalDirectorySourceId &&
                record.ActiveInstallation is { } existingActive &&
                !string.Equals(existingActive.RelativeDirectory, slot.RelativeDirectory, StringComparison.Ordinal) &&
                _packageStore.TryOpen(ResolveRelativePackagePath(existingActive.RelativeDirectory), out _);

            if (!canKeepPreviousActive)
            {
                // Repeated dev rebuilds and cache repairs replace the slot directly instead of keeping a dead/duplicate rollback.
                RetireSlot(record.ActiveInstallation);
                RetireSlot(record.PreviousInstallation);
                record.PreviousInstallation = null;
            }
            else
            {
                RetireSlot(record.PreviousInstallation);
                record.PreviousInstallation = record.ActiveInstallation;
            }

            record.ActiveInstallation = slot;
        }

        State.RetiredDataModIds.Remove(modId);
        SweepRetiredDirectories();
        SweepRetiredDataDirectories();
        Save();
    }

    /// <summary>
    /// Rolls back <paramref name="modId"/> to its <c>N=1</c> prior installation slot immediately or at next startup.
    /// </summary>
    public bool TryRollback(string modId, bool isRestartRequired = false)
    {
        if (!State.Mods.TryGetValue(modId, out var record) || record.PreviousInstallation is not { } previous)
        {
            return false;
        }

        if (!_packageStore.TryOpen(ResolveRelativePackagePath(previous.RelativeDirectory), out _))
        {
            RetireSlot(previous);
            record.PreviousInstallation = null;
            SweepRetiredDirectories();
            SweepRetiredDataDirectories();
            Save();
            return false;
        }

        if (isRestartRequired)
        {
            if (record.PendingOperation?.TargetInstallation is { } previousPending)
            {
                RetireSlot(previousPending);
            }

            record.PendingOperation = new ModPendingOperation
            {
                Kind = ModPendingOperationKind.Update,
                TargetInstallation = previous
            };
            record.PreviousInstallation = null;
        }
        else
        {
            if (record.PendingOperation?.TargetInstallation is { } previousPending)
            {
                RetireSlot(previousPending);
            }

            RetireSlot(record.ActiveInstallation);
            record.ActiveInstallation = previous;
            record.PreviousInstallation = null;
            record.PendingOperation = null;
        }

        SweepRetiredDirectories();
        SweepRetiredDataDirectories();
        Save();
        return true;
    }

    /// <summary>
    /// Records an uninstall immediately or queues it for next cold startup when active owners could not unload cleanly.
    /// </summary>
    public void RecordUninstall(string modId, bool isRestartRequired = false, bool shouldDeleteData = false)
    {
        if (!State.Mods.TryGetValue(modId, out var record)) return;

        if (isRestartRequired)
        {
            if (record.PendingOperation?.TargetInstallation is { } previousPending)
            {
                RetireSlot(previousPending);
            }

            record.IsEnabled = false;
            record.PendingOperation = new ModPendingOperation
            {
                Kind = ModPendingOperationKind.Uninstall,
                ShouldDeleteData = shouldDeleteData
            };
        }
        else
        {
            RetireSlot(record.ActiveInstallation);
            RetireSlot(record.PreviousInstallation);
            RetireSlot(record.PendingOperation?.TargetInstallation);

            if (shouldDeleteData)
            {
                RetireDataDirectory(modId);
            }

            State.Mods.Remove(modId);
        }

        SweepRetiredDirectories();
        SweepRetiredDataDirectories();
        Save();
    }

    /// <summary>
    /// Returns default-enabled mods listed in <paramref name="index"/> that have never been processed before,
    /// recording them so user opt-outs are never overridden on subsequent launches.
    /// </summary>
    public IReadOnlyList<string> ClaimDefaultEnabledMods(ModIndex index)
    {
        var claimed = new List<string>();
        foreach (var entry in index.Mods)
        {
            if (entry.IsDefaultEnabled && State.SeenDefaultModIds.Add(entry.Id))
            {
                claimed.Add(entry.Id);
            }
        }

        if (claimed.Count > 0)
        {
            Save();
        }

        return claimed;
    }

    /// <summary>
    /// Determines whether explicit EULA/license confirmation is still required for the supplied license declaration.
    /// </summary>
    public static bool RequiresLicenseAcceptance(
        ModStateRecord? record,
        string? spdx,
        bool isAcceptanceRequired,
        string? licenseTextSha256 = null
    )
    {
        if (!isAcceptanceRequired || string.IsNullOrWhiteSpace(spdx)) return false;

        if (record?.AcceptedLicense is not { } accepted) return true;

        if (!string.Equals(accepted.Spdx, spdx, StringComparison.Ordinal)) return true;

        if (licenseTextSha256 is not null &&
            !string.Equals(accepted.TextSha256, licenseTextSha256, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records explicit user acceptance of a mod's license terms.
    /// </summary>
    public void RecordLicenseAcceptance(
        string modId,
        string spdx,
        string? licenseTextSha256 = null,
        long? acceptedAtUnixSeconds = null
    )
    {
        if (!State.Mods.TryGetValue(modId, out var record)) return;

        record.AcceptedLicense = new ModAcceptedLicense
        {
            Spdx = spdx,
            TextSha256 = licenseTextSha256,
            AcceptedAtUnixSeconds = acceptedAtUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        Save();
    }

    private ModInstallationSlot CreateSlot(ModInstallation installation)
    {
        var relative = Path.GetRelativePath(_packageStore.RootDirectory, installation.Directory).Replace('\\', '/');
        return new ModInstallationSlot
        {
            InstallationId = installation.InstallationId,
            Version = installation.Manifest.Version,
            RelativeDirectory = relative
        };
    }

    private string ResolveRelativePackagePath(string relativeDirectory)
    {
        PackagePath.ValidateRelative(relativeDirectory);
        return Path.GetFullPath(Path.Combine(_packageStore.RootDirectory, relativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
    }

    private void RetireSlot(ModInstallationSlot? slot)
    {
        if (slot is null) return;

        State.RetiredDirectories.Add(slot.RelativeDirectory);
    }

    private bool SweepRetiredDirectories()
    {
        if (State.RetiredDirectories.Count == 0) return false;

        var didChange = false;
        foreach (var relativeDirectory in State.RetiredDirectories.ToArray())
        {
            string fullPath;
            try
            {
                fullPath = ResolveRelativePackagePath(relativeDirectory);
            }
            catch (ModValidationException)
            {
                State.RetiredDirectories.Remove(relativeDirectory);
                didChange = true;
                continue;
            }

            if (_packageStore.TryRemoveDirectory(fullPath))
            {
                State.RetiredDirectories.Remove(relativeDirectory);
                didChange = true;
            }
        }

        return didChange;
    }

    private void RetireDataDirectory(string modId)
    {
        if (!ModValidationRules.IsValidIdentifier(modId)) return;

        State.RetiredDataModIds.Add(modId);
    }

    private bool SweepRetiredDataDirectories()
    {
        if (State.RetiredDataModIds.Count == 0) return false;

        var didChange = false;
        foreach (var modId in State.RetiredDataModIds.ToArray())
        {
            if (!ModValidationRules.IsValidIdentifier(modId))
            {
                State.RetiredDataModIds.Remove(modId);
                didChange = true;
                continue;
            }

            if (TryDeleteDataDirectory(modId))
            {
                State.RetiredDataModIds.Remove(modId);
                didChange = true;
            }
        }

        return didChange;
    }

    private bool TryDeleteDataDirectory(string modId)
    {
        if (!ModValidationRules.IsValidIdentifier(modId)) return true;

        var path = Path.Combine(_packageStore.DataDirectory, modId);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
