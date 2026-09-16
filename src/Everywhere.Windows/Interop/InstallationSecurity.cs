using System.Security.AccessControl;
using System.Security.Principal;
using Everywhere.ProcessIsolation.Hosting;

namespace Everywhere.Windows.Interop;

public static class InstallationSecurity
{
    private const FileSystemRights OwnerRights = FileSystemRights.FullControl;
    private const FileSystemRights UserRights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
    private const InheritanceFlags ChildInheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static HostsControlPlatformResult ProtectInstalledDirectory(string executablePath)
    {
        var directoryPath = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return HostsControlPlatformResult.Failure("The installed executable directory could not be resolved.");
        }

        try
        {
            var directory = new DirectoryInfo(directoryPath);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return HostsControlPlatformResult.Failure(
                    "The installed executable directory is a reparse point and cannot be used as the privileged Hosts boundary.");
            }

            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(administrators);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(system, OwnerRights, ChildInheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(
                new FileSystemAccessRule(administrators, OwnerRights, ChildInheritance, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, UserRights, ChildInheritance, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);

            var appliedSecurity = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            if (!appliedSecurity.AreAccessRulesProtected ||
                !administrators.Equals(appliedSecurity.GetOwner(typeof(SecurityIdentifier))) ||
                !HasAccessRule(appliedSecurity, system, OwnerRights) ||
                !HasAccessRule(appliedSecurity, administrators, OwnerRights) ||
                !HasAccessRule(appliedSecurity, users, UserRights))
            {
                return HostsControlPlatformResult.Failure("The installation directory ACL could not be verified after it was applied.");
            }

            return HostsControlPlatformResult.Success("The installation directory ACL was applied and verified.");
        }
        catch (Exception exception)
        {
            return HostsControlPlatformResult.Failure(
                $"The installation directory could not be protected: {exception.Message} (0x{exception.HResult:X8})");
        }
    }

    public static HostsServiceModeEnvironmentAssessment AssessPortableEnvironment(string executablePath)
    {
        if (InstallationIdentity.IsCurrentMachineInstallation(executablePath))
        {
            return new HostsServiceModeEnvironmentAssessment(false, false, "The current executable is managed by the installer.");
        }

        var directoryPath = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return new HostsServiceModeEnvironmentAssessment(true, true, "The executable directory could not be resolved.");
        }

        try
        {
            var root = Path.GetPathRoot(directoryPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return new HostsServiceModeEnvironmentAssessment(true, true, "The executable volume could not be resolved.");
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType is not DriveType.Fixed)
            {
                return new HostsServiceModeEnvironmentAssessment(true, true, $"The executable is stored on a {drive.DriveType} volume.");
            }

            for (var directory = new DirectoryInfo(directoryPath); directory is not null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return new HostsServiceModeEnvironmentAssessment(
                        true,
                        true,
                        $"The executable path contains the reparse point '{directory.FullName}'.");
                }

                var probePath = Path.Combine(directory.FullName, $".everywhere-write-probe-{Guid.NewGuid():N}.tmp");
                try
                {
                    using var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.Delete, 1, FileOptions.DeleteOnClose);
                    return new HostsServiceModeEnvironmentAssessment(
                        true,
                        true,
                        $"The current user can modify the executable path through '{directory.FullName}'.");
                }
                catch (UnauthorizedAccessException)
                {
                    // Continue toward the volume root because a writable parent can replace a protected child directory.
                }
            }

            return new HostsServiceModeEnvironmentAssessment(
                true,
                false,
                "The current user cannot create files in the executable directory or its ancestors.");
        }
        catch (Exception exception)
        {
            return new HostsServiceModeEnvironmentAssessment(true, true, $"The executable environment could not be verified: {exception.Message}");
        }
    }

    private static bool HasAccessRule(DirectorySecurity security, SecurityIdentifier identity, FileSystemRights requiredRights) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule =>
                identity.Equals(rule.IdentityReference) &&
                rule.AccessControlType is AccessControlType.Allow &&
                (rule.FileSystemRights & requiredRights) == requiredRights &&
                (rule.InheritanceFlags & ChildInheritance) == ChildInheritance);
}