using System.Diagnostics.CodeAnalysis;
using Everywhere.Common;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Resolution;

/// <summary>
/// Solves cross-source mod dependencies using finite-domain constraint propagation, conflict-driven clause learning
/// (CDCL No-Goods), and non-chronological backjumping, while enforcing host compatibility, optionality, and source stickiness.
/// </summary>
public static class ModDependencySolver
{
    private const int MaxConflicts = 5000;

    /// <summary>
    /// Resolves a compatible, acyclic set of packages satisfying all active mods and explicit targets.
    /// Optional dependencies (<see cref="ModDependency.IsOptional"/>) are never auto-installed and never fail resolution.
    /// </summary>
    public static ModSolveResult Solve(ModSolveRequest request)
    {
        var host = request.Host;
        var hostVersion = ModCompatibility.ParseVersion(host.HostVersion);
        var modApiVersion = ModCompatibility.ParseVersion(host.ModApiVersion);

        var installedById = request.Installed.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var targetsById = BuildTargetMap(request.Targets);
        var sourcePriority = BuildSourcePriorityMap(request.Sources);

        var rootConstraints = new Dictionary<string, List<ActiveConstraint>>(StringComparer.Ordinal);
        var invalidClauses = new List<ModConflictClause>();

        foreach (var installed in request.Installed)
        {
            if (!installed.IsEnabled || request.ExcludedModIds.Contains(installed.Id) || targetsById.ContainsKey(installed.Id))
            {
                continue;
            }

            AddRootConstraint(rootConstraints, installed.Id, range: null, installed.SourceId);
        }

        foreach (var (targetId, target) in targetsById)
        {
            if (request.ExcludedModIds.Contains(targetId))
            {
                invalidClauses.Add(
                    new ModConflictClause(
                        ConsumerId: null,
                        ConsumerVersion: null,
                        DependencyId: targetId,
                        RequiredRange: target.VersionRange,
                        Reason: $"Target mod '{targetId}' is excluded from the active set."));
                continue;
            }

            SemanticVersionRange? parsedRange = null;
            if (target.VersionRange is not null && !SemanticVersionRange.TryParse(target.VersionRange, out parsedRange))
            {
                invalidClauses.Add(
                    new ModConflictClause(
                        ConsumerId: null,
                        ConsumerVersion: null,
                        DependencyId: targetId,
                        RequiredRange: target.VersionRange,
                        Reason: $"Invalid requested version range '{target.VersionRange}' for '{targetId}'."));
                continue;
            }

            AddRootConstraint(rootConstraints, targetId, parsedRange, target.PreferredSourceId);
        }

        if (invalidClauses.Count > 0)
        {
            return Unsat(invalidClauses);
        }

        var domains = new Dictionary<string, DomainBuildResult>(StringComparer.Ordinal);
        var state = new SolverState(request, installedById, targetsById, sourcePriority, host, hostVersion, modApiVersion, rootConstraints, domains);
        return state.Solve();
    }

    /// <summary>
    /// Computes all direct and transitive installed consumers that require <paramref name="targetModId"/>,
    /// ordered consumer-first so callers can confirm and deactivate dependents cleanly.
    /// </summary>
    public static IReadOnlyList<string> GetReverseDependents(
        string targetModId,
        IReadOnlyList<InstalledModSnapshot> installed,
        bool shouldIncludeDisabled = false
    )
    {
        var activeInstalled = installed
            .Where(item => shouldIncludeDisabled || item.IsEnabled)
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        if (!activeInstalled.ContainsKey(targetModId))
        {
            return [];
        }

        var reverseEdges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var item in activeInstalled.Values)
        {
            if (item.Entry is null) continue;

            foreach (var dependency in item.Entry.Dependencies)
            {
                if (dependency.IsOptional || !activeInstalled.ContainsKey(dependency.Id)) continue;

                if (!reverseEdges.TryGetValue(dependency.Id, out var consumers))
                {
                    consumers = [];
                    reverseEdges.Add(dependency.Id, consumers);
                }

                consumers.Add(item.Id);
            }
        }

        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (reverseEdges.TryGetValue(targetModId, out var direct))
        {
            foreach (var consumer in direct.Order(StringComparer.Ordinal))
            {
                if (consumer != targetModId && affected.Add(consumer))
                {
                    queue.Enqueue(consumer);
                }
            }
        }

        while (queue.TryDequeue(out var current))
        {
            if (!reverseEdges.TryGetValue(current, out var nextConsumers)) continue;

            foreach (var next in nextConsumers.Order(StringComparer.Ordinal))
            {
                if (next != targetModId && affected.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return OrderConsumersFirst(affected, activeInstalled);
    }

    /// <summary>
    /// Finds installed <see cref="ModInstallIntent.AutoDependency"/> mods that are no longer reachable from any remaining
    /// explicit installed mod after removing <paramref name="removingModIds"/>.
    /// </summary>
    public static IReadOnlyList<string> GetOrphanAutoDependencies(
        IReadOnlySet<string> removingModIds,
        IReadOnlyList<InstalledModSnapshot> installed
    )
    {
        var remaining = installed
            .Where(item => !removingModIds.Contains(item.Id))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);

        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var item in remaining.Values.Where(item => item.Intent == ModInstallIntent.Explicit))
        {
            if (reachable.Add(item.Id))
            {
                queue.Enqueue(item.Id);
            }
        }

        while (queue.TryDequeue(out var current))
        {
            if (!remaining.TryGetValue(current, out var mod) || mod.Entry is null) continue;

            foreach (var dependency in mod.Entry.Dependencies)
            {
                if (!dependency.IsOptional && remaining.ContainsKey(dependency.Id) && reachable.Add(dependency.Id))
                {
                    queue.Enqueue(dependency.Id);
                }
            }
        }

        var orphans = remaining.Values
            .Where(item => item.Intent == ModInstallIntent.AutoDependency && !reachable.Contains(item.Id))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);

        return OrderConsumersFirst(orphans, remaining);
    }

    private static IReadOnlyList<string> OrderConsumersFirst(
        IReadOnlySet<string> subset,
        IReadOnlyDictionary<string, InstalledModSnapshot> modsById
    )
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var dependencyFirst = new List<string>(subset.Count);

        foreach (var id in subset.Order(StringComparer.Ordinal))
        {
            Visit(id);
        }

        dependencyFirst.Reverse();
        return dependencyFirst;

        void Visit(string id)
        {
            if (!visited.Add(id)) return;

            if (modsById.TryGetValue(id, out var snapshot) && snapshot.Entry is not null)
            {
                foreach (var depId in snapshot.Entry.Dependencies
                             .Select(d => d.Id)
                             .Where(subset.Contains)
                             .Order(StringComparer.Ordinal))
                {
                    Visit(depId);
                }
            }

            dependencyFirst.Add(id);
        }
    }

    private static Dictionary<string, ModTargetRequirement> BuildTargetMap(IReadOnlyList<ModTargetRequirement> targets)
    {
        var map = new Dictionary<string, ModTargetRequirement>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            map[target.Id] = target;
        }

        return map;
    }

    private static Dictionary<string, int> BuildSourcePriorityMap(IReadOnlyList<ModSourceCatalogEntry> sources)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sources.Count; i++)
        {
            map.TryAdd(sources[i].SourceId, i);
        }

        return map;
    }

    private static void AddRootConstraint(
        Dictionary<string, List<ActiveConstraint>> rootConstraints,
        string modId,
        SemanticVersionRange? range,
        string? preferredSourceId
    )
    {
        if (!rootConstraints.TryGetValue(modId, out var list))
        {
            list = [];
            rootConstraints.Add(modId, list);
        }

        list.Add(
            new ActiveConstraint(
                ConsumerId: null,
                ConsumerVersion: null,
                Range: range,
                PreferredSourceId: preferredSourceId,
                ReasonLevels: []));
    }

    private static ModSolveResult Unsat(IReadOnlyList<ModConflictClause> clauses)
    {
        return new ModSolveResult
        {
            IsSuccess = false,
            Conflicts = clauses,
            FailureSummary = string.Join("; ", clauses.Select(FormatClause).Distinct(StringComparer.Ordinal))
        };
    }

    private static string FormatClause(ModConflictClause clause)
    {
        var origin = clause.ConsumerId is null
            ? "root requirement"
            : $"{clause.ConsumerId}@{clause.ConsumerVersion}";
        var range = clause.RequiredRange is null ? string.Empty : $" ({clause.RequiredRange})";
        return $"{origin} -> {clause.DependencyId}{range}: {clause.Reason}";
    }

    private sealed class SolverState(
        ModSolveRequest request,
        Dictionary<string, InstalledModSnapshot> installedById,
        Dictionary<string, ModTargetRequirement> targetsById,
        Dictionary<string, int> sourcePriority,
        ModHostEnvironment host,
        SemanticVersion hostVersion,
        SemanticVersion modApiVersion,
        Dictionary<string, List<ActiveConstraint>> rootConstraints,
        Dictionary<string, DomainBuildResult> domainCache
    )
    {
        private readonly List<Assignment> _trail = [];
        private readonly Dictionary<string, Assignment> _assigned = new(StringComparer.Ordinal);
        private readonly Dictionary<int, DecisionAtom> _decisions = [];
        private readonly List<LearnedNoGood> _noGoods = [];

        internal ModSolveResult Solve()
        {
            var currentLevel = 0;
            var conflictsCount = 0;

            while (true)
            {
                var (conflict, pendingRequirements) = Propagate(currentLevel);

                if (conflict is not null)
                {
                    conflictsCount++;
                    if (conflictsCount > MaxConflicts)
                    {
                        var limitClause = new ModConflictClause(
                            null,
                            null,
                            conflict.Clauses.FirstOrDefault()?.DependencyId ?? string.Empty,
                            null,
                            $"Solver exceeded {MaxConflicts} conflicts while resolving dependencies.");
                        return Unsat([limitClause, .. conflict.Clauses]);
                    }

                    var sortedLevels = conflict.DecisionLevels.Where(level => level > 0).Distinct().Order().ToArray();
                    if (sortedLevels.Length == 0)
                    {
                        return Unsat(conflict.Clauses);
                    }

                    var atoms = sortedLevels.Select(level => _decisions[level]).ToArray();
                    _noGoods.Add(new LearnedNoGood(atoms, conflict.Clauses));

                    var backjumpLevel = sortedLevels.Length >= 2 ? sortedLevels[^2] : 0;
                    BackjumpTo(backjumpLevel);
                    currentLevel = backjumpLevel;
                    continue;
                }

                var unassigned = ChooseBranchVariable(pendingRequirements);
                if (unassigned is null)
                {
                    var cycleConflict = FindActiveCycleConflict();
                    if (cycleConflict is not null)
                    {
                        conflictsCount++;
                        var sortedLevels = cycleConflict.DecisionLevels.Where(level => level > 0).Distinct().Order().ToArray();
                        if (sortedLevels.Length == 0)
                        {
                            return Unsat(cycleConflict.Clauses);
                        }

                        _noGoods.Add(new LearnedNoGood(sortedLevels.Select(level => _decisions[level]).ToArray(), cycleConflict.Clauses));
                        var backjumpLevel = sortedLevels.Length >= 2 ? sortedLevels[^2] : 0;
                        BackjumpTo(backjumpLevel);
                        currentLevel = backjumpLevel;
                        continue;
                    }

                    return BuildSolution();
                }

                var (nextModId, candidates) = unassigned.Value;
                currentLevel++;
                var chosen = candidates[0];
                _decisions[currentLevel] = new DecisionAtom(nextModId, chosen.Key);
                Assign(new Assignment(nextModId, chosen, currentLevel, [currentLevel]));
            }
        }

        private (ConflictState? Conflict, Dictionary<string, PendingRequirement> Pending) Propagate(int currentLevel)
        {
            while (true)
            {
                var pending = new Dictionary<string, PendingRequirement>(StringComparer.Ordinal);
                foreach (var (modId, constraints) in rootConstraints)
                {
                    foreach (var constraint in constraints)
                    {
                        AddPendingConstraint(pending, modId, constraint);
                    }
                }

                for (var i = 0; i < _trail.Count; i++)
                {
                    var assignment = _trail[i];
                    foreach (var dependency in assignment.Candidate.RequiredDependencies)
                    {
                        var constraint = new ActiveConstraint(
                            assignment.ModId,
                            assignment.Candidate.Version,
                            dependency.Range,
                            assignment.Candidate.SourceId,
                            assignment.ReasonLevels);
                        AddPendingConstraint(pending, dependency.Id, constraint);
                    }
                }

                // Verify no active learned No-Good is completely satisfied on the trail.
                foreach (var noGood in _noGoods)
                {
                    var hasAllAtoms = true;
                    var unionLevels = new HashSet<int>();
                    foreach (var atom in noGood.Atoms)
                    {
                        if (!_assigned.TryGetValue(atom.ModId, out var assignment) || assignment.Candidate.Key != atom.Key)
                        {
                            hasAllAtoms = false;
                            break;
                        }

                        unionLevels.UnionWith(assignment.ReasonLevels);
                    }

                    if (hasAllAtoms)
                    {
                        return (new ConflictState(unionLevels, noGood.Clauses), pending);
                    }
                }

                // Verify all already-assigned required dependencies satisfy their active constraints.
                foreach (var (depId, requirement) in pending)
                {
                    if (!_assigned.TryGetValue(depId, out var existing)) continue;

                    if (request.ExcludedModIds.Contains(depId))
                    {
                        var conflictLevels = new HashSet<int>(existing.ReasonLevels);
                        var clauses = new List<ModConflictClause>();
                        foreach (var constraint in requirement.Constraints)
                        {
                            conflictLevels.UnionWith(constraint.ReasonLevels);
                            clauses.Add(
                                new ModConflictClause(
                                    constraint.ConsumerId,
                                    constraint.ConsumerVersion,
                                    depId,
                                    constraint.Range?.ToString(),
                                    $"Dependency '{depId}' is excluded from the active set."));
                        }

                        return (new ConflictState(conflictLevels, clauses), pending);
                    }

                    foreach (var constraint in requirement.Constraints)
                    {
                        if (constraint.Range is not null && !constraint.Range.Contains(existing.Candidate.ParsedVersion))
                        {
                            var conflictLevels = new HashSet<int>(existing.ReasonLevels);
                            conflictLevels.UnionWith(constraint.ReasonLevels);
                            var clauses = BuildAssignmentRangeConflictClauses(depId, existing, requirement.Constraints, constraint);
                            return (new ConflictState(conflictLevels, clauses), pending);
                        }
                    }
                }

                var didPropagate = false;
                foreach (var (depId, requirement) in pending.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    if (_assigned.ContainsKey(depId)) continue;

                    var filterResult = FilterDomain(depId, requirement);
                    requirement.FilteredCandidates = filterResult.Remaining;

                    if (filterResult.Remaining.Count == 0)
                    {
                        return (new ConflictState(filterResult.ConflictLevels, filterResult.Clauses), pending);
                    }

                    if (filterResult.Remaining.Count == 1)
                    {
                        var unitCandidate = filterResult.Remaining[0];
                        Assign(new Assignment(depId, unitCandidate, currentLevel, filterResult.UnitReasonLevels));
                        didPropagate = true;
                        break;
                    }
                }

                if (!didPropagate)
                {
                    return (null, pending);
                }
            }
        }

        private DomainFilterResult FilterDomain(string modId, PendingRequirement requirement)
        {
            var baseDomain = GetOrBuildDomain(modId);
            if (request.ExcludedModIds.Contains(modId))
            {
                var excludedLevels = new HashSet<int>();
                var excludedClauses = new List<ModConflictClause>();
                foreach (var constraint in requirement.Constraints)
                {
                    excludedLevels.UnionWith(constraint.ReasonLevels);
                    excludedClauses.Add(
                        new ModConflictClause(
                            constraint.ConsumerId,
                            constraint.ConsumerVersion,
                            modId,
                            constraint.Range?.ToString(),
                            $"Mod '{modId}' is excluded from the active set."));
                }

                return new DomainFilterResult([], excludedLevels, excludedLevels, excludedClauses);
            }

            if (baseDomain.Candidates.Length == 0)
            {
                var emptyLevels = new HashSet<int>();
                var emptyClauses = new List<ModConflictClause>();
                foreach (var constraint in requirement.Constraints)
                {
                    emptyLevels.UnionWith(constraint.ReasonLevels);
                    emptyClauses.Add(
                        new ModConflictClause(
                            constraint.ConsumerId,
                            constraint.ConsumerVersion,
                            modId,
                            constraint.Range?.ToString(),
                            baseDomain.EmptyReason));
                }

                return new DomainFilterResult([], emptyLevels, emptyLevels, emptyClauses);
            }

            var eliminatingLevels = new HashSet<int>();
            var primaryReasonLevels = new HashSet<int>(requirement.Constraints[0].ReasonLevels);
            var remaining = new List<Candidate>(baseDomain.Candidates.Length);
            var noGoodEliminationCount = 0;

            foreach (var candidate in baseDomain.Candidates)
            {
                var isEliminated = false;

                foreach (var constraint in requirement.Constraints)
                {
                    if (constraint.Range is not null && !constraint.Range.Contains(candidate.ParsedVersion))
                    {
                        eliminatingLevels.UnionWith(constraint.ReasonLevels);
                        isEliminated = true;
                        break;
                    }
                }

                if (!isEliminated && TryFindBlockingNoGood(modId, candidate.Key, out var blockingLevels))
                {
                    eliminatingLevels.UnionWith(blockingLevels);
                    noGoodEliminationCount++;
                    isEliminated = true;
                }

                if (!isEliminated)
                {
                    remaining.Add(candidate);
                }
            }

            if (remaining.Count == 0)
            {
                var conflictLevels = new HashSet<int>(primaryReasonLevels);
                conflictLevels.UnionWith(eliminatingLevels);
                var clauses = BuildDomainWipeoutClauses(modId, requirement.Constraints, noGoodEliminationCount > 0);
                return new DomainFilterResult(remaining, conflictLevels, conflictLevels, clauses);
            }

            var orderedRemaining = OrderCandidatesForRequirement(remaining, requirement);
            var unitLevels = new HashSet<int>(primaryReasonLevels);
            unitLevels.UnionWith(eliminatingLevels);
            return new DomainFilterResult(orderedRemaining, eliminatingLevels, unitLevels, []);
        }

        private bool TryFindBlockingNoGood(string modId, CandidateKey key, out HashSet<int> blockingLevels)
        {
            foreach (var noGood in _noGoods)
            {
                var containsSelf = false;
                var allOthersActive = true;
                HashSet<int>? levels = null;

                foreach (var atom in noGood.Atoms)
                {
                    if (atom.ModId == modId)
                    {
                        if (atom.Key != key)
                        {
                            allOthersActive = false;
                            break;
                        }

                        containsSelf = true;
                        continue;
                    }

                    if (!_assigned.TryGetValue(atom.ModId, out var assignment) || assignment.Candidate.Key != atom.Key)
                    {
                        allOthersActive = false;
                        break;
                    }

                    levels ??= [];
                    levels.UnionWith(assignment.ReasonLevels);
                }

                if (containsSelf && allOthersActive)
                {
                    blockingLevels = levels ?? [];
                    return true;
                }
            }

            blockingLevels = [];
            return false;
        }

        private List<Candidate> OrderCandidatesForRequirement(List<Candidate> remaining, PendingRequirement requirement)
        {
            var preferredSources = requirement.Constraints
                .Select(c => c.PreferredSourceId)
                .Where(sourceId => sourceId is not null)
                .ToHashSet(StringComparer.Ordinal);

            if (preferredSources.Count == 0)
            {
                return remaining;
            }

            return
            [
                .. remaining
                    .OrderByDescending(candidate => candidate.IsAlreadyInstalledVersion && IsKeepingInstalledVersion(candidate.ModId))
                    .ThenByDescending(candidate => candidate.MatchesPreferredChannel)
                    .ThenByDescending(candidate => preferredSources.Contains(candidate.SourceId))
                    .ThenBy(candidate => sourcePriority.GetValueOrDefault(candidate.SourceId, int.MaxValue))
                    .ThenByDescending(candidate => candidate.ParsedVersion)
            ];
        }

        private bool IsKeepingInstalledVersion(string modId)
        {
            if (!request.ShouldKeepInstalledVersions) return false;
            return !targetsById.TryGetValue(modId, out var target) || target.VersionRange is null && target.PreferredChannel is null;
        }

        private (string ModId, List<Candidate> Candidates)? ChooseBranchVariable(Dictionary<string, PendingRequirement> pending)
        {
            (string ModId, List<Candidate> Candidates)? best = null;

            foreach (var (modId, requirement) in pending.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (_assigned.ContainsKey(modId) || requirement.FilteredCandidates is not { Count: > 1 } candidates)
                {
                    continue;
                }

                if (best is null || candidates.Count < best.Value.Candidates.Count)
                {
                    best = (modId, candidates);
                }
            }

            return best;
        }

        private ConflictState? FindActiveCycleConflict()
        {
            var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 = unvisited, 1 = visiting, 2 = visited
            var stack = new List<Assignment>();

            foreach (var assignment in _trail.OrderBy(item => item.ModId, StringComparer.Ordinal))
            {
                if (state.GetValueOrDefault(assignment.ModId) != 0) continue;

                var cycle = Visit(assignment);
                if (cycle is not null) return cycle;
            }

            return null;

            ConflictState? Visit(Assignment current)
            {
                state[current.ModId] = 1;
                stack.Add(current);

                foreach (var dependency in current.Candidate.RequiredDependencies)
                {
                    if (!_assigned.TryGetValue(dependency.Id, out var next)) continue;

                    var nextState = state.GetValueOrDefault(next.ModId);
                    if (nextState == 1)
                    {
                        var startIndex = stack.FindIndex(item => item.ModId == next.ModId);
                        var cycleItems = stack.Skip(startIndex).ToArray();
                        var levels = new HashSet<int>();
                        foreach (var item in cycleItems)
                        {
                            levels.UnionWith(item.ReasonLevels);
                        }

                        var cyclePath = string.Join(" -> ", cycleItems.Select(item => item.ModId).Append(next.ModId));
                        var clause = new ModConflictClause(
                            current.ModId,
                            current.Candidate.Version,
                            next.ModId,
                            dependency.Range.ToString(),
                            $"Circular dependency detected: {cyclePath}.");
                        return new ConflictState(levels, [clause]);
                    }

                    if (nextState == 0)
                    {
                        var nested = Visit(next);
                        if (nested is not null) return nested;
                    }
                }

                stack.RemoveAt(stack.Count - 1);
                state[current.ModId] = 2;
                return null;
            }
        }

        private ModSolveResult BuildSolution()
        {
            var ordered = new List<ResolvedModPackage>(_assigned.Count);
            var visited = new HashSet<string>(StringComparer.Ordinal);

            foreach (var modId in _assigned.Keys.Order(StringComparer.Ordinal))
            {
                Visit(modId);
            }

            return new ModSolveResult
            {
                IsSuccess = true,
                Packages = ordered
            };

            void Visit(string modId)
            {
                if (!visited.Add(modId)) return;

                var assignment = _assigned[modId];
                foreach (var depId in assignment.Candidate.AllDependencyIds
                             .Where(_assigned.ContainsKey)
                             .Order(StringComparer.Ordinal))
                {
                    Visit(depId);
                }

                var intent = DetermineIntent(modId);
                ordered.Add(
                    new ResolvedModPackage
                    {
                        Id = modId,
                        Version = assignment.Candidate.Version,
                        SourceId = assignment.Candidate.SourceId,
                        Intent = intent,
                        SelectedEntry = assignment.Candidate.SelectedEntry,
                        IndexEntry = assignment.Candidate.IndexEntry,
                        IndexVersion = assignment.Candidate.IndexVersion,
                        IsAlreadyInstalledVersion = assignment.Candidate.IsAlreadyInstalledVersion
                    });
            }
        }

        private ModInstallIntent DetermineIntent(string modId)
        {
            if (targetsById.TryGetValue(modId, out var target))
            {
                return target.Intent;
            }

            return installedById.TryGetValue(modId, out var installed)
                ? installed.Intent
                : ModInstallIntent.AutoDependency;
        }

        private DomainBuildResult GetOrBuildDomain(string modId)
        {
            if (domainCache.TryGetValue(modId, out var cached))
            {
                return cached;
            }

            var result = BuildDomain(modId);
            domainCache.Add(modId, result);
            return result;
        }

        private DomainBuildResult BuildDomain(string modId)
        {
            var installed = installedById.GetValueOrDefault(modId);
            targetsById.TryGetValue(modId, out var target);

            // Preserve source stickiness once installed unless an explicit target requests another source.
            var pinnedSourceId = target?.PreferredSourceId ?? installed?.SourceId;
            var preferredChannel = target?.PreferredChannel;

            var byKey = new Dictionary<CandidateKey, Candidate>();
            var hadAnyListedVersion = false;
            var hadHostIncompatible = false;

            if (installed is { Entry: { } installedEntry } &&
                SemanticVersion.TryParse(installed.Version, out var installedSemVer))
            {
                hadAnyListedVersion = true;
                var isSameSource = pinnedSourceId is null || pinnedSourceId == installed.SourceId;
                if (isSameSource)
                {
                    if (IsEntryHostCompatible(installedEntry))
                    {
                        if (TryCreateCandidate(
                                modId,
                                installed.SourceId,
                                installed.Version,
                                installedSemVer,
                                installedEntry,
                                indexEntry: null,
                                indexVersion: null,
                                isAlreadyInstalledVersion: true,
                                matchesPreferredChannel: false,
                                out var installedCandidate))
                        {
                            byKey[installedCandidate.Key] = installedCandidate;
                        }
                    }
                    else
                    {
                        hadHostIncompatible = true;
                    }
                }
            }

            foreach (var source in request.Sources)
            {
                if (pinnedSourceId is not null && source.SourceId != pinnedSourceId)
                {
                    continue;
                }

                var entry = source.Index.Mods.FirstOrDefault(item => item.Id == modId);
                if (entry is null) continue;

                string? preferredChannelVersion = null;
                if (preferredChannel is not null)
                {
                    entry.Channels.TryGetValue(preferredChannel, out preferredChannelVersion);
                }

                foreach (var version in entry.Versions)
                {
                    hadAnyListedVersion = true;
                    if (!SemanticVersion.TryParse(version.Version, out var parsedVersion)) continue;

                    var selectedEntry = ModPlatformSelector.Select(version.Entry, host.RuntimeIdentifier);
                    if (selectedEntry is null || !IsEntryHostCompatible(selectedEntry))
                    {
                        hadHostIncompatible = true;
                        continue;
                    }

                    var matchesChannel = preferredChannelVersion is not null &&
                        string.Equals(version.Version, preferredChannelVersion, StringComparison.Ordinal);
                    var isInstalledVersion = installed is not null &&
                        installed.SourceId == source.SourceId &&
                        SemanticVersion.TryParse(installed.Version, out var curSemVer) &&
                        curSemVer == parsedVersion;

                    if (TryCreateCandidate(
                            modId,
                            source.SourceId,
                            version.Version,
                            parsedVersion,
                            selectedEntry,
                            entry,
                            version,
                            isInstalledVersion,
                            matchesChannel,
                            out var candidate))
                    {
                        byKey[candidate.Key] = candidate;
                    }
                }
            }

            if (byKey.Count == 0)
            {
                var emptyReason = !hadAnyListedVersion
                    ? $"Mod '{modId}' was not found in any eligible source."
                    : hadHostIncompatible
                        ? $"No published version of '{modId}' is compatible with host '{host.RuntimeIdentifier}' ({host.HostFramework}, API {host.ModApiVersion}, host {host.HostVersion})."
                        : $"Mod '{modId}' has no valid installable versions.";
                return new DomainBuildResult([], emptyReason);
            }

            var keepInstalled = IsKeepingInstalledVersion(modId);
            var ordered = byKey.Values
                .OrderByDescending(candidate => keepInstalled && candidate.IsAlreadyInstalledVersion)
                .ThenByDescending(candidate => candidate.MatchesPreferredChannel)
                .ThenBy(candidate => sourcePriority.GetValueOrDefault(candidate.SourceId, int.MaxValue))
                .ThenByDescending(candidate => candidate.ParsedVersion)
                .ToArray();

            return new DomainBuildResult(ordered, string.Empty);
        }

        private bool IsEntryHostCompatible(UniversalModEntry entry)
        {
            if (!SemanticVersionRange.TryParse(entry.ModApiVersion, out var apiRange) || !apiRange.Contains(modApiVersion))
            {
                return false;
            }

            if (entry.HostVersion is { } hv &&
                (!SemanticVersionRange.TryParse(hv, out var hostRange) || !hostRange.Contains(hostVersion)))
            {
                return false;
            }

            return ModCompatibility.IsFrameworkCompatible(host.HostFramework, entry.Framework);
        }

        private static bool TryCreateCandidate(
            string modId,
            string sourceId,
            string version,
            SemanticVersion parsedVersion,
            UniversalModEntry selectedEntry,
            ModIndexEntry? indexEntry,
            ModIndexVersion? indexVersion,
            bool isAlreadyInstalledVersion,
            bool matchesPreferredChannel,
            [NotNullWhen(true)] out Candidate? candidate
        )
        {
            var requiredDeps = new List<RequiredDependency>();
            var allDeps = new List<string>(selectedEntry.Dependencies.Length);

            foreach (var dep in selectedEntry.Dependencies)
            {
                allDeps.Add(dep.Id);
                if (dep.IsOptional) continue;

                if (!SemanticVersionRange.TryParse(dep.Version, out var range))
                {
                    candidate = null;
                    return false;
                }

                requiredDeps.Add(new RequiredDependency(dep.Id, range));
            }

            candidate = new Candidate(
                new CandidateKey(sourceId, parsedVersion),
                modId,
                sourceId,
                version,
                parsedVersion,
                selectedEntry,
                indexEntry,
                indexVersion,
                isAlreadyInstalledVersion,
                matchesPreferredChannel,
                [.. requiredDeps],
                [.. allDeps]);
            return true;
        }

        private static List<ModConflictClause> BuildAssignmentRangeConflictClauses(
            string depId,
            Assignment assigned,
            IReadOnlyList<ActiveConstraint> allConstraints,
            ActiveConstraint violated
        )
        {
            var clauses = new List<ModConflictClause>();
            foreach (var constraint in allConstraints)
            {
                clauses.Add(
                    new ModConflictClause(
                        constraint.ConsumerId,
                        constraint.ConsumerVersion,
                        depId,
                        constraint.Range?.ToString(),
                        constraint == violated
                            ? $"Selected version '{assigned.Candidate.Version}' does not satisfy '{constraint.Range}'."
                            : $"Requires compatible '{depId}'."));
            }

            return clauses;
        }

        private static List<ModConflictClause> BuildDomainWipeoutClauses(
            string modId,
            IReadOnlyList<ActiveConstraint> constraints,
            bool hasNoGoodEliminations
        )
        {
            var clauses = new List<ModConflictClause>(constraints.Count);
            foreach (var constraint in constraints)
            {
                clauses.Add(
                    new ModConflictClause(
                        constraint.ConsumerId,
                        constraint.ConsumerVersion,
                        modId,
                        constraint.Range?.ToString(),
                        hasNoGoodEliminations
                            ? $"No candidate version of '{modId}' remains after excluding conflicting downstream dependencies."
                            : $"Combined constraints eliminate all host-compatible versions of '{modId}'."));
            }

            return clauses;
        }

        private void Assign(Assignment assignment)
        {
            _trail.Add(assignment);
            _assigned[assignment.ModId] = assignment;
        }

        private void BackjumpTo(int level)
        {
            while (_trail.Count > 0 && _trail[^1].Level > level)
            {
                var popped = _trail[^1];
                _trail.RemoveAt(_trail.Count - 1);
                _assigned.Remove(popped.ModId);
            }

            foreach (var key in _decisions.Keys.Where(decisionLevel => decisionLevel > level).ToArray())
            {
                _decisions.Remove(key);
            }
        }

        private static void AddPendingConstraint(
            Dictionary<string, PendingRequirement> pending,
            string modId,
            ActiveConstraint constraint
        )
        {
            if (!pending.TryGetValue(modId, out var requirement))
            {
                requirement = new PendingRequirement();
                pending.Add(modId, requirement);
            }

            requirement.Constraints.Add(constraint);
        }
    }

    private readonly record struct CandidateKey(string SourceId, SemanticVersion Version);

    private readonly record struct DecisionAtom(string ModId, CandidateKey Key);

    private sealed record RequiredDependency(string Id, SemanticVersionRange Range);

    private sealed record Candidate(
        CandidateKey Key,
        string ModId,
        string SourceId,
        string Version,
        SemanticVersion ParsedVersion,
        UniversalModEntry SelectedEntry,
        ModIndexEntry? IndexEntry,
        ModIndexVersion? IndexVersion,
        bool IsAlreadyInstalledVersion,
        bool MatchesPreferredChannel,
        RequiredDependency[] RequiredDependencies,
        string[] AllDependencyIds
    );

    private sealed record DomainBuildResult(Candidate[] Candidates, string EmptyReason);

    private sealed record Assignment(string ModId, Candidate Candidate, int Level, HashSet<int> ReasonLevels);

    private sealed record ActiveConstraint(
        string? ConsumerId,
        string? ConsumerVersion,
        SemanticVersionRange? Range,
        string? PreferredSourceId,
        HashSet<int> ReasonLevels
    );

    private sealed class PendingRequirement
    {
        internal List<ActiveConstraint> Constraints { get; } = [];

        internal List<Candidate>? FilteredCandidates { get; set; }
    }

    private sealed record DomainFilterResult(
        List<Candidate> Remaining,
        HashSet<int> ConflictLevels,
        HashSet<int> UnitReasonLevels,
        List<ModConflictClause> Clauses
    );

    private sealed record ConflictState(HashSet<int> DecisionLevels, IReadOnlyList<ModConflictClause> Clauses);

    private sealed record LearnedNoGood(DecisionAtom[] Atoms, IReadOnlyList<ModConflictClause> Clauses);
}
