using System.Runtime.InteropServices;
using Avalonia.Controls;
using Everywhere.Interop;
using Everywhere.Chat.Links;

namespace Everywhere.Windows.Interop;

internal sealed partial class LinkFileOperations : ILinkFileOperations
{
    public Task<ChatLinkOperationStatus> OpenDirectoryAsync(string path, TopLevel owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new System.Diagnostics.ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(start);
        return Task.FromResult(process is null ? ChatLinkOperationStatus.Failed : ChatLinkOperationStatus.Accepted);
    }

    public Task<ChatLinkOperationStatus> RevealItemAsync(string path, bool isDirectory, TopLevel owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<ChatLinkOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Shell parsing can block on a slow share. Keep it off both the UI and the shared message window.
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(RevealFile(path, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled(cancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "RevealChatLink"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ChatLinkOperationStatus RevealFile(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const uint apartmentThreaded = 2;
        Marshal.ThrowExceptionForHR(CoInitializeEx(nint.Zero, apartmentThreaded));
        try
        {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, nint.Zero, out var item, 0, out _));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // With zero children, the PIDL identifies the single item to select in its parent folder.
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(item, 0, nint.Zero, 0));
                return ChatLinkOperationStatus.Accepted;
            }
            finally
            {
                Marshal.FreeCoTaskMem(item);
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    public Task<ChatLinkOperationStatus> OpenFileWithAsync(string path, TopLevel owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // OAIF_EXEC | OAIF_HIDE_REGISTRATION; never write a default association.
        var info = new OpenAsInfo { File = path, Flags = 0x00000004 | 0x00000020 };
        var result = SHOpenWithDialog(owner.TryGetPlatformHandle()?.Handle ?? nint.Zero, ref info);
        if (result is unchecked((int)0x800704c7) or unchecked((int)0x80004004))
        {
            return Task.FromResult(ChatLinkOperationStatus.Canceled);
        }

        Marshal.ThrowExceptionForHR(result);
        return Task.FromResult(ChatLinkOperationStatus.Accepted);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public uint Flags;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint binding, out nint item, uint attributes, out uint resultAttributes);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(nint folder, uint count, nint children, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHOpenWithDialog(nint owner, ref OpenAsInfo info);
}