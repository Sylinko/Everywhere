using Everywhere.ProcessIsolation.Roles;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Applies the local, same-build handshake policy. The validator is intentionally
/// small: path/ACL checks belong to endpoint setup, while this method binds the
/// accepted connection to one desktop session and one Host-generated nonce.
/// </summary>
public static class RpcHandshakeValidator
{
    /// <summary>
    /// Validates a peer claiming <paramref name="expectedPeerRole"/> and creates
    /// the accepted response, including a fresh nonce only on success.
    /// </summary>
    /// <param name="handshake">Identity supplied by the connecting peer.</param>
    /// <param name="expectedPeerRole">Role that the local endpoint is willing to accept.</param>
    /// <param name="localIdentity">Build, role, PID, and desktop session of the local process.</param>
    /// <returns>An accepted response or a stable rejection category.</returns>
    public static RpcHandshakeAck Validate(RpcHandshake handshake, ProcessRole expectedPeerRole, RpcHandshakeIdentity localIdentity) =>
        Validate(handshake, ProcessRoleNames.ToWireName(expectedPeerRole), localIdentity);

    /// <summary>
    /// Validates a peer using a closed wire identity that is not a persistent
    /// <see cref="ProcessRole"/>, such as the short-lived Hosts controller.
    /// </summary>
    /// <param name="handshake">Identity supplied by the connecting peer.</param>
    /// <param name="expectedPeerWireName">Closed, non-localized wire identity accepted by this endpoint.</param>
    /// <param name="localIdentity">Build, role, PID, and desktop session of the local process.</param>
    /// <returns>An accepted response or a stable rejection category.</returns>
    public static RpcHandshakeAck Validate(RpcHandshake handshake, string expectedPeerWireName, RpcHandshakeIdentity localIdentity)
    {
        var rejectionCode = handshake.AssemblyInformationalVersion != localIdentity.AssemblyInformationalVersion ? "build_mismatch" :
            handshake.Role != expectedPeerWireName ? "role_mismatch" :
            handshake.ProcessId <= 0 ? "invalid_process_id" :
            string.IsNullOrWhiteSpace(handshake.DesktopSessionId) ? "missing_desktop_session" :
            !string.Equals(handshake.DesktopSessionId, localIdentity.DesktopSessionId, StringComparison.Ordinal) ? "desktop_session_mismatch" : null;

        return CreateResponse(
            localIdentity,
            rejectionCode is null ? Guid.NewGuid().ToString("N") : null,
            rejectionCode);
    }

    /// <summary>
    /// Validates the accepted Host identity returned to Main. Host-side request
    /// validation does not replace this check because both peers must independently
    /// enforce the same-build, expected-role, and desktop-session boundary.
    /// </summary>
    public static void ValidateAcceptedPeer(RpcHandshakeAck response, ProcessRole expectedPeerRole, RpcHandshakeIdentity localIdentity)
        => ValidateAcceptedPeer(response, ProcessRoleNames.ToWireName(expectedPeerRole), localIdentity);

    /// <summary>Validates an accepted peer with a closed wire identity outside <see cref="ProcessRole"/>.</summary>
    public static void ValidateAcceptedPeer(RpcHandshakeAck response, string expectedPeerWireName, RpcHandshakeIdentity localIdentity)
    {
        if (!response.Accepted)
        {
            throw new RpcRemoteException(
                response.RejectionCode ?? "handshake_rejected",
                "The peer rejected the RPC handshake.");
        }

        var violation = response.AssemblyInformationalVersion != localIdentity.AssemblyInformationalVersion ? "build identity" :
            response.Role != expectedPeerWireName ? "process role" :
            response.ProcessId <= 0 ? "process ID" :
            !string.Equals(response.DesktopSessionId, localIdentity.DesktopSessionId, StringComparison.Ordinal) ? "desktop session" : null;

        if (violation is not null)
        {
            throw new RpcProtocolException($"The accepted RPC peer returned an unexpected {violation}.");
        }
    }

    private static RpcHandshakeAck CreateResponse(RpcHandshakeIdentity localIdentity, string? connectionNonce, string? rejectionCode) => new()
    {
        AssemblyInformationalVersion = localIdentity.AssemblyInformationalVersion,
        Role = localIdentity.WireName,
        ProcessId = localIdentity.ProcessId,
        DesktopSessionId = localIdentity.DesktopSessionId,
        ConnectionNonce = connectionNonce,
        Accepted = rejectionCode is null,
        RejectionCode = rejectionCode
    };
}