using System.Text.Json.Serialization;
using Everywhere.Chat;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Public DSL shape for the <c>attachments</c> root.
/// </summary>
/// <remarks>
/// This wrapper keeps strategy authors away from the UI-oriented attachment collection shape while still reusing
/// the existing attachment domain models and their JSON annotations for member exposure.
/// </remarks>
internal sealed record StrategyAttachmentsRoot(
    [property: JsonPropertyName("selection")] TextSelectionAttachment? Selection,
    [property: JsonPropertyName("files")] IReadOnlyList<FileAttachment> Files,
    [property: JsonPropertyName("texts")] IReadOnlyList<TextAttachment> Texts);

/// <summary>
/// Public DSL shape for environment predicates.
/// </summary>
/// <example>
/// <c>environment.os.equals: windows</c> and <c>environment.isWindows.equals: true</c> are equivalent on Windows.
/// </example>
internal sealed record StrategyEnvironmentRoot(
    [property: JsonPropertyName("os")] string Os,
    [property: JsonPropertyName("isWindows")] bool IsWindows,
    [property: JsonPropertyName("isMacOS")] bool IsMacOS,
    [property: JsonPropertyName("isLinux")] bool IsLinux,
    [property: JsonPropertyName("architecture")] string Architecture)
{
    /// <summary>
    /// Captures the process environment once for deterministic in-memory evaluation.
    /// </summary>
    public static StrategyEnvironmentRoot Current { get; } = new(
        #if WINDOWS

        "windows",
        true,
        false,
        false,

        #elif MACOS

        "macos",
        false,
        true,
        false,

        #elif LINUX

        "linux",
        false,
        false,
        true,

        #endif

        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
}

/// <summary>
/// Source-generated JSON metadata used as the public Condition DSL schema.
/// </summary>
/// <remarks>
/// The binding layer prefers this context so <c>JsonPropertyName</c>, <c>JsonIgnore</c>, generated accessors, and
/// camel-case naming all describe the same public surface consumed by strategy authors.
/// </remarks>
[JsonSerializable(typeof(StrategyAttachmentsRoot))]
[JsonSerializable(typeof(StrategyEnvironmentRoot))]
[JsonSerializable(typeof(ProcessInfo))]
[JsonSerializable(typeof(FileAttachment))]
[JsonSerializable(typeof(TextAttachment))]
[JsonSerializable(typeof(TextSelectionAttachment))]
[JsonSerializable(typeof(ExtraContextSnapshot))]
[JsonSerializable(typeof(ExtraContextNode))]
internal sealed partial class ConditionExpressionJsonSerializerContext : JsonSerializerContext;
