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
                ModelProviderSchema.Ollama => "http://localhost:11434",
                _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, null)
            };
        }

        /// <summary>
        /// Normalizes the given endpoint URL according to the schema's requirements.
        /// For OpenAI:
        /// https://api.openai.com/v1 -> https://api.openai.com/v1
        /// https://api.openai.com -> https://api.openai.com/v1
        /// https://api.openai.com/ -> https://api.openai.com/v1
        /// https://api.openai.com/v2 -> https://api.openai.com/v2 (Custom version, do not override)
        /// https://api.openai.com/v1/chat -> https://api.openai.com/v1/chat (Custom endpoint, do not override)
        /// api.openai.com -> api.openai.com/v1 (No url scheme, process as normal)
        /// api.openai.com/ -> api.openai.com/v1 (No url scheme, process as normal)
        /// api.openai.com/v2 -> api.openai.com/v2 (No url scheme, process as normal)
        /// api.openai.com/v1/chat -> api.openai.com/v1/chat (No url scheme, process as normal)
        ///
        /// For Gemini: append /v1beta
        /// </summary>
        /// <param name="endpoint"></param>
        /// <returns></returns>
        public string? NormalizeEndpoint(string? endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;

            endpoint = endpoint.Trim().TrimEnd('/');

            // Don't normalize endpoint ending with #
            if (endpoint.EndsWith('#'))
            {
                return endpoint[..^1].Trim().TrimEnd('/');
            }

            // 1. Get prefix, version (v1, v2, v3, v1beta, etc.), and suffix (if any)
            var match = EndpointRegex().Match(endpoint);
            if (!match.Success)
                return endpoint;

            var prefix = match.Groups["prefix"].Value;
            var version = match.Groups["version"].Value;
            var suffix = match.Groups["suffix"].Value;

            // 2. If has suffix, return as is (user specified a custom path)
            if (suffix.Length > 0)
                return endpoint;

            // 3. If has version, keep as is
            if (version.Length > 0)
                return endpoint;

            // 4. If no version, append default version according to schema
            return schema switch
            {
                ModelProviderSchema.OpenAI => $"{prefix}/v1",
                ModelProviderSchema.OpenAIResponses => $"{prefix}/v1",
                ModelProviderSchema.Anthropic => prefix,
                ModelProviderSchema.Google => $"{prefix}/v1beta",
                _ => prefix
            };
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
                ModelProviderSchema.OpenAI => $"{prefix}/chat/completions",
                ModelProviderSchema.OpenAIResponses => $"{prefix}/responses",
                ModelProviderSchema.Anthropic => $"{prefix}/v1/messages",
                ModelProviderSchema.Google => $"{prefix}/models",
                ModelProviderSchema.Ollama => $"{prefix}/api/chat",
                _ => throw new ArgumentOutOfRangeException(nameof(schema), schema, null)
            };
        }
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