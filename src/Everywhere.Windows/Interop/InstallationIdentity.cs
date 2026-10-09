using Microsoft.Win32;

namespace Everywhere.Windows.Interop;

public static class InstallationIdentity
{
    // The existing Inno AppId includes the second closing brace; changing this key would break installed-update detection.
    private const string RegistryInstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}_is1";

    public static bool IsCurrentRegisteredInstallation(string executablePath)
    {
        using var machineKey = Registry.LocalMachine.OpenSubKey(RegistryInstallKey);
        if (IsCurrentInstallation(machineKey, executablePath))
        {
            return true;
        }

        using var legacyUserKey = Registry.CurrentUser.OpenSubKey(RegistryInstallKey);
        return IsCurrentInstallation(legacyUserKey, executablePath);
    }

    private static bool IsCurrentInstallation(RegistryKey? key, string executablePath)
    {
        var installLocation = key?.GetValue("InstallLocation")?.ToString();
        var processDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(installLocation) || string.IsNullOrWhiteSpace(processDirectory))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(installLocation)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(processDirectory)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}