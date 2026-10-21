using System.Reflection;
using Everywhere.Modding.Abstractions;
using MonoMod.RuntimeDetour;

namespace ModFixture.Patching;

/// <summary>
/// Compiles into two independent collectible mods, each owning its hook.
/// </summary>
public sealed class Entry : IMod, IDisposable
{
    /// <summary>
    /// Identifies this contribution in the shared patch chain.
    /// </summary>
    private string _id = "";

    /// <summary>
    /// Prevents installed hooks from using retired state, including after normal exit disposal.
    /// </summary>
    private bool _isDisposed;

    /// <summary>
    /// Records late calls without resolving services from a disposing provider.
    /// </summary>
    private Action<string>? _log;

    /// <inheritdoc />
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        _id = context.ModId;
        _log = context.Services.GetService(typeof(Action<string>)) as Action<string>;
        var target = (MethodInfo?)context.Services.GetService(typeof(MethodInfo))
            ?? throw new InvalidOperationException("Missing patch target.");
        var wrapper = typeof(Entry).GetMethod(nameof(Wrap)) ?? throw new MissingMethodException();
        context.Register(new Hook(target, wrapper, this, new DetourConfig(_id, before: _id == "patch.a" ? ["patch.b"] : [])));

        var observe = (Action?)context.Services.GetService(typeof(Action))
            ?? throw new InvalidOperationException("Missing patch observer.");
        observe();

        if (context.Services.GetService(typeof(string)) as string == _id)
        {
            throw new InvalidOperationException("Injected failure after installing the hook.");
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Invokes the remaining chain before appending this mod's contribution.
    /// </summary>
    public string Wrap(Func<string, string> next, string value)
    {
        if (_isDisposed)
        {
            _log?.Invoke("stopped:" + _id);
            return next(value);
        }

        return next(value) + (_id == "patch.a" ? "A" : "B");
    }

    /// <inheritdoc />
    public void Dispose() => _isDisposed = true;
}