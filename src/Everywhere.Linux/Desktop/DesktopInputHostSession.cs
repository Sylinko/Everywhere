using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Keeps the Input Host healthy on starter desktops without claiming shortcut or
/// global capture support. Nonempty desired states are explicitly rejected.
/// </summary>
public sealed class DesktopInputHostSession : IProcessRoleSession, IInputHostRpc
{
    private volatile bool _isDraining;

    /// <inheritdoc />
    public void Bind(RpcConnection connection) => InputHostRpcBinding.Bind(connection, this);

    /// <inheritdoc />
    public void OnAuthenticated(RpcHandshake peer)
    {
    }

    /// <inheritdoc />
    public ValueTask<ApplyInputStateResponse> ApplyStateAsync(ApplyInputStateRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new ApplyInputStateResponse
            {
                IsApplied = !_isDraining && request.KeyboardRegistrations.Length == 0 && request.MouseRegistrations.Length == 0
                    && request.CaptureId == 0,
            });
    }

    /// <inheritdoc />
    public ValueTask BeginDrainingAsync(CancellationToken cancellationToken = default)
    {
        _isDraining = true;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => BeginDrainingAsync();
}