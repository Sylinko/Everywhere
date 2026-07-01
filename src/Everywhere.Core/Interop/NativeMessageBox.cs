#if LINUX
using System.Diagnostics;
#endif

using Everywhere.Common;

namespace Everywhere.Interop;

public enum NativeMessageBoxResult
{
    None,
    Ok,
    Cancel,
    Yes,
    No,
    Retry,
    Ignore
}

public enum NativeMessageBoxButtons
{
    None,
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
    RetryCancel,
    AbortRetryIgnore
}

public enum NativeMessageBoxIcon
{
    None,
    Information,
    Warning,
    Error,
    Question,
    Stop,
    Hand,
    Asterisk
}

public static partial class NativeMessageBox
{
    /// <summary>
    /// Gets an exception handler that shows exceptions in a native message box.
    /// </summary>
    public static IExceptionHandler ExceptionHandler { get; } = new ExceptionHandlerImpl();

    private class ExceptionHandlerImpl : IExceptionHandler
    {
        public void HandleException(Exception exception, string? message = null, object? source = null, int lineNumber = 0)
        {
            Show(
                $"Error at [{source}:{lineNumber}]",
                $"{message ?? "An error occurred."}\n\n{exception.GetFriendlyMessage()}",
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Error);
        }
    }

    public static NativeMessageBoxResult Show(
        string title,
        string message,
        NativeMessageBoxButtons buttons = NativeMessageBoxButtons.Ok,
        NativeMessageBoxIcon icon = NativeMessageBoxIcon.None)
    {
#if WINDOWS
        return ShowWindowsMessageBox(title, message, buttons, icon);
#elif MACOS
        return ShowMacOSMessageBox(title, message, buttons, icon);
#elif LINUX
        return ShowLinuxMessageBox(title, message, buttons, icon);
#else
        #error Platform not supported
        throw new PlatformNotSupportedException();
#endif
    }

#if WINDOWS
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

    private static NativeMessageBoxResult ShowWindowsMessageBox(
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
#endif

#if MACOS
    /// <summary>
    /// Gets or sets a custom handler for showing message boxes on macOS.
    /// The implementation depends on Xamarin.Mac so we leave it to the application to provide one.
    /// </summary>
    public static Func<string, string, NativeMessageBoxButtons, NativeMessageBoxIcon, NativeMessageBoxResult>? MacOSMessageBoxHandler { get; set; }

    private static NativeMessageBoxResult ShowMacOSMessageBox(
        string title,
        string message,
        NativeMessageBoxButtons buttons,
        NativeMessageBoxIcon icon)
    {
        return MacOSMessageBoxHandler?.Invoke(title, message, buttons, icon) ??
            throw new SystemException("!!FIXME: macOS message box is not implemented");
    }
#endif

#if LINUX
    private static NativeMessageBoxResult ShowLinuxMessageBox(string title, string message, NativeMessageBoxButtons buttons, NativeMessageBoxIcon icon)
    {
        // First try GTK3 (xfce, gnome)
        try
        {
            return Gtk3Interop.ShowMessageBox(title, message, buttons, icon);
        }
        catch (DllNotFoundException)
        {
            // Ignore
        }

        // Then try KDE
        if (KdeInterop.IsAvailable())
        {
            return KdeInterop.ShowMessageBox(title, message, buttons, icon);
        }

        // Final Fallback: Console
        // If no GUI toolkits are found, we shouldn't crash.
        Console.WriteLine($"[{icon}] {title}: {message}");
        if (buttons is NativeMessageBoxButtons.Ok or NativeMessageBoxButtons.OkCancel)
        {
            Console.WriteLine("Press Enter to continue...");
            Console.ReadLine();
            return NativeMessageBoxResult.Ok;
        }

        throw new PlatformNotSupportedException(
            "No suitable message box implementation found (GTK3 library missing, kdialog missing, and no Handler provided).");
    }

    private static partial class Gtk3Interop
    {
        private const string LibGtk = "libgtk-3.so.0";

        // GtkMessageType
        private enum GtkMessageType
        {
            Info = 0,
            Warning = 1,
            Question = 2,
            Error = 3,
            Other = 4
        }

        // GtkButtonsType
        private enum GtkButtonsType
        {
            None = 0,
            Ok = 1,
            Close = 2,
            Cancel = 3,
            YesNo = 4,
            OkCancel = 5
        }

        // GtkResponseType
        private enum GtkResponseType
        {
            None = -1,
            Ok = -5,
            Cancel = -6,
            Close = -7,
            Yes = -8,
            No = -9
        }

        [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static partial bool gtk_init_check(IntPtr argc, IntPtr argv);

        [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
        private static partial void gtk_window_set_title(IntPtr window, string title);

        [LibraryImport(LibGtk)]
        private static partial int gtk_dialog_run(IntPtr dialog);

        [LibraryImport(LibGtk)]
        private static partial void gtk_widget_destroy(IntPtr widget);

        // English ver
        // To support more complex MessageFormat, we would typically call set_markup or format_secondary_text.
        // However, to keep the interop simple, we put the message into format and pass null as arg (if message contains no format specifiers).
        // Alternatively, we can set format to "%s" and pass message to arg.
        // But LibraryImport does not support __arglist (varargs).
        // Workaround: directly use gtk_message_dialog_new(..., message, null) as long as message contains no %,
        // or define a specific import that accepts two strings.

        [LibraryImport(LibGtk, EntryPoint = "gtk_message_dialog_new", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr gtk_message_dialog_new(
            IntPtr parent,
            int flags,
            GtkMessageType type,
            GtkButtonsType buttons,
            string format,
            string message);

        public static NativeMessageBoxResult ShowMessageBox(
            string title,
            string message,
            NativeMessageBoxButtons buttons,
            NativeMessageBoxIcon icon)
        {
            if (!gtk_init_check(IntPtr.Zero, IntPtr.Zero))
            {
                Console.Error.WriteLine("Error: Unable to initialize GTK (no display?).");
                return NativeMessageBoxResult.None;
            }

            var gtkType = icon switch
            {
                NativeMessageBoxIcon.Information => GtkMessageType.Info,
                NativeMessageBoxIcon.Asterisk => GtkMessageType.Info,
                NativeMessageBoxIcon.Warning => GtkMessageType.Warning,
                NativeMessageBoxIcon.Error => GtkMessageType.Error,
                NativeMessageBoxIcon.Hand => GtkMessageType.Error,
                NativeMessageBoxIcon.Stop => GtkMessageType.Error,
                NativeMessageBoxIcon.Question => GtkMessageType.Question,
                _ => GtkMessageType.Other
            };

            var gtkButtons = buttons switch
            {
                NativeMessageBoxButtons.Ok => GtkButtonsType.Ok,
                NativeMessageBoxButtons.OkCancel => GtkButtonsType.OkCancel,
                NativeMessageBoxButtons.YesNo => GtkButtonsType.YesNo,
                NativeMessageBoxButtons.YesNoCancel => GtkButtonsType.None, // TODO
                _ => GtkButtonsType.Ok
            };

            // flags: 0 (Modal) or 1 (DestroyWithParent)
            // Use %s for message displaying, actual formatting is implemented by C#.
            var dialog = gtk_message_dialog_new(IntPtr.Zero, 0, gtkType, gtkButtons, "%s", message);
            if (dialog == IntPtr.Zero) return NativeMessageBoxResult.None;

            try
            {
                gtk_window_set_title(dialog, title);
                var responseId = gtk_dialog_run(dialog);
                return responseId switch
                {
                    (int)GtkResponseType.Ok => NativeMessageBoxResult.Ok,
                    (int)GtkResponseType.Cancel => NativeMessageBoxResult.Cancel,
                    (int)GtkResponseType.Yes => NativeMessageBoxResult.Yes,
                    (int)GtkResponseType.No => NativeMessageBoxResult.No,
                    _ => NativeMessageBoxResult.None
                };
            }
            finally
            {
                gtk_widget_destroy(dialog);
            }
        }
    }

    private static class KdeInterop
    {
        // Check if kdialog is available in the PATH
        public static bool IsAvailable()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "kdialog",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                process?.WaitForExit();
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        public static NativeMessageBoxResult ShowMessageBox(
            string title,
            string message,
            NativeMessageBoxButtons buttons,
            NativeMessageBoxIcon icon)
        {
            // kdialog arguments builder
            var args = new List<string>
            {
                $"--title \"{title.Replace("\"", "\\\"")}\""
            };

            // Mapping Logic:
            // kdialog changes its mode based on the switch (e.g., --msgbox, --error, --yesno).
            // We must prioritize the 'Buttons' logic because it determines the return value interaction.

            bool isYesNo = buttons == NativeMessageBoxButtons.YesNo || buttons == NativeMessageBoxButtons.YesNoCancel;
            bool isOkCancel = buttons == NativeMessageBoxButtons.OkCancel;

            string typeSwitch = "--msgbox"; // Default to simple Info/OK

            if (isYesNo)
            {
                // --yesno provides Yes/No buttons.
                // kdialog returns 0 for Yes, 1 for No.
                typeSwitch = "--yesno";

                // Note: --yesno usually implies a 'Question' icon in KDE,
                // but we can try to force an icon if strictly needed, though kdialog command structure is rigid.
                // For this implementation, we rely on the button mode determining the dialog type.
            }
            else if (isOkCancel)
            {
                // --warningcontinuecancel or --warningyesno could be used, but standard "Ok/Cancel"
                // is best approximated by warningcontinuecancel in kdialog semantics, or just --yesno rewritten.
                // However, simply using --warningcontinuecancel fits best for "Action/Cancel".
                typeSwitch = "--warningcontinuecancel";
            }
            else
            {
                // For OK-only buttons, we change the dialog style based on the icon.
                typeSwitch = icon switch
                {
                    NativeMessageBoxIcon.Error => "--error",
                    NativeMessageBoxIcon.Stop => "--error",
                    NativeMessageBoxIcon.Hand => "--error",
                    NativeMessageBoxIcon.Warning => "--sorry", // KDE "Sorry" is often used for warnings
                    NativeMessageBoxIcon.Information => "--msgbox",
                    _ => "--msgbox"
                };
            }

            args.Add(typeSwitch);
            args.Add($"\"{message.Replace("\"", "\\\"")}\"");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "kdialog",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var arg in args) psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi);
                if (process == null) return NativeMessageBoxResult.None;

                process.WaitForExit();
                int exitCode = process.ExitCode;

                // Map kdialog exit codes to NativeMessageBoxResult
                if (isYesNo)
                {
                    // 0 = Yes, 1 = No
                    return exitCode == 0 ? NativeMessageBoxResult.Yes : NativeMessageBoxResult.No;
                }
                else if (isOkCancel)
                {
                    // 0 = Continue (Ok), 1 = Cancel
                    return exitCode == 0 ? NativeMessageBoxResult.Ok : NativeMessageBoxResult.Cancel;
                }
                else
                {
                    // For Info/Error boxes (OK button only)
                    // 0 = OK
                    return exitCode == 0 ? NativeMessageBoxResult.Ok : NativeMessageBoxResult.None;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to run kdialog: {ex.Message}");
                return NativeMessageBoxResult.None;
            }
        }
    }
#endif
}