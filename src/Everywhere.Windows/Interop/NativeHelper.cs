using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.Networking.Connectivity;
using Windows.UI.Notifications;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Avalonia.Input;
using Everywhere.Extensions;
using Everywhere.Interop;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Windows.Interop;

public class NativeHelper : INativeHelper
{
    private const string AppName = nameof(Everywhere);
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ProcessFullPath => Path.GetFullPath(Environment.ProcessPath.NotNull());

    public bool IsInstalled => InstallationIdentity.IsCurrentRegisteredInstallation(ProcessFullPath);

    public bool IsStartupEnabled
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

    /// <inheritdoc />
    public NativeApplicationInfo? GetApplicationInfo(string identityOrPath)
    {
        if (!Path.IsPathFullyQualified(identityOrPath) || !File.Exists(identityOrPath))
        {
            return null;
        }

        var path = Path.GetFullPath(identityOrPath);
        var name = FileVersionInfo.GetVersionInfo(path).FileDescription;
        return new NativeApplicationInfo(path, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(path) : name, path);
    }

    /// <inheritdoc />
    public NativeApplicationInfo? GetApplicationInfo(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        using var process = new SafeProcessHandle(
            PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId),
            true);
        if (process.IsInvalid)
        {
            return null;
        }

        var buffer = new char[32_768].AsSpan();
        var length = (uint)buffer.Length;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length) && length > 0 ?
            GetApplicationInfo(new string(buffer[..(int)length])) :
            null;
    }

    /// <inheritdoc />
    public Bitmap? GetApplicationIcon(string? path)
    {
        try
        {
            using var icon = path is not null && Path.IsPathFullyQualified(path) && File.Exists(path) ?
                Icon.ExtractAssociatedIcon(path) ?? (Icon)SystemIcons.Application.Clone() :
                (Icon)SystemIcons.Application.Clone();
            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }
}