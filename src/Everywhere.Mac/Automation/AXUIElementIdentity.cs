using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Borrows one live AX reference for identity-map comparison during an active element incarnation.
/// </summary>
/// <remarks>
/// This value does not own the reference. An operation-local AX owner keeps lookup keys alive until comparison completes, and a canonical AX visual element keeps stored keys alive until the exact map entry is removed.
/// </remarks>
public readonly record struct AXUIElementIdentity
{
    public nint Handle { get; }

    public nuint Hash { get; }

    public AXUIElementIdentity(nint handle)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "An AX identity requires a live native reference.");
        }

        Handle = handle;
        Hash = CFInterop.CFHash(handle);
    }
}

/// <summary>
/// Compares AX identities by Core Foundation equality rather than native pointer value.
/// </summary>
public sealed class AXUIElementIdentityComparer : IEqualityComparer<AXUIElementIdentity>
{
    public static AXUIElementIdentityComparer Shared { get; } = new();

    private AXUIElementIdentityComparer()
    {
    }

    public bool Equals(AXUIElementIdentity x, AXUIElementIdentity y) =>
        x.Handle == y.Handle || x.Hash == y.Hash && CFInterop.CFEqual(x.Handle, y.Handle);

    public int GetHashCode(AXUIElementIdentity obj) => obj.Hash.GetHashCode();
}