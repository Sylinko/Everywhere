using System.ComponentModel;
using System.Text.RegularExpressions;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// Provides schema definitions and constants for model providers.
/// </summary>
[TypeConverter(typeof(FallbackEnumConverter))]
public enum ModelProviderSchema
{
    OpenAI,
    OpenAIResponses,
    Anthropic,
    Google,
    GoogleInteractions,
    Mistral,
    Ollama,
}

public static partial class ModelProviderSchemaExtensions
{
    /// <param name="schema"></param>
    extension(ModelProviderSchema schema)
    {
        /// <summary>
        /// Gets the default endpoint URL for the given model provider schema.
        /// </summary>
        public string GetDefaultEndpoint()
        {
            return schema switch
            {
                ModelProviderSchema.OpenAI => "https://api.openai.com/v1",
                ModelProviderSchema.OpenAIResponses => "https://api.openai.com/v1",
                ModelProviderSchema.Anthropic => "https://api.anthropic.com",
                ModelProviderSchema.Google => "https://generativelanguage.googleapis.com/v1beta",
                ModelProviderSchema.GoogleInteractions => "https://generativelanguage.googleapis.com/v1",
                ModelProviderSchema.Ollama => "http://localhost:11434",
                ModelProviderSchema.Mistral => "https://api.mistral.ai/v1",
                _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, null)
            };
        }

        /// <summary>
        /// Normalizes the given endpoint URL according to the schema's requirements.
        /// Endpoints ending with <c>#</c> are trimmed and returned verbatim without adding or rewriting version segments.
        /// For OpenAI / OpenAIResponses / Mistral: appends <c>/v1</c> when no path/version is present, and strips pasted resource suffixes.
        /// For Anthropic: strips pasted <c>/v1/messages</c> or bare <c>/v1</c> suffixes because the SDK appends <c>/v1/messages</c>.
        /// For Google (<c>generateContent</c>): defaults to <c>/v1beta</c> while preserving explicit versions and custom paths.
        /// For GoogleInteractions (<c>interactions</c>): defaults to <c>/v1</c> while preserving explicit versions and custom paths, and strips <c>/interactions</c>.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <returns></returns>
        public string? NormalizeEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return null;

            endpoint = endpoint.Trim().TrimEnd('/');

            // Don't modify explicit endpoints marked with '#'
            if (endpoint.EndsWith('#')) return endpoint[..^1].Trim().TrimEnd('/');

            var queryIndex = endpoint.IndexOf('?');
            var basePart = (queryIndex >= 0 ? endpoint[..queryIndex] : endpoint).TrimEnd('/');
            var queryPart = queryIndex >= 0 ? endpoint[queryIndex..] : string.Empty;

            switch (schema)
            {
                case ModelProviderSchema.OpenAI or ModelProviderSchema.Mistral:
                    basePart = StripTrailingSuffix(basePart, "/chat/completions");
                    break;
                case ModelProviderSchema.OpenAIResponses:
                    basePart = StripTrailingSuffix(basePart, "/responses");
                    break;
                case ModelProviderSchema.Anthropic:
                    basePart = StripTrailingSuffix(basePart, "/v1/messages");
                    basePart = StripTrailingSuffix(basePart, "/v1");
                    break;
                case ModelProviderSchema.Google:
                    basePart = StripTrailingSuffix(basePart, "/models");
                    break;
                case ModelProviderSchema.GoogleInteractions:
                    basePart = StripTrailingSuffix(basePart, "/interactions");
                    break;
                case ModelProviderSchema.Ollama:
                    basePart = StripTrailingSuffix(basePart, "/api/chat");
                    break;
            }

            // 1. Get prefix, version (v1, v2, v3, v1beta, etc.), and suffix (if any)
            var match = EndpointRegex().Match(basePart);
            if (!match.Success) return basePart + queryPart;

            var prefix = match.Groups["prefix"].Value;
            var version = match.Groups["version"].Value;
            var suffix = match.Groups["suffix"].Value;

            // 2. If has suffix, return as is (user specified a custom path)
            if (suffix.Length > 0) return basePart + queryPart;

            // 3. If has version, keep as is
            if (version.Length > 0) return basePart + queryPart;

            // 4. If no version, append default version according to schema
            var normalized = schema switch
            {
                ModelProviderSchema.OpenAI => $"{prefix}/v1",
                ModelProviderSchema.OpenAIResponses => $"{prefix}/v1",
                ModelProviderSchema.Anthropic => prefix,
                ModelProviderSchema.Google => $"{prefix}/v1beta",
                ModelProviderSchema.GoogleInteractions => $"{prefix}/v1",
                ModelProviderSchema.Mistral => $"{prefix}/v1",
                _ => prefix
            };
            return normalized + queryPart;
        }

        /// <summary>
        /// Returns the full preview endpoint URL for the given schema.
        /// This appends the schema-specific API path to the normalized base endpoint.
        /// For example, OpenAI endpoints are suffixed with "/chat/completions",
        /// while Anthropic endpoints are suffixed with "/messages".
        /// </summary>
        /// <param name="endpoint">The raw endpoint URL provided by the user.</param>
        /// <returns>The full API endpoint URL used for preview/testing.</returns>
        public string PreviewEndpoint(string? endpoint)
        {
            var prefix = schema.NormalizeEndpoint(endpoint);
            return schema switch
            {
                ModelProviderSchema.OpenAI => AppendPreviewSuffix(prefix, "/chat/completions"),
                ModelProviderSchema.OpenAIResponses => AppendPreviewSuffix(prefix, "/responses"),
                ModelProviderSchema.Anthropic => AppendPreviewSuffix(prefix, "/v1/messages", alreadyCompleteSuffix: "/messages"),
                ModelProviderSchema.Google => AppendGooglePreviewSuffix(prefix),
                ModelProviderSchema.GoogleInteractions => AppendPreviewSuffix(prefix, "/interactions"),
                ModelProviderSchema.Ollama => AppendPreviewSuffix(prefix, "/api/chat"),
                ModelProviderSchema.Mistral => AppendPreviewSuffix(prefix, "/chat/completions"),
                _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, null)
            };
        }
    }

    /// <summary>
    /// Resolves the Interactions resource from a normalized provider base. Keep the
    /// reverse-proxy path and query intact; an explicitly supplied resource is already complete.
    /// </summary>
    public static Uri GetGeminiInteractionsUri(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/interactions", StringComparison.Ordinal) ? endpoint : new UriBuilder(endpoint) { Path = path + "/interactions" }.Uri;
    }

    /// <summary>
    /// Resolves the Gemini <c>generateContent</c> or <c>streamGenerateContent</c> resource from a normalized base URI,
    /// preserving any reverse-proxy prefix or query parameters.
    /// </summary>
    public static Uri GetGeminiGenerateContentUri(Uri endpoint, string modelId, bool isStreaming)
    {
        var normalizedModel = modelId.StartsWith("models/", StringComparison.Ordinal) ? modelId["models/".Length..] : modelId;
        var path = endpoint.AbsolutePath.TrimEnd('/');
        var method = isStreaming ? "streamGenerateContent" : "generateContent";
        var targetPath = path.EndsWith(":generateContent", StringComparison.Ordinal)
            ? $"{path[..^":generateContent".Length]}:{method}"
            : path.EndsWith(":streamGenerateContent", StringComparison.Ordinal)
                ? $"{path[..^":streamGenerateContent".Length]}:{method}"
                : $"{path}/models/{normalizedModel}:{method}";

        var builder = new UriBuilder(endpoint) { Path = targetPath };
        if (!isStreaming) return builder.Uri;

        var query = builder.Query;
        if (query.StartsWith('?')) query = query[1..];
        if (!query.Contains("alt=", StringComparison.OrdinalIgnoreCase))
        {
            builder.Query = string.IsNullOrEmpty(query) ? "alt=sse" : $"{query}&alt=sse";
        }

        return builder.Uri;
    }

    private static string StripTrailingSuffix(string value, string suffix) =>
        value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value[..^suffix.Length] : value;

    private static string AppendPreviewSuffix(string? normalizedEndpoint, string suffix, string? alreadyCompleteSuffix = null)
    {
        if (string.IsNullOrEmpty(normalizedEndpoint)) return suffix;

        var queryIndex = normalizedEndpoint.IndexOf('?');
        var basePart = (queryIndex >= 0 ? normalizedEndpoint[..queryIndex] : normalizedEndpoint).TrimEnd('/');
        var queryPart = queryIndex >= 0 ? normalizedEndpoint[queryIndex..] : string.Empty;

        var completeSuffix = alreadyCompleteSuffix ?? suffix;
        if (basePart.EndsWith(completeSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return basePart + queryPart;
        }

        return basePart + suffix + queryPart;
    }

    private static string AppendGooglePreviewSuffix(string? normalizedEndpoint)
    {
        if (string.IsNullOrEmpty(normalizedEndpoint)) return "/models/{model}:generateContent";

        var queryIndex = normalizedEndpoint.IndexOf('?');
        var basePart = (queryIndex >= 0 ? normalizedEndpoint[..queryIndex] : normalizedEndpoint).TrimEnd('/');
        var queryPart = queryIndex >= 0 ? normalizedEndpoint[queryIndex..] : string.Empty;

        if (basePart.EndsWith(":generateContent", StringComparison.Ordinal) ||
            basePart.EndsWith(":streamGenerateContent", StringComparison.Ordinal))
        {
            return basePart + queryPart;
        }

        return $"{basePart}/models/{{model}}:generateContent{queryPart}";
    }

    /// <summary>
    /// Regex to decompose an endpoint URL into prefix, optional version, and optional suffix.
    /// - prefix: scheme + host (e.g. "https://api.openai.com") or bare host (e.g. "api.openai.com")
    /// - version: a path segment like "/v1", "/v2", "/v1beta" (optional)
    /// - suffix: any remaining path after the version segment (optional)
    /// </summary>
    [GeneratedRegex(@"^(?<prefix>https?://[^/]+|.+?)(?<version>/v\d+\w*)?(?<suffix>/.+)?$")]
    private static partial Regex EndpointRegex();
}
