using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>Creates user-scoped endpoints that permit same-user communication across UAC elevation.</summary>
public static partial class NamedPipeEndpoint
{
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint SeKernelObject = 6;

    /// <summary>
    /// Creates the one-client endpoint. Windows uses the OS first-instance flag and
    /// an explicit current-user security descriptor whose medium integrity label
    /// permits ordinary processes to connect to an elevated same-user peer. Other
    /// platforms use a separate file lease because the named-pipe implementation
    /// does not expose equivalent ownership semantics.
    /// </summary>
    public static NamedPipeServerStream CreateServer(string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsServer(endpoint);
        }

        return new NamedPipeServerStream(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsServer(string endpoint)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var userSidValue = userSid.Value;
        var security = new PipeSecurity();

        // CurrentUserOnly inherits the elevated creator's integrity label and blocks
        // the filtered-token Main process before RPC authentication can run. Keep the
        // same user-only DACL, then set only LABEL_SECURITY_INFORMATION through a
        // WRITE_OWNER handle. Supplying the label as a complete SACL at creation time
        // instead requires SeSecurityPrivilege, which ordinary processes do not hold.
        // Let Windows assign the owner and primary group from the creator token.
        // Explicitly assigning the user SID as the primary group can require a
        // privilege that filtered and ordinary user tokens do not hold.
        security.SetSecurityDescriptorSddlForm($"D:P(A;;GA;;;{userSidValue})");

        var server = NamedPipeServerStreamAcl.Create(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0,
            0,
            security,
            HandleInheritability.None,
            PipeAccessRights.TakeOwnership);
        try
        {
            SetMediumIntegrityLabel(server.SafePipeHandle);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void SetMediumIntegrityLabel(SafePipeHandle pipeHandle)
    {
        var descriptor = new RawSecurityDescriptor("S:(ML;;NW;;;ME)");
        var systemAcl = descriptor.SystemAcl ?? throw new InvalidOperationException("The medium-integrity pipe label is invalid.");
        var acl = new byte[systemAcl.BinaryLength];
        systemAcl.GetBinaryForm(acl, 0);

        fixed (byte* aclPointer = acl)
        {
            var error = SetSecurityInfo(pipeHandle, SeKernelObject, LabelSecurityInformation, 0, 0, 0, (nint)aclPointer);
            if (error != 0)
            {
                throw new Win32Exception((int)error, "The pipe integrity label could not be applied.");
            }
        }
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(
        SafePipeHandle handle,
        uint objectType,
        uint securityInfo,
        nint owner,
        nint group,
        nint dacl,
        nint sacl);
}