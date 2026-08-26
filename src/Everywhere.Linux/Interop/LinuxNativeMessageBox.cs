using System.Diagnostics;
using System.Runtime.InteropServices;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;

internal static partial class LinuxNativeMessageBox
{
    public static NativeMessageBoxResult Show(string title, string message, NativeMessageBoxButtons buttons, NativeMessageBoxIcon icon)
    {
        // First try GTK3 (XFCE, GNOME).
        try
        {
            return Gtk3Interop.ShowMessageBox(title, message, buttons, icon);
        }
        catch (DllNotFoundException)
        {
        }

        // Then try KDE.
        if (KdeInterop.IsAvailable())
        {
            return KdeInterop.ShowMessageBox(title, message, buttons, icon);
        }

        // If no GUI toolkit is available, keep the diagnostic visible instead of crashing silently.
        Console.WriteLine($"[{icon}] {title}: {message}");
        if (buttons is NativeMessageBoxButtons.Ok or NativeMessageBoxButtons.OkCancel)
        {
            Console.WriteLine("Press Enter to continue...");
            Console.ReadLine();
            return NativeMessageBoxResult.Ok;
        }

        throw new PlatformNotSupportedException(
            "No suitable message box implementation found (GTK3 library missing, kdialog missing, and no handler provided).");
    }

    private static partial class Gtk3Interop
    {
        private const string LibGtk = "libgtk-3.so.0";

        private enum GtkMessageType
        {
            Info = 0,
            Warning = 1,
            Question = 2,
            Error = 3,
            Other = 4
        }

        private enum GtkButtonsType
        {
            None = 0,
            Ok = 1,
            // Close = 2,
            // Cancel = 3,
            YesNo = 4,
            OkCancel = 5
        }

        private enum GtkResponseType
        {
            // None = -1,
            Ok = -5,
            Cancel = -6,
            // Close = -7,
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
                NativeMessageBoxButtons.YesNoCancel => GtkButtonsType.None,
                _ => GtkButtonsType.Ok
            };

            var dialog = gtk_message_dialog_new(IntPtr.Zero, 0, gtkType, gtkButtons, "%s", message);
            if (dialog == IntPtr.Zero)
            {
                return NativeMessageBoxResult.None;
            }

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

        public static NativeMessageBoxResult ShowMessageBox(string title, string message, NativeMessageBoxButtons buttons, NativeMessageBoxIcon icon)
        {
            var args = new List<string>
            {
                $"--title \"{title.Replace("\"", "\\\"")}\""
            };

            var isYesNo = buttons is NativeMessageBoxButtons.YesNo or NativeMessageBoxButtons.YesNoCancel;
            var isOkCancel = buttons == NativeMessageBoxButtons.OkCancel;
            string typeSwitch;

            if (isYesNo)
            {
                typeSwitch = "--yesno";
            }
            else if (isOkCancel)
            {
                typeSwitch = "--warningcontinuecancel";
            }
            else
            {
                typeSwitch = icon switch
                {
                    NativeMessageBoxIcon.Error => "--error",
                    NativeMessageBoxIcon.Stop => "--error",
                    NativeMessageBoxIcon.Hand => "--error",
                    NativeMessageBoxIcon.Warning => "--sorry",
                    _ => "--msgbox"
                };
            }

            args.Add(typeSwitch);
            args.Add($"\"{message.Replace("\"", "\\\"")}\"");

            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "kdialog",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                foreach (var argument in args)
                {
                    processStartInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    return NativeMessageBoxResult.None;
                }

                process.WaitForExit();
                var exitCode = process.ExitCode;
                if (isYesNo)
                {
                    return exitCode == 0 ? NativeMessageBoxResult.Yes : NativeMessageBoxResult.No;
                }

                if (isOkCancel)
                {
                    return exitCode == 0 ? NativeMessageBoxResult.Ok : NativeMessageBoxResult.Cancel;
                }

                return exitCode == 0 ? NativeMessageBoxResult.Ok : NativeMessageBoxResult.None;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Failed to run kdialog: {exception.Message}");
                return NativeMessageBoxResult.None;
            }
        }
    }
}