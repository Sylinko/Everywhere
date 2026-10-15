using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using MonoMod;

namespace Everywhere.Patches.SemanticKernel.Core;

/// <summary>
/// Changes only the ownership of SK's parameter-converter cache. The upstream factory delegate,
/// including culture, nullable, enum and TypeConverter behavior, is passed through unchanged.
/// </summary>
[MonoModPatch("Microsoft.SemanticKernel.KernelFunctionFromMethod")]
internal class patch_KernelFunctionFromMethod
{
    private static readonly ConditionalWeakTable<Type, ParserEntry> CollectibleParsers = new();

    /// <summary>
    /// Replaces the single GetOrAdd call in GetConverter during weaving. Noncollectible types
    /// retain the upstream fast path; collectible keys and their converter closures can die together.
    /// </summary>
    private static Func<object?, CultureInfo, object?>? GetOrAddParser(
        ConcurrentDictionary<Type, Func<object?, CultureInfo, object?>?> parsers,
        Type targetType,
        Func<Type, Func<object?, CultureInfo, object?>?> factory)
    {
        return targetType.IsCollectible ?
            CollectibleParsers.GetValue(targetType, key => new ParserEntry(factory(key))).Parser :
            parsers.GetOrAdd(targetType, factory);
    }

    /// <summary>
    /// Allows the weak-key table to preserve the upstream cache's null results.
    /// </summary>
    private sealed class ParserEntry(Func<object?, CultureInfo, object?>? parser)
    {
        /// <summary>
        /// Gets the original converter, or null for unsupported types.
        /// </summary>
        internal Func<object?, CultureInfo, object?>? Parser { get; } = parser;
    }
}