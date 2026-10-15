using MonoMod.InlineRT;

namespace MonoMod;

/// <summary>
/// Requires review when the SK version or the proxy's execution entry points change.
/// </summary>
internal static class MonoModRules
{
    static MonoModRules()
    {
        var module = MonoModRulesManager.Modder.Module;
        if (module.Assembly.Name.Version != new Version(1, 75, 0, 0))
        {
            throw new InvalidOperationException("Review the revocable function and schema patches before updating Semantic Kernel.");
        }

        var type = module.GetType("Microsoft.SemanticKernel.KernelFunction");
        var invoke = type.Methods.SingleOrDefault(method => method.Name == "InvokeAsync" && !method.HasGenericParameters);
        var stream = type.Methods.SingleOrDefault(method => method.Name == "InvokeStreamingAsync" && method.HasGenericParameters);
        if (invoke is not { IsPublic: true, Parameters.Count: 3 } || stream is not { IsPublic: true, Parameters.Count: 3 } ||
            invoke.Parameters[0].ParameterType.FullName != "Microsoft.SemanticKernel.Kernel" ||
            stream.Parameters[0].ParameterType.FullName != "Microsoft.SemanticKernel.Kernel" ||
            type.Methods.Count(method => method.Name == "InvokeCoreAsync") != 2)
        {
            throw new InvalidOperationException("SK changed its invocation entry points; review the revocable function patch.");
        }
    }
}