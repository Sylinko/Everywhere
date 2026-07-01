using System.Runtime.InteropServices;
using Everywhere.I18N;
using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Helper class for managing macOS Accessibility permissions required for global event listening.
/// </summary>
public static partial class PermissionHelper
{
    // Key for the options dictionary.
    private static readonly NSString AxTrustedCheckOptionPrompt = new("AXTrustedCheckOptionPrompt");

    /// <summary>
    /// Checks if the application has been granted Accessibility access.
    /// </summary>
    public static void EnsureAccessibilityTrusted()
    {
        // For sandboxed apps, this will always be false.
        // For non-sandboxed apps, it checks the system settings.
        var isTrusted = AXIsProcessTrustedWithOptions(new NSDictionary(AxTrustedCheckOptionPrompt, NSNumber.FromBoolean(true)));
        if (isTrusted) return;

        NativeMessageBox.Show(
            CoreLocaleResolver.Common_Info,
            LocaleResolver.MacOS_PermissionHelper_PleaseGrantAccessibilityPermission);
        Environment.Exit(0);
    }

    /// <summary>
    /// Requests screen recording permission by attempting to capture a minimal portion of the screen.
    /// https://stackoverflow.com/questions/59337022/enabling-screen-recording-api-in-catalina-kcgwindowname
    /// </summary>
    public static void RequestForScreenRecordingPermission()
    {
#pragma warning disable CA1422
        using var _ = CGImage.ScreenImage(0, new CGRect(0, 0, 1, 1), CGWindowListOption.OnScreenOnly, CGWindowImageOption.Default);
#pragma warning restore CA1422
    }

    // ReSharper disable once InconsistentNaming
    private static bool AXIsProcessTrustedWithOptions(NSDictionary options)
    {
        return AXIsProcessTrustedWithOptions(options.Handle);
    }

    // C# binding for the C function AXIsProcessTrustedWithOptions.
    [LibraryImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AXIsProcessTrustedWithOptions(nint options);
}