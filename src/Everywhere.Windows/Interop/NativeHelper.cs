using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using Windows.Data.Xml.Dom;
using Windows.Networking.Connectivity;
using Windows.UI.Notifications;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Avalonia.Input;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Interop;
using Microsoft.Win32;

namespace Everywhere.Windows.Interop;

public class NativeHelper : INativeHelper
{
    private const string AppName = nameof(Everywhere);
    private const string RegistryInstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D66EA41B-8DEB-4E5A-9D32-AB4F8305F664}}_is1";
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string ProcessFullPath => Path.GetFullPath(Environment.ProcessPath.NotNull());

    public bool IsInstalled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryInstallKey);
            return key?.GetValue("InstallLocation")?.ToString() is not null;
        }
    }

    public bool IsAdministrator
    {
        get
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool IsUserStartupEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey);
                return key?.GetValue(AppName) != null;
            }
            catch
            {
                // If the registry key cannot be accessed, assume it is not enabled.
                return false;
            }
        }
        set
        {
            if (value)
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
                key?.SetValue(AppName, $"\"{ProcessFullPath}\" --autorun");
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
                key?.DeleteValue(AppName, false);
            }
        }
    }

    public bool IsAdministratorStartupEnabled
    {
        get
        {
            try
            {
                return TaskSchedulerHelper.IsTaskScheduled(AppName);
            }
            catch
            {
                return false;
            }
        }
        set
        {
            if (!IsAdministrator) throw new UnauthorizedAccessException("The current user is not an administrator.");

            if (value)
            {
                TaskSchedulerHelper.CreateScheduledTask(AppName, $"\"{ProcessFullPath}\" --autorun --load-user-profile");
            }
            else
            {
                TaskSchedulerHelper.DeleteScheduledTask(AppName);
            }
        }
    }

    public bool IsLowDataModeActive
    {
        get
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null) return false;

            var cost = profile.GetConnectionCost();
            return cost.NetworkCostType != NetworkCostType.Unrestricted || cost.ApproachingDataLimit || cost.OverDataLimit;
        }
    }

    public void RestartAsAdministrator()
    {
        if (IsAdministrator)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath.NotNull(),
            Arguments = "--ui",
            UseShellExecute = true,
            Verb = "runas" // This will prompt for elevation
        };

        Entrance.ReleaseMutex();
        Process.Start(startInfo);
        Environment.Exit(0); // Exit the current process
    }

    public bool GetKeyState(KeyModifiers keyModifiers)
    {
        var result = false;
        if (keyModifiers.HasFlag(KeyModifiers.Control)) result &= PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_CONTROL) != 0;
        if (keyModifiers.HasFlag(KeyModifiers.Shift)) result &= PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SHIFT) != 0;
        if (keyModifiers.HasFlag(KeyModifiers.Alt)) result &= PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_MENU) != 0;
        return result;
    }

    public Task<bool> ShowDesktopNotificationAsync(string message, string? title)
    {
        const string ModelId = "com.Sylinko.Everywhere";
        try
        {
            EnsureAumidRegistered();
        }
        catch
        {
            // ignore
        }

        try
        {
            var xml =
                $"""
                 <toast launch='conversationId=9813'>
                     <visual>
                         <binding template='ToastGeneric'>
                             {(string.IsNullOrEmpty(title) ? "" : $"<text>{SecurityElement.Escape(title)}</text>")}
                             <text>{SecurityElement.Escape(message)}</text>
                         </binding>
                     </visual>
                 </toast>
                 """;
            var xmlDocument = new XmlDocument();
            xmlDocument.LoadXml(xml);

            var toast = new ToastNotification(xmlDocument);
            ToastNotificationManager.CreateToastNotifier(ModelId).Show(toast);
            var tcs = new TaskCompletionSource<bool>();

            toast.Activated += (_, _) => tcs.SetResult(true);
            toast.Dismissed += (_, _) => tcs.SetResult(false);
            toast.Failed += (_, _) => tcs.SetResult(false);

            return tcs.Task;
        }
        catch
        {
            return Task.FromResult(false);
        }

        void EnsureAumidRegistered()
        {
            var iconFilePath = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location)!, "Everywhere.ico");
            using var registryKey = Registry.CurrentUser.CreateSubKey(Path.Combine(@"Software\Classes\AppUserModelId", ModelId));
            registryKey.SetValue("DisplayName", "Everywhere");
            registryKey.SetValue("IconUri", iconFilePath);
        }
    }

    public void OpenFileLocation(string fullPath)
    {
        if (fullPath.IsNullOrWhiteSpace()) return;
        var args = $"/e,/select,\"{fullPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }
}