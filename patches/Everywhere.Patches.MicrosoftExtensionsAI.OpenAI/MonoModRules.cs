using MonoMod.InlineRT;

namespace MonoMod;

/// <summary>Requires review when an SDK update changes the patched parser contract.</summary>
internal static class MonoModRules
{
    static MonoModRules()
    {
        var module = MonoModRulesManager.Modder.Module;
        if (module.Assembly.Name.Version != new Version(10, 9, 0, 0))
            throw new InvalidOperationException("Review the complete MEAI Responses streaming conversion before updating this patch.");

        var extensions = module.GetType("Microsoft.Extensions.AI.OpenAIClientExtensions") ??
            throw new InvalidOperationException("The MEAI OpenAI patch target was not found.");

        var method = extensions.Methods.SingleOrDefault(method => method.Name == "ParseCallContent" &&
            method.Parameters.Count == 3 && method.Parameters.All(parameter => parameter.ParameterType.FullName == "System.String"));
        if (method is null || !method.IsStatic || !method.IsAssembly ||
            method.ReturnType.FullName != "Microsoft.Extensions.AI.FunctionCallContent")
            throw new InvalidOperationException(
                "MEAI changed the Responses streaming call parser; review the patch against the new SDK before building.");

        var client = module.GetType("Microsoft.Extensions.AI.OpenAIResponsesChatClient") ??
            throw new InvalidOperationException("The MEAI Responses conversion target was not found.");

        var stream = client.Methods.SingleOrDefault(m => m.Name == "FromOpenAIStreamingResponseUpdatesAsync");
        if (stream is null || !stream.IsStatic || stream.Parameters.Count != 5 ||
            stream.Parameters[^1].ParameterType.FullName != "System.Threading.CancellationToken")
            throw new InvalidOperationException("MEAI changed the Responses conversion signature; review the donor before building.");
    }
}