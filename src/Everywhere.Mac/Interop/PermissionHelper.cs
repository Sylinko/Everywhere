using System.Runtime.InteropServices;
using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Main-only helper for managing macOS permissions that require user-facing UI.
/// </summary>
public static class PermissionHelper
{
    /// <summary>
    /// Checks Accessibility permission, shows the existing localized prompt when
    /// it is absent, and exits after the user has been told to restart.
    /// </summary>
    public static void EnsureAccessibilityTrusted()
    {
        if (AccessibilityPermission.IsTrusted(prompt: true)) return;

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
}

/// <summary>
/// Lightweight macOS Accessibility API wrapper usable by a headless Host.
/// It never prompts, localizes, displays UI, or terminates the process.
/// </summary>
public static partial class AccessibilityPermission
{
    private static readonly NSString AxTrustedCheckOptionPrompt = new("AXTrustedCheckOptionPrompt");

    /// <summary>Returns whether the current process is trusted by macOS Accessibility.</summary>
    /// <param name="prompt">Whether macOS may display its system permission prompt.</param>
    public static bool IsTrusted(bool prompt)
    {
        using var options = new NSDictionary(AxTrustedCheckOptionPrompt, NSNumber.FromBoolean(prompt));
        return AXIsProcessTrustedWithOptions(options);
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