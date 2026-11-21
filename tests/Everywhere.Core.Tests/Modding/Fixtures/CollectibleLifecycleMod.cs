using Everywhere.Modding.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Core.Tests.Modding.Fixtures;

/// <summary>
/// Records collectible activation and retirement events into a host-owned trace list.
/// </summary>
public sealed class CollectibleLifecycleMod : IMod, IAsyncDisposable
{
    private IModContext? _context;
    private IList<string>? _trace;

    /// <inheritdoc />
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _trace = context.Services.GetRequiredService<IList<string>>();
        var version = context.Mods.TryGetInfo(context.ModId, out var info) ? info.Version : "unknown";
        _trace.Add($"init:{context.ModId}:{version}");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            _trace?.Add($"dispose:{_context.ModId}:{_context.StopReason}");
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Throws during disposal so host retirement tests can exercise <c>RestartRequired</c> flows.
/// </summary>
public sealed class FailingStopMod : IMod, IAsyncDisposable
{
    /// <inheritdoc />
    public ValueTask InitializeAsync(IModContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => throw new InvalidOperationException("Simulated retirement failure");
}
