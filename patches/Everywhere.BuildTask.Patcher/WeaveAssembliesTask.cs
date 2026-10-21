using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using MonoMod;
using Task = Microsoft.Build.Utilities.Task;

namespace Everywhere.BuildTask.Patcher;

public class WeaveAssembliesTask : Task
{
    [Required]
    public ITaskItem[] References { get; set; } = [];

    /// <summary>
    /// Implementation assemblies selected by the SDK for the current build.
    /// </summary>
    [Required]
    public ITaskItem[] RuntimeAssemblies { get; set; } = [];

    [Required]
    public ITaskItem[] PatchAssemblies { get; set; } = [];

    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] RemovedReferences { get; set; } = [];

    [Output]
    public ITaskItem[] AddedReferences { get; set; } = [];

    public override bool Execute()
    {
        try
        {
            if (!Directory.Exists(OutputDirectory))
            {
                Directory.CreateDirectory(OutputDirectory);
            }

            // Map target assembly name → patch DLL path, using PatchTargets metadata
            var patchMap = new Dictionary<string, ITaskItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var patchAssembly in PatchAssemblies)
            {
                var patchTargets = patchAssembly.GetMetadata("PatchTargets");
                if (string.IsNullOrWhiteSpace(patchTargets))
                {
                    Log.LogWarning($"PatchAssembly '{patchAssembly.ItemSpec}' has no PatchTargets metadata, skipping.");
                    continue;
                }

                foreach (var target in patchTargets.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    patchMap[target] = patchAssembly;
                }
            }

            var removed = new List<ITaskItem>();
            var added = new List<ITaskItem>();

            // Runtime assets have already been selected for the current TFM/RID and
            // conflict-resolved by the SDK. Never infer implementation paths from ref/ paths.
            var runtimeAssemblies = RuntimeAssemblies
                .Where(item => string.Equals(Path.GetExtension(item.ItemSpec), ".dll", StringComparison.OrdinalIgnoreCase))
                .DistinctBy(item => Path.GetFullPath(item.ItemSpec))
                .ToLookup(item => Path.GetFileNameWithoutExtension(item.ItemSpec), StringComparer.OrdinalIgnoreCase);
            var depDirs = RuntimeAssemblies
                .Concat(References)
                .Concat(PatchAssemblies)
                .Select(item => Path.GetDirectoryName(item.ItemSpec))
                .OfType<string>()
                .Where(Directory.Exists)
                .Distinct()
                .ToArray();

            foreach (var reference in References)
            {
                var refName = Path.GetFileNameWithoutExtension(reference.ItemSpec);
                if (patchMap.TryGetValue(refName, out var patchAssembly))
                {
                    var patchDllPath = patchAssembly.ItemSpec;
                    var frameworkName = reference.GetMetadata("FrameworkReferenceName");
                    if (!string.IsNullOrEmpty(frameworkName))
                    {
                        // Platform SDK native registration tables may contain metadata tokens
                        // from the original assembly. Cecil writing does not preserve those IDs.
                        Log.LogError($"Weaving framework reference '{frameworkName}' is not supported: native SDK components may depend on stable metadata tokens.");
                        continue;
                    }
                    var candidates = runtimeAssemblies[refName].ToArray();
                    if (candidates.Length != 1)
                    {
                        Log.LogError($"Expected one SDK-selected runtime assembly for '{refName}', found {candidates.Length}: " +
                            string.Join(", ", candidates.Select(item => item.ItemSpec)));
                        continue;
                    }

                    var runtimeAssembly = candidates[0];
                    var inputDllPath = runtimeAssembly.ItemSpec;
                    var referenceIdentity = AssemblyName.GetAssemblyName(reference.ItemSpec);
                    var runtimeIdentity = AssemblyName.GetAssemblyName(inputDllPath);
                    if (!string.Equals(referenceIdentity.FullName, runtimeIdentity.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.LogError($"Runtime assembly identity '{runtimeIdentity}' does not match reference '{referenceIdentity}'.");
                        continue;
                    }

                    var outputDllPath = Path.Combine(OutputDirectory, Path.GetFileName(reference.ItemSpec));

                    Log.LogMessage(MessageImportance.High, $"[Patcher] Intercepting: {refName}");
                    Log.LogMessage(MessageImportance.Normal, $"[Patcher] Base DLL: {inputDllPath}");
                    Log.LogMessage(MessageImportance.Normal, $"[Patcher] Patch DLL: {patchDllPath}");

                    if (!File.Exists(inputDllPath))
                    {
                        Log.LogError($"Base DLL not found: {inputDllPath}");
                        continue;
                    }

                    if (!File.Exists(patchDllPath))
                    {
                        Log.LogError($"Patch DLL not found: {patchDllPath}");
                        continue;
                    }

                    var taskAssemblyPath = typeof(WeaveAssembliesTask).Assembly.Location;
                    var inputFingerprint = CreateInputFingerprint(inputDllPath, patchDllPath, taskAssemblyPath,
                        typeof(MonoModder).Assembly.Location, typeof(Mono.Cecil.ModuleDefinition).Assembly.Location);
                    if (IsOutputUpToDate(outputDllPath, inputFingerprint))
                    {
                        Log.LogMessage(MessageImportance.High, $"[Patcher] Up-to-date: {outputDllPath}");
                    }
                    else
                    {
                        using var modder = new MonoModder();
                        modder.InputPath = inputDllPath;
                        modder.OutputPath = outputDllPath;

                        foreach (var dir in depDirs)
                        {
                            modder.DependencyDirs.Add(dir);
                        }

                        modder.Read();
                        if (modder.Module.Assembly.CustomAttributes.Any(attribute =>
                                attribute.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
                        {
                            Log.LogError($"The SDK-selected patch input is a reference assembly: {inputDllPath}");
                            continue;
                        }
                        modder.MapDependencies();

                        modder.ReadMod(patchDllPath);
                        modder.MapDependencies();

                        var ruleAssemblyDirectories = depDirs
                            .Append(Path.GetDirectoryName(typeof(MonoModder).Assembly.Location))
                            .Where(path => !string.IsNullOrWhiteSpace(path))
                            .OfType<string>()
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        Assembly? ResolveRuleAssembly(AssemblyLoadContext _, AssemblyName assemblyName)
                        {
                            var alreadyLoaded = AppDomain.CurrentDomain
                                .GetAssemblies()
                                .FirstOrDefault(assembly =>
                                    string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
                            if (alreadyLoaded != null)
                            {
                                return alreadyLoaded;
                            }

                            foreach (var directory in ruleAssemblyDirectories)
                            {
                                var candidate = Path.Combine(directory, $"{assemblyName.Name}.dll");
                                if (File.Exists(candidate))
                                {
                                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                                }
                            }

                            return null;
                        }

                        AssemblyLoadContext.Default.Resolving += ResolveRuleAssembly;
                        try
                        {
                            modder.AutoPatch();
                        }
                        finally
                        {
                            AssemblyLoadContext.Default.Resolving -= ResolveRuleAssembly;
                        }
                        // A failed write must never leave a valid stamp beside a partial DLL.
                        File.Delete(outputDllPath + ".inputs");
                        modder.Write();
                        File.WriteAllText(outputDllPath + ".inputs", inputFingerprint);

                        Log.LogMessage(MessageImportance.High, $"[Patcher] Wrote patched DLL: {outputDllPath}");
                    }

                    removed.Add(CloneItem(runtimeAssembly));
                    added.Add(CloneItem(runtimeAssembly, outputDllPath));
                }
            }

            RemovedReferences = removed.ToArray();
            AddedReferences = added.ToArray();

            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    private static TaskItem CloneItem(ITaskItem item, string? itemSpec = null)
    {
        var clone = new TaskItem(itemSpec ?? item.ItemSpec);
        item.CopyMetadataTo(clone);
        return clone;
    }

    private static bool IsOutputUpToDate(string outputPath, string inputFingerprint)
    {
        var stampPath = outputPath + ".inputs";
        return File.Exists(outputPath) && File.Exists(stampPath) && File.ReadAllText(stampPath) == inputFingerprint;
    }

    private static string CreateInputFingerprint(params string[] inputPaths)
    {
        // Paths matter as well as timestamps: a TFM/RID or package change may select
        // a different implementation whose timestamp is older than the cached output.
        return string.Join("\n", inputPaths.Select(path =>
        {
            var file = new FileInfo(path);
            return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }));
    }
}
