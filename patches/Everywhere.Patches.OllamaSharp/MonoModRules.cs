using MonoMod.InlineRT;

namespace MonoMod;

/// <summary>Fails weaving when an SDK update invalidates the donor's private contracts.</summary>
internal static class MonoModRules
{
    static MonoModRules()
    {
        var client = MonoModRulesManager.Modder.Module.GetType("OllamaSharp.OllamaApiClient") ??
            throw new InvalidOperationException("The OllamaSharp patch target was not found.");

        foreach (var name in new[] { "ProcessStreamedChatResponseAsync", "ProcessStreamedCompletionResponseAsync", "ProcessStreamedResponseAsync", "SendToOllamaAsync" })
        {
            var method = client.Methods.SingleOrDefault(method => method.Name == name);
            var count = name == "SendToOllamaAsync" ? 4 : 2;
            if (method is null || method.Parameters.Count != count ||
                method.Parameters[0].ParameterType.FullName != (count == 4 ? "System.Net.Http.HttpRequestMessage" : "System.Net.Http.HttpResponseMessage") ||
                method.Parameters[^1].ParameterType.FullName != "System.Threading.CancellationToken")
                throw new InvalidOperationException($"OllamaSharp changed {name}; review the patch against the new SDK before building.");
        }

        if (!client.Fields.Any(field => field.Name == "_client" && field.FieldType.FullName == "System.Net.Http.HttpClient"))
            throw new InvalidOperationException("OllamaSharp changed HttpClient ownership; review the patch before building.");
    }
}