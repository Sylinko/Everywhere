using System.Reflection;
using System.Runtime.Loader;
using ZLinq;

namespace Everywhere.Modding.Hosting.Services;

/// <summary>
/// Matches reflection keys and assemblies against a set of retiring collectible
/// <see cref="AssemblyLoadContext"/> instances without retaining references to unloaded types.
/// </summary>
internal static class CollectibleContextMatcher
{
    /// <summary>
    /// Determines whether <paramref name="assembly"/> is owned by one of <paramref name="contexts"/>.
    /// </summary>
    internal static bool References(Assembly? assembly, IReadOnlySet<AssemblyLoadContext> contexts) =>
        assembly is not null &&
        contexts.Count > 0 &&
        AssemblyLoadContext.GetLoadContext(assembly) is { } context &&
        contexts.Contains(context);

    /// <summary>
    /// Determines whether <paramref name="type"/> or any of its element or generic type arguments
    /// belongs to one of <paramref name="contexts"/>.
    /// </summary>
    internal static bool References(Type? type, IReadOnlySet<AssemblyLoadContext> contexts)
    {
        if (type is null || contexts.Count == 0 || !type.IsCollectible)
        {
            return false;
        }

        if (References(type.Assembly, contexts))
        {
            return true;
        }

        if (type.HasElementType && References(type.GetElementType(), contexts))
        {
            return true;
        }

        return type.IsGenericType &&
               type.GetGenericArguments().AsValueEnumerable().Any(argument => !argument.IsGenericParameter && References(argument, contexts));
    }

    /// <summary>
    /// Determines whether <paramref name="member"/>, its declaring or reflected type,
    /// its defining module assembly, or any constructed generic method arguments belong to one of <paramref name="contexts"/>.
    /// </summary>
    internal static bool References(MemberInfo? member, IReadOnlySet<AssemblyLoadContext> contexts)
    {
        if (member is null || contexts.Count == 0)
        {
            return false;
        }

        if (member is Type type)
        {
            return References(type, contexts);
        }

        if (References(member.DeclaringType, contexts) ||
            References(member.ReflectedType, contexts) ||
            TryGetMemberAssembly(member) is { } assembly && References(assembly, contexts))
        {
            return true;
        }

        return member is MethodBase { IsGenericMethod: true } method &&
               method.GetGenericArguments().AsValueEnumerable().Any(argument => !argument.IsGenericParameter && References(argument, contexts));
    }

    /// <summary>
    /// Determines whether any invocation entry in <paramref name="handler"/> references one of <paramref name="contexts"/>
    /// through its delegate type, target instance, or invoked method.
    /// </summary>
    internal static bool References(Delegate? handler, IReadOnlySet<AssemblyLoadContext> contexts)
    {
        if (handler is null || contexts.Count == 0)
        {
            return false;
        }

        foreach (var invocation in Delegate.EnumerateInvocationList(handler))
        {
            if (References(invocation.GetType(), contexts) ||
                References(invocation.Method, contexts) ||
                invocation.Target is { } target && ReferencesObject(target, contexts))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Determines whether <paramref name="value"/> references one of <paramref name="contexts"/>,
    /// dispatching reflection, delegate, and assembly values to their specialized matchers.
    /// </summary>
    internal static bool ReferencesObject(object? value, IReadOnlySet<AssemblyLoadContext> contexts) => value switch
    {
        null => false,
        _ when contexts.Count == 0 => false,
        Type type => References(type, contexts),
        MemberInfo member => References(member, contexts),
        Delegate handler => References(handler, contexts),
        Assembly assembly => References(assembly, contexts),
        AssemblyLoadContext context => contexts.Contains(context),
        _ => References(value.GetType(), contexts)
    };

    private static Assembly? TryGetMemberAssembly(MemberInfo member)
    {
        try
        {
            return member.Module.Assembly;
        }
        catch (NotImplementedException)
        {
            return null;
        }
    }
}
