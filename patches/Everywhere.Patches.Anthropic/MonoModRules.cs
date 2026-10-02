using MonoMod.InlineRT;

namespace MonoMod;

/// <summary>Rejects SDK upgrades until the copied request loop has been reviewed.</summary>
internal static class MonoModRules
{
    static MonoModRules()
    {
        var module = MonoModRulesManager.Modder.Module;
        if (module.Assembly.Name.Version != new Version(12, 45, 0, 0))
            throw new InvalidOperationException("Review the complete Anthropic Execute<T> implementation before updating this patch.");

        var client = module.GetType("Anthropic.AnthropicClientWithRawResponse") ??
            throw new InvalidOperationException("The Anthropic patch target was not found.");

        var execute = client.Methods.SingleOrDefault(method => method.Name == "Execute");
        if (execute is null || execute.GenericParameters.Count != 1 || execute.Parameters.Count != 2 ||
            execute.Parameters[^1].ParameterType.FullName != "System.Threading.CancellationToken")
            throw new InvalidOperationException("Anthropic changed Execute<T>; review the donor before building.");

        // MonoModReplace copies the donor's flags. Preserve the original implicit interface
        // implementation flags as well as its full body; otherwise the SDK cannot load.
        var attributes = execute.Attributes;
        MonoModRulesManager.Modder.PostProcessors += modder =>
            modder.Module.GetType(client.FullName).Methods.Single(method => method.Name == "Execute").Attributes = attributes;
    }
}