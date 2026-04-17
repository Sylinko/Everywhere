using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Mono.Cecil;
using MonoMod;
using Task = Microsoft.Build.Utilities.Task;

namespace Everywhere.BuildTask.Patcher;

public partial class WeaveAssembliesTask : Task
{
    [Required]
    public ITaskItem[] References { get; set; } = [];

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
            var patchMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
                    patchMap[target] = patchAssembly.ItemSpec;
                }
            }

            var removed = new List<ITaskItem>();
            var added = new List<ITaskItem>();

            // Gather dependency directories from references and patch assemblies.
            // Also include lib/ counterparts of ref/ directories, so MonoMod can
            // resolve runtime assemblies (ref/ assemblies contain only stubs).
            var depDirs = References
                .Select(r => Path.GetDirectoryName(r.ItemSpec))
                .Concat(PatchAssemblies.Select(p => Path.GetDirectoryName(p.ItemSpec)))
                .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
                .OfType<string>()
                .Distinct()
                .ToList();

            // For any ref/ directory, also add the corresponding lib/ directory
            foreach (var dir in depDirs.ToList())
            {
                var libDir = TryGetLibDirectory(dir);
                if (libDir != null && !depDirs.Contains(libDir))
                {
                    depDirs.Add(libDir);
                }
            }

            foreach (var reference in References)
            {
                var refName = Path.GetFileNameWithoutExtension(reference.ItemSpec);
                if (patchMap.TryGetValue(refName, out var patchDllPath))
                {
                    // If the reference points to a ref/ assembly (e.g., NuGet ref/net8.0/),
                    // resolve the corresponding lib/ assembly instead. Ref assemblies contain
                    // stub method bodies and may include injected dummy methods (e.g., Avalonia's
                    // NotClientImplementable enforcement) that break runtime behavior.
                    var inputDllPath = ResolveLibAssembly(reference.ItemSpec);

                    var outputDllPath = Path.Combine(OutputDirectory, Path.GetFileName(reference.ItemSpec));

                    Log.LogMessage(MessageImportance.High, $"[Patcher] Intercepting: {refName}");
                    Log.LogMessage(MessageImportance.Normal, $"[Patcher] Base DLL: {inputDllPath}");
                    Log.LogMessage(MessageImportance.Normal, $"[Patcher] Patch DLL: {patchDllPath}");

                    if (!File.Exists(inputDllPath))
                    {
                        Log.LogError($"Base DLL not found: {inputDllPath}");
                        continue;
                    }

                    using var modder = new MonoModder();
                    modder.InputPath = inputDllPath;
                    modder.OutputPath = outputDllPath;

                    foreach (var dir in depDirs)
                    {
                        modder.DependencyDirs.Add(dir);
                    }

                    modder.Read();
                    modder.MapDependencies();

                    modder.ReadMod(patchDllPath);
                    modder.MapDependencies();

                    modder.AutoPatch();
                    modder.Write();

                    Log.LogMessage(MessageImportance.High, $"[Patcher] Wrote patched DLL: {outputDllPath}");

                    removed.Add(reference);
                    var newItem = new TaskItem(outputDllPath);
                    reference.CopyMetadataTo(newItem); // Keep original metadata
                    added.Add(newItem);
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

    /// <summary>
    /// If <paramref name="refAssemblyPath"/> is inside a NuGet <c>ref/</c> folder,
    /// returns the corresponding <c>lib/</c> assembly path. Otherwise returns the
    /// original path unchanged.
    /// </summary>
    private string ResolveLibAssembly(string refAssemblyPath)
    {
        if (!RefAssemblyPathRegex().IsMatch(refAssemblyPath))
            return refAssemblyPath;

        var sep = Path.DirectorySeparatorChar;
        var refSeg = $"{sep}ref{sep}";
        var libPath = refAssemblyPath.Replace(refSeg, $"{sep}lib{sep}");

        if (File.Exists(libPath))
        {
            Log.LogMessage(MessageImportance.High,
                $"[Patcher] Resolved ref/ \u2192 lib/ assembly: {Path.GetFileName(refAssemblyPath)}");
            return libPath;
        }

        Log.LogWarning($"[Patcher] ref/ assembly detected but no lib/ counterpart found: {refAssemblyPath}");
        return refAssemblyPath;
    }

    /// <summary>
    /// If <paramref name="dir"/> is a NuGet <c>ref/tfm</c> directory,
    /// returns the corresponding <c>lib/tfm</c> directory path.
    /// </summary>
    private static string? TryGetLibDirectory(string dir)
    {
        var sep = Path.DirectorySeparatorChar;
        var refSeg = $"{sep}ref{sep}";
        var idx = dir.LastIndexOf(refSeg, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var libDir = string.Concat(dir.AsSpan(0, idx), $"{sep}lib{sep}", dir.AsSpan(idx + refSeg.Length));
        return Directory.Exists(libDir) ? libDir : null;
    }

    /// <summary>
    /// Mono.Cecil doesn't guarantee alignment of FieldRVA data when writing assemblies.
    /// This causes runtime failures when the data is accessed via
    /// <c>RuntimeHelpers.CreateSpan&lt;T&gt;()</c>, which requires the data address to be
    /// aligned to <c>sizeof(T)</c>. Fix: pad each field's <see cref="FieldDefinition.InitialValue"/>
    /// to a multiple of 8 bytes so that successive fields stay aligned within the data section.
    /// The runtime reads based on the field type's ClassSize, so the trailing padding bytes
    /// are never accessed.
    /// </summary>
    private static void EnsureFieldRvaAlignment(ModuleDefinition module)
    {
        const int alignment = 8;
        foreach (var type in module.Types)
        {
            foreach (var field in type.Fields)
            {
                if (field.InitialValue is not { Length: > 0 }) continue;
                var remainder = field.InitialValue.Length % alignment;
                if (remainder == 0) continue;
                var padding = alignment - remainder;
                var padded = new byte[field.InitialValue.Length + padding];
                Buffer.BlockCopy(field.InitialValue, 0, padded, 0, field.InitialValue.Length);
                field.InitialValue = padded;
            }
        }
    }

    // Regex to match NuGet package paths containing a ref/ segment:
    //   .../packages/packageName/version/ref/tfm/Assembly.dll
    [GeneratedRegex(@"[/\\]ref[/\\][^/\\]+[/\\]", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex RefAssemblyPathRegex();
}
