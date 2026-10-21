using Bitmap = Avalonia.Media.Imaging.Bitmap;
using Avalonia.Input;
using Everywhere.Interop;
using Network;
using UserNotifications;

namespace Everywhere.Mac.Interop;

// This implementation leverages the Microsoft.macOS SDK (or Xamarin.Mac)
// for type-safe access to native macOS APIs.
public sealed class NativeHelper : INativeHelper
{
    // A unique identifier for your app, used for notifications and launch services.
    private const string AppBundleIdentifier = "com.sylinko.everywhere";
    private static readonly string UserLaunchAgentPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library/LaunchAgents",
        $"{AppBundleIdentifier}.plist");

    // The main application bundle.
    private static readonly NSBundle MainBundle = NSBundle.MainBundle;

    /// <summary>
    /// On macOS, an app is generally considered "installed" if it's running from the /Applications directory.
    /// </summary>
    public bool IsInstalled => MainBundle.BundlePath.StartsWith("/Applications", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Manages whether the app starts automatically on user login.
    /// A per-user LaunchAgent executes the application bundle at login.
    /// </summary>
    public bool IsStartupEnabled
    {
        get => File.Exists(UserLaunchAgentPath);
        set
        {
            if (value)
            {
                // Ensure the directory exists.
                Directory.CreateDirectory(Path.GetDirectoryName(UserLaunchAgentPath)!);

                var plist =
                    $"""
                     <?xml version="1.0" encoding="UTF-8"?>
                     <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                     <plist version="1.0">
                     <dict>
                         <key>Label</key>
                         <string>{AppBundleIdentifier}</string>
                         <key>ProgramArguments</key>
                         <array>
                             <string>{MainBundle.ExecutablePath}</string>
                         </array>
                         <key>RunAtLoad</key>
                         <true/>
                     </dict>
                     </plist>
                     """;
                File.WriteAllText(UserLaunchAgentPath, plist);
            }
            else
            {
                if (File.Exists(UserLaunchAgentPath))
                {
                    File.Delete(UserLaunchAgentPath);
                }
            }
        }
    }

    public bool IsLowDataModeActive
    {
        get
        {
            using var monitor = new NWPathMonitor();
            var isConstrained = false;
            monitor.SnapshotHandler = path => { isConstrained = path.IsConstrained; };
            monitor.Start();
            return isConstrained;
        }
    }

    public bool GetKeyState(KeyModifiers keyModifiers)
    {
        var flags = keyModifiers.ToCGEventFlags();
        var flagsState = CGEventSource.GetFlagsState(CGEventSourceStateID.HidSystem);
        return (flagsState & flags) == flags;
    }

    /// <summary>
    /// Shows a desktop notification using the UserNotifications framework (UNNotification).
    /// If user clicks or approves the notification, returns true. Otherwise, false.
    /// </summary>
    public Task<bool> ShowDesktopNotificationAsync(string message, string? title = null)
    {
        var tcs = new TaskCompletionSource<bool>();

        var center = UNUserNotificationCenter.Current;
        center.RequestAuthorization(
            UNAuthorizationOptions.Alert,
            (granted, error) =>
            {
                // nullable annotation is not correct here
                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                if (!granted || error is not null)
                {
                    tcs.SetResult(false);
                    return;
                }

                var content = new UNMutableNotificationContent
                {
                    Title = title ?? "Everywhere",
                    Body = message,
                    Sound = UNNotificationSound.Default
                };

                var request = UNNotificationRequest.FromIdentifier(
                    Guid.CreateVersion7().ToString(),
                    content,
                    null);

                center.AddNotificationRequest(
                    request,
                    err =>
                    {
                        // nullable annotation is not correct here
                        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                        if (err is not null)
                        {
                            tcs.SetResult(false);
                            return;
                        }

                        // Handle user interaction
                        center.Delegate = new NotificationDelegate(tcs);
                    });
            });

        using var pool = new NSAutoreleasePool();
        NSApplication.SharedApplication.RequestUserAttention(NSRequestUserAttentionType.InformationalRequest);

        return tcs.Task;
    }

    /// <summary>
    /// Reveals a file in Finder.
    /// </summary>
    public void OpenFileLocation(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return;

        if (Path.GetDirectoryName(fullPath) is not { Length: > 0 } directoryPath)
        {
            // root, just open finder
            NSWorkspace.SharedWorkspace.SelectFile(fullPath, "/");
        }
        else
        {
            NSWorkspace.SharedWorkspace.SelectFile(fullPath, directoryPath);
        }
    }

    /// <inheritdoc />
    public NativeApplicationInfo? GetApplicationInfo(string identityOrPath)
    {
        var path = identityOrPath;
        if (!Path.IsPathFullyQualified(path))
        {
            using var url = NSWorkspace.SharedWorkspace.UrlForApplication(identityOrPath);
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            // macios SDK has nullable annotation issues, so we need to check for null here.
            if (url?.Path is not { } applicationPath)
            {
                return null;
            }

            path = applicationPath;
        }

        if (!Directory.Exists(path) || !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var bundle = NSBundle.FromPath(path);
        if (bundle?.BundleIdentifier is not { Length: > 0 } identifier)
        {
            return null;
        }

        var name = bundle.ObjectForInfoDictionary("CFBundleDisplayName")?.ToString() ??
            bundle.ObjectForInfoDictionary("CFBundleName")?.ToString() ??
            Path.GetFileNameWithoutExtension(path);
        return new NativeApplicationInfo(identifier, name, path);
    }

    /// <inheritdoc />
    public NativeApplicationInfo? GetApplicationInfo(int processId)
    {
        using var application = NSRunningApplication.GetRunningApplication(processId);
        return application.BundleUrl?.Path is { } path ? GetApplicationInfo(path) : null;
    }

    /// <inheritdoc />
    public Bitmap? GetApplicationIcon(string? path)
    {
        using var image = path is not null && Directory.Exists(path) ?
            NSWorkspace.SharedWorkspace.IconForFile(path) :
            NSWorkspace.SharedWorkspace.GetIcon(UniformTypeIdentifiers.UTTypes.ApplicationBundle);
        using var tiff = image.AsTiff();
        if (tiff is null)
        {
            return null;
        }

        using var representation = new NSBitmapImageRep(tiff);
        using var properties = new NSDictionary();
        using var png = representation.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, properties);
        if (png is null)
        {
            return null;
        }

        using var stream = png.AsStream();
        return new Bitmap(stream);
    }

    private class NotificationDelegate(TaskCompletionSource<bool> tcs) : UNUserNotificationCenterDelegate
    {
        public override void DidReceiveNotificationResponse(
            UNUserNotificationCenter center,
            UNNotificationResponse response,
            Action completionHandler)
        {
            tcs.TrySetResult(true);
            completionHandler();
        }

        public override void OpenSettings(UNUserNotificationCenter center, UNNotification? notification)
        {
            // just override to avoid base method throwing NotImplementedException
        }

        public override void WillPresentNotification(
            UNUserNotificationCenter center,
            UNNotification notification,
            Action<UNNotificationPresentationOptions> completionHandler)
        {
            // Show the notification even if the app is in the foreground.
            completionHandler(UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Banner);
        }
    }
}