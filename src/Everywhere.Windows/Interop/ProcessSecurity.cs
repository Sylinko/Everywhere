using System.Security.Principal;

namespace Everywhere.Windows.Interop;

/// <summary>Provides process-token facts needed before the application service graph exists.</summary>
internal static class ProcessSecurity
{
    /// <summary>Whether the current effective token has membership in the built-in Administrators group.</summary>
    public static bool HasAdministrativeToken
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}