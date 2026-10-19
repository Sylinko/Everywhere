using System.Runtime.Loader;
using Autofac.Core;
using ZLinq;

namespace Everywhere.Plugin.Hosting.Services;

/// <summary>
/// Removes Autofac reflection entries belonging to retiring assembly owners, including generic
/// arguments whose defining assembly is the host. Live owners are never selected wholesale.
/// </summary>
internal static class PluginReflectionCache
{
    /// <summary>
    /// Clears only keys or recorded assemblies referring to an owner that is actually retiring.
    /// Activation scopes and assembly resources must have finished disposal before this call.
    /// </summary>
    internal static void Clear(IReadOnlySet<AssemblyLoadContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        ReflectionCacheSet.Shared.Clear((key, assemblies) =>
            assemblies.AsValueEnumerable().Any(assembly => AssemblyLoadContext.GetLoadContext(assembly) is { } context && contexts.Contains(context)) ||
            key is Type type && ReferencesContext(type, contexts) ||
            key?.DeclaringType is { } declaringType && ReferencesContext(declaringType, contexts) ||
            key?.ReflectedType is { } reflectedType && ReferencesContext(reflectedType, contexts));
    }

    /// <summary>
    /// Follows constructed generic arguments and element types rather than only Type.Assembly.
    /// </summary>
    private static bool ReferencesContext(Type type, IReadOnlySet<AssemblyLoadContext> contexts)
    {
        if (AssemblyLoadContext.GetLoadContext(type.Assembly) is { } context && contexts.Contains(context))
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } elementType && ReferencesContext(elementType, contexts))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().AsValueEnumerable().Any(argument => ReferencesContext(argument, contexts));
    }
}