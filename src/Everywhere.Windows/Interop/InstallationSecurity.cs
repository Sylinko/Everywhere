using Everywhere.ProcessIsolation.Hosting;

namespace Everywhere.Windows.Interop;

public static class InstallationSecurity
{
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
}