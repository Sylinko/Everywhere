using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Mac.Interop;
using Everywhere.Messages;
using Everywhere.ViewModels;
using ObjCRuntime;

namespace Everywhere.Mac;

[Register("AppDelegate")]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllMethods)]
public partial class AppDelegate : NSApplicationDelegate
{
    /// <summary>
    /// Sets whether the application is visible in the Dock.
    /// </summary>
    public static bool IsVisibleInDock
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            UpdateIsVisibleInDock();
        }
    }

    private NSObject? _spaceChangeObserver;

    /// <summary>
    /// Checks if the current active space is a fullscreen space.
    /// from: https://exchangetuts.com/index.php/os-x-detecting-when-front-app-goes-into-fullscreen-mode-1640598124761527
    /// </summary>
    /// <returns></returns>
    private static bool IsInFullscreenSpace()
    {
        var pWindows = CGInterop.CGWindowListCopyWindowInfo(CGWindowListOption.OnScreenOnly, 0);
        using var windows = Runtime.GetNSObject<NSArray>(pWindows, true);
        if (windows is null) return false;

        using var windowName = new NSString("kCGWindowName");
        using var windowOwnerName = new NSString("kCGWindowOwnerName");
        using var windowOwnerPid = new NSString("kCGWindowOwnerPID");

        var count = windows.Count;
        for (nuint i = 0; i < count; i++)
        {
            var window = windows.GetItem<NSDictionary>(i);
            if (window is null) continue;

            using var nameObj = window.ValueForKey(windowName);
            var name = nameObj?.ToString();
            if (name != "Fullscreen Backdrop") continue;

            // In non-English macOS, the Dock process name may be localized.
            // So we use the process path to identify it.
            using var pidObj = window.ValueForKey(windowOwnerPid);
            if (!int.TryParse(pidObj?.ToString(), out var pid) || pid <= 0) continue;

            var path = GetPathByPid(pid);
            if (path?.Equals("/System/Library/CoreServices/Dock.app/Contents/MacOS/Dock", StringComparison.OrdinalIgnoreCase) is true) return true;
        }

        return false;
    }

    private const int PROC_PIDPATHINFO_MAXSIZE = 1024;

    [LibraryImport("libproc", EntryPoint = "proc_pidpath")]
    private static unsafe partial int ProcPidPath(int pid, byte* buffer, uint bufferSize);

    public static unsafe string? GetPathByPid(int pid)
    {
        var buffer = stackalloc byte[PROC_PIDPATHINFO_MAXSIZE];
        var length = ProcPidPath(pid, buffer, PROC_PIDPATHINFO_MAXSIZE);
        return length <= 0 ? null : Encoding.UTF8.GetString(buffer, length);
    }

    public override void DidFinishLaunching(NSNotification notification)
    {
        _spaceChangeObserver = NSWorkspace.SharedWorkspace.NotificationCenter.AddObserver(
            NSWorkspace.ActiveSpaceDidChangeNotification,
            OnSpaceChanged
        );

        // Request for screen recording permissions
        // otherwise, we cannot get "kCGWindowName" info.
        PermissionHelper.RequestForScreenRecordingPermission();
    }

    public override void WillTerminate(NSNotification notification)
    {
        if (_spaceChangeObserver is null) return;
        NSWorkspace.SharedWorkspace.NotificationCenter.RemoveObserver(_spaceChangeObserver);
        _spaceChangeObserver = null;
    }

    public override void WillFinishLaunching(NSNotification notification)
    {
        NSAppleEventManager.SharedAppleEventManager.SetEventHandler(
            this,
            new Selector("handleGetURLEvent:withReplyEvent:"),
            AEEventClass.Internet,
            AEEventID.GetUrl);
    }

    private static void OnSpaceChanged(NSNotification obj)
    {
        UpdateIsVisibleInDock();
    }

    /// <summary>
    /// Updates the application's visibility in the Dock based on the current space.
    /// </summary>
    private static void UpdateIsVisibleInDock()
    {
        var isVisible = IsVisibleInDock && !IsInFullscreenSpace();
        NSApplication.SharedApplication.ActivationPolicy =
            isVisible ? NSApplicationActivationPolicy.Regular : NSApplicationActivationPolicy.Accessory;
    }

    public override bool ApplicationShouldHandleReopen(NSApplication sender, bool hasVisibleWindows)
    {
        // We handled the reopen by showing the chat window.
        WeakReferenceMessenger.Default.Send<ApplicationMessage>(new ShowWindowMessage(ShowWindowMessage.ChatWindow));
        return true;
    }

    [Export("handleGetURLEvent:withReplyEvent:")]
    private void HandleGetURLEvent(NSAppleEventDescriptor evt, NSAppleEventDescriptor replyEvt)
    {
        var url = evt.ParamDescriptorForKeyword(GetDescriptor("----"))?.StringValue;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.Equals(
                UrlProtocolCallbackMessage.Scheme,
                StringComparison.OrdinalIgnoreCase))
        {
            WeakReferenceMessenger.Default.Send<ApplicationMessage>(new UrlProtocolCallbackMessage(url));
        }

        static uint GetDescriptor(string s) => (uint)(s[0] << 24 | s[1] << 16 | s[2] << 8 | s[3]);
    }
}