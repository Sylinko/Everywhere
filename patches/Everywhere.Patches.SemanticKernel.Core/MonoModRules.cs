using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.InlineRT;

namespace MonoMod;

/// <summary>
/// Requires review on SDK updates and changes one cache call without copying the parameter converter.
/// </summary>
internal static class MonoModRules
{
    static MonoModRules()
    {
        var module = MonoModRulesManager.Modder.Module;
        if (module.Assembly.Name.Version != new Version(1, 75, 0, 0))
        {
            throw new InvalidOperationException("Review the SK parameter-converter cache patch before updating Semantic Kernel.");
        }

        MonoModRulesManager.Modder.PostProcessors += modder =>
        {
            var type = modder.Module.GetType("Microsoft.SemanticKernel.KernelFunctionFromMethod");
            var converter = type.Methods.Single(method => method.Name == "GetConverter");
            var calls = converter.Body.Instructions.Where(instruction =>
                instruction.OpCode == OpCodes.Callvirt &&
                instruction.Operand is MethodReference { Name: "GetOrAdd", Parameters.Count: 2 } method &&
                method.DeclaringType.FullName.StartsWith("System.Collections.Concurrent.ConcurrentDictionary`2", StringComparison.Ordinal)).ToArray();
            if (calls.Length != 1)
            {
                throw new InvalidOperationException("SK changed GetConverter's cache call; review the weak-key cache patch.");
            }

            // The stack already contains the dictionary, key and unchanged upstream factory delegate.
            calls[0].OpCode = OpCodes.Call;
            calls[0].Operand = type.Methods.Single(method => method.Name == "GetOrAddParser");
        };
    }
}