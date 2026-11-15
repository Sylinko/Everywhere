using System.Runtime.InteropServices;
using Everywhere.Interop;

namespace Everywhere.Windows.Interop;

internal static partial class WindowsNativeMessageBox
{
    private enum MessageBoxResult
    {
        Ok = 1,
        Cancel = 2,
        Yes = 6,
        No = 7,
        Retry = 4,
        Ignore = 5
    }

    [Flags]
    private enum MessageBoxTypes
    {
        None = 0x00000000,

        Ok = None,
        OkCancel = 0x00000001,
        YesNo = 0x00000004,
        YesNoCancel = 0x00000003,
        RetryCancel = 0x00000005,
        AbortRetryIgnore = 0x00000002,

        Information = 0x00000040,
        Warning = 0x00000030,
        Error = 0x00000010,
        Question = 0x00000020,
        Stop = Error,
        Hand = Error,
        Asterisk = Information
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial MessageBoxResult MessageBox(IntPtr hWnd, string text, string caption, MessageBoxTypes type);

    public static NativeMessageBoxResult Show(
        string title,
        string message,
        NativeMessageBoxButtons buttons,
        NativeMessageBoxIcon icon)
    {
        var buttonFlags = buttons switch
        {
            NativeMessageBoxButtons.Ok => MessageBoxTypes.Ok,
            NativeMessageBoxButtons.OkCancel => MessageBoxTypes.OkCancel,
            NativeMessageBoxButtons.YesNo => MessageBoxTypes.YesNo,
            NativeMessageBoxButtons.YesNoCancel => MessageBoxTypes.YesNoCancel,
            NativeMessageBoxButtons.RetryCancel => MessageBoxTypes.RetryCancel,
            NativeMessageBoxButtons.AbortRetryIgnore => MessageBoxTypes.AbortRetryIgnore,
            _ => MessageBoxTypes.None
        };

        var iconFlags = icon switch
        {
            NativeMessageBoxIcon.Information => MessageBoxTypes.Information,
            NativeMessageBoxIcon.Warning => MessageBoxTypes.Warning,
            NativeMessageBoxIcon.Error => MessageBoxTypes.Error,
            NativeMessageBoxIcon.Question => MessageBoxTypes.Question,
            NativeMessageBoxIcon.Stop => MessageBoxTypes.Stop,
            NativeMessageBoxIcon.Hand => MessageBoxTypes.Hand,
            NativeMessageBoxIcon.Asterisk => MessageBoxTypes.Asterisk,
            _ => MessageBoxTypes.None
        };

        var result = MessageBox(IntPtr.Zero, message, title, buttonFlags | iconFlags);
        return result switch
        {
            MessageBoxResult.Ok => NativeMessageBoxResult.Ok,
            MessageBoxResult.Cancel => NativeMessageBoxResult.Cancel,
            MessageBoxResult.Yes => NativeMessageBoxResult.Yes,
            MessageBoxResult.No => NativeMessageBoxResult.No,
            MessageBoxResult.Retry => NativeMessageBoxResult.Retry,
            MessageBoxResult.Ignore => NativeMessageBoxResult.Ignore,
            _ => NativeMessageBoxResult.None
        };
    }
}