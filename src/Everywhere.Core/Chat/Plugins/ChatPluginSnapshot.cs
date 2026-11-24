using Everywhere.Harness;
using Everywhere.Utilities;
using Tabler.Avalonia;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Exposes the canonical rule-matching identifier shared by live plugins and generation snapshots.
/// </summary>
public interface IChatPluginKey
{
    /// <summary>
    /// Gets the canonical key matched by tool settings and persistent rule sets.
    /// </summary>
    string Key { get; }
}

/// <summary>
/// Captures immutable metadata, tool bindings, and wire-namespace formatting for a single generation.
/// </summary>
public abstract class ChatPluginSnapshot : IChatPluginKey
{
    /// <summary>
    /// Gets the deduplicated plugin display name within the enclosing generation scope.
    /// </summary>
    public string Name { get; }

    /// <inheritdoc />
    public string Key { get; }

    /// <summary>
    /// Gets the localized title key captured from the source plugin.
    /// </summary>
    public IDynamicLocaleKey HeaderKey { get; }

    /// <summary>
    /// Gets the localized description key captured from the source plugin.
    /// </summary>
    public IDynamicLocaleKey DescriptionKey { get; }

    /// <summary>
    /// Gets the vector icon kind captured from the source plugin, if any.
    /// </summary>
    public TablerIconKind? Icon { get; }

    /// <summary>
    /// Gets the custom icon source URI or data captured from the source plugin, if any.
    /// </summary>
    public string? BeautifulIcon { get; }

    /// <summary>
    /// Gets the fixed wire-name namespace prefix prepended to model-facing function names, if any.
    /// </summary>
    public string? WireNamespacePrefix { get; }

    /// <summary>
    /// Gets the tool bindings captured in this snapshot.
    /// </summary>
    public IReadOnlyList<ToolBinding> Bindings { get; }

    /// <summary>
    /// Gets the frozen function descriptors corresponding one-to-one with <see cref="Bindings"/>.
    /// </summary>
    public IReadOnlyList<ChatFunction> Functions { get; }

    /// <summary>
    /// Gets the number of functions captured in this snapshot.
    /// </summary>
    public int FunctionCount => Bindings.Count;

    /// <summary>
    /// Copies immutable metadata from <paramref name="source"/> and builds per-function tool bindings
    /// without retaining references to the live plugin instance.
    /// </summary>
    private protected ChatPluginSnapshot(
        ChatPlugin source,
        string? wireNamespacePrefix,
        NumberedDeduplicator pluginNameDeduplicator,
        NumberedDeduplicator functionNameDeduplicator,
        IReadOnlyList<ChatFunction> actualFunctions
    )
    {
        Name = pluginNameDeduplicator.Deduplicate(source.Name);
        Key = source.Key;
        HeaderKey = source.HeaderKey;
        DescriptionKey = source.DescriptionKey;
        Icon = source.Icon;
        BeautifulIcon = source.BeautifulIcon;
        WireNamespacePrefix = wireNamespacePrefix;

        var bindings = new ToolBinding[actualFunctions.Count];
        var functions = new ChatFunction[actualFunctions.Count];
        for (var i = 0; i < actualFunctions.Count; i++)
        {
            var binding = new ToolBinding(this, actualFunctions[i], functionNameDeduplicator);
            bindings[i] = binding;
            functions[i] = binding.Function;
        }

        Bindings = bindings;
        Functions = functions;
    }

    /// <summary>
    /// Formats a sanitized raw function name with this snapshot's wire-namespace prefix.
    /// </summary>
    public string FormatWireName(string sanitizedFunctionName) =>
        string.IsNullOrEmpty(WireNamespacePrefix) ? sanitizedFunctionName : $"{WireNamespacePrefix}{sanitizedFunctionName}";
}

/// <summary>
/// Represents an immutable generation snapshot for an in-process native plugin.
/// </summary>
public abstract class NativeChatPluginSnapshot : ChatPluginSnapshot
{
    private protected NativeChatPluginSnapshot(
        NativeChatPlugin source,
        string? wireNamespacePrefix,
        NumberedDeduplicator pluginNameDeduplicator,
        NumberedDeduplicator functionNameDeduplicator,
        IReadOnlyList<ChatFunction> actualFunctions
    ) : base(source, wireNamespacePrefix, pluginNameDeduplicator, functionNameDeduplicator, actualFunctions)
    {
    }
}

/// <summary>
/// Represents an immutable generation snapshot for a built-in Everywhere plugin.
/// </summary>
public sealed class BuiltInChatPluginSnapshot : NativeChatPluginSnapshot
{
    /// <summary>
    /// Initializes a snapshot of a built-in plugin using unprefixed wire tool names.
    /// </summary>
    public BuiltInChatPluginSnapshot(
        BuiltInChatPlugin source,
        NumberedDeduplicator pluginNameDeduplicator,
        NumberedDeduplicator functionNameDeduplicator,
        IReadOnlyList<ChatFunction> actualFunctions
    ) : base(source, null, pluginNameDeduplicator, functionNameDeduplicator, actualFunctions)
    {
    }
}

/// <summary>
/// Represents an immutable generation snapshot for a mod-supplied native plugin.
/// </summary>
public sealed class ModChatPluginSnapshot : NativeChatPluginSnapshot
{
    /// <summary>
    /// Wire namespace prefix applied to mod-supplied tool declarations exposed to model providers.
    /// </summary>
    public const string NamespacePrefix = "mod__";

    /// <summary>
    /// Initializes a snapshot of a mod-supplied plugin using the <see cref="NamespacePrefix"/> wire namespace.
    /// </summary>
    public ModChatPluginSnapshot(
        ModChatPlugin source,
        NumberedDeduplicator pluginNameDeduplicator,
        NumberedDeduplicator functionNameDeduplicator,
        IReadOnlyList<ChatFunction> actualFunctions
    ) : base(source, NamespacePrefix, pluginNameDeduplicator, functionNameDeduplicator, actualFunctions)
    {
    }
}

/// <summary>
/// Represents an immutable generation snapshot for a Model Context Protocol (MCP) plugin.
/// </summary>
public sealed class McpChatPluginSnapshot : ChatPluginSnapshot
{
    /// <summary>
    /// Wire namespace prefix applied to MCP tool declarations exposed to model providers.
    /// </summary>
    public const string NamespacePrefix = "mcp__";

    /// <summary>
    /// Initializes a snapshot of an MCP plugin using the <see cref="NamespacePrefix"/> wire namespace.
    /// </summary>
    public McpChatPluginSnapshot(
        McpChatPlugin source,
        NumberedDeduplicator pluginNameDeduplicator,
        NumberedDeduplicator functionNameDeduplicator,
        IReadOnlyList<ChatFunction> actualFunctions
    ) : base(source, NamespacePrefix, pluginNameDeduplicator, functionNameDeduplicator, actualFunctions)
    {
    }
}
