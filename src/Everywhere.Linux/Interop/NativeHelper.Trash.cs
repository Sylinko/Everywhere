using System.Runtime.InteropServices;

namespace Everywhere.Linux.Interop;

public partial class NativeHelper
{
    /// <inheritdoc />
    public Task MoveToTrashAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                MoveToTrash(paths, cancellationToken);
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("The system trash service is unavailable.", exception);
            }
        }, cancellationToken);
    }

    private static void MoveToTrash(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var cancellable = TrashNativeMethods.g_cancellable_new();
        try
        {
            // Dispose the registration before releasing the native object it calls into.
            using var registration = cancellationToken.Register(() => TrashNativeMethods.g_cancellable_cancel(cancellable));
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = TrashNativeMethods.g_file_new_for_path(path);
                try
                {
                    if (TrashNativeMethods.g_file_trash(file, cancellable, out var error) != 0)
                    {
                        continue;
                    }

                    try
                    {
                        const int CanceledError = 19; // G_IO_ERROR_CANCELLED
                        if (TrashNativeMethods.g_error_matches(error, TrashNativeMethods.g_io_error_quark(), CanceledError) != 0)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }
                        var detail = Marshal.PtrToStructure<TrashNativeMethods.GError>(error).Message;
                        throw new IOException($"Could not move '{path}' to the trash: {detail}");
                    }
                    finally
                    {
                        TrashNativeMethods.g_error_free(error);
                    }
                }
                finally
                {
                    TrashNativeMethods.g_object_unref(file);
                }
            }
        }
        finally
        {
            TrashNativeMethods.g_object_unref(cancellable);
        }
    }

    private static partial class TrashNativeMethods
    {
        [LibraryImport("libgio-2.0.so.0")]
        public static partial nint g_cancellable_new();

        [LibraryImport("libgio-2.0.so.0")]
        public static partial void g_cancellable_cancel(nint cancellable);

        [LibraryImport("libgio-2.0.so.0", StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint g_file_new_for_path(string path);

        [LibraryImport("libgio-2.0.so.0")]
        public static partial int g_file_trash(nint file, nint cancellable, out nint error);

        [LibraryImport("libgio-2.0.so.0")]
        public static partial uint g_io_error_quark();

        [LibraryImport("libglib-2.0.so.0")]
        public static partial int g_error_matches(nint error, uint domain, int code);

        [LibraryImport("libglib-2.0.so.0")]
        public static partial void g_error_free(nint error);

        [LibraryImport("libgobject-2.0.so.0")]
        public static partial void g_object_unref(nint instance);

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct GError
        {
            public string Message => Marshal.PtrToStringUTF8(_message) ?? "Unknown GIO error.";

            private readonly uint _domain;
            private readonly int _code;
            private readonly nint _message;
        }
    }
}