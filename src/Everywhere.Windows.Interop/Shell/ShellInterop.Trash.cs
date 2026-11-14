using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Everywhere.Windows.Interop.Shell;

/// <summary>
/// Provides common shell interop operations.
/// </summary>
public static unsafe class ShellInterop
{
    /// <summary>
    /// Synchronously recycles a batch of paths on the calling STA, including native progress UI.
    /// Cancellation is best effort and never rolls back completed items. The method returns only
    /// after native work stops; cancellation after all items succeeded does not override success.
    /// </summary>
    /// <remarks>
    /// The caller supplies the STA thread. This method balances its own COM initialization and
    /// releases all owned native references on that thread before returning.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The token or native progress UI canceled the operation.</exception>
    /// <exception cref="IOException">An item could not be recycled; earlier items may have completed.</exception>
    public static void MoveToTrash(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (paths.Count == 0)
        {
            return;
        }

        PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED).ThrowOnFailure();
        var pOperation = (IFileOperation*)null;
        var pCallback = (IFileOperationProgressSink*)null;
        var sink = new TrashProgressSink(cancellationToken);
        try
        {
            FileOperation.CreateInstance(out pOperation).ThrowOnFailure();
            pOperation->SetOperationFlags(
                FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE | FILEOPERATION_FLAGS.FOFX_ADDUNDORECORD |
                FILEOPERATION_FLAGS.FOF_NOCONFIRMATION | FILEOPERATION_FLAGS.FOF_NOERRORUI |
                FILEOPERATION_FLAGS.FOFX_EARLYFAILURE | FILEOPERATION_FLAGS.FOF_NO_CONNECTED_ELEMENTS);
            pCallback = TrashProgressSinkWrappers.CreateReference(sink);
            pOperation->Advise(pCallback, out var cookie);

            try
            {
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PInvoke.SHCreateItemFromParsingName(path, null, out IShellItem* item).ThrowOnFailure();
                    try
                    {
                        pOperation->DeleteItem(item, null);
                    }
                    finally
                    {
                        item->Release();
                    }
                }

                pOperation->PerformOperations();
                if (sink.Error is { } error)
                {
                    throw error;
                }

                pOperation->GetAnyOperationsAborted(out var hasAborted);
                if (hasAborted)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
            finally
            {
                pOperation->Unadvise(cookie);
            }
        }
        catch (COMException exception)
        {
            if (sink.Error is { } error)
            {
                throw error;
            }

            if (TrashProgressSink.IsCancellation(exception.HResult))
            {
                throw new OperationCanceledException("Moving files to the trash was canceled.", exception, cancellationToken);
            }

            throw new IOException("Could not move files to the Recycle Bin.", exception);
        }
        finally
        {
            if (pOperation is not null)
            {
                pOperation->Release();
            }

            if (pCallback is not null)
            {
                pCallback->Release();
            }

            PInvoke.CoUninitialize();
        }
    }

    private sealed class TrashProgressSink(CancellationToken cancellationToken) : IFileOperationProgressSink.Interface
    {
        public IOException? Error { get; private set; }

        private const int CanceledHResult = unchecked((int)0x800704C7);
        private const int UserCanceledHResult = unchecked((int)0x80270000);
        private const int AbortHResult = unchecked((int)0x80004004);

        public static bool IsCancellation(int result) => result is CanceledHResult or UserCanceledHResult or AbortHResult;

        public HRESULT PreDeleteItem(uint dwFlags, IShellItem* psiItem)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return (HRESULT)CanceledHResult;
            }

            // The Shell can propose permanent deletion when recycling is unavailable. Veto it before any deletion.
            if ((dwFlags & (uint)_TRANSFER_SOURCE_FLAGS.TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0)
            {
                Error = new IOException("The item cannot be recycled. Permanent deletion is not allowed.");
                return (HRESULT)unchecked((int)0x80004005);
            }

            return default;
        }

        public HRESULT PostDeleteItem(uint dwFlags, IShellItem* psiItem, HRESULT hrDelete, IShellItem* psiNewlyCreated)
        {
            RecordFailure(hrDelete);
            return default;
        }

        public HRESULT UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
            return iWorkSoFar < iWorkTotal && cancellationToken.IsCancellationRequested ? (HRESULT)CanceledHResult : default;
        }

        public HRESULT FinishOperations(HRESULT hrResult)
        {
            RecordFailure(hrResult);
            return default;
        }

        private void RecordFailure(HRESULT result)
        {
            if (result.Value < 0 && !IsCancellation(result.Value))
            {
                Error ??= new IOException("Could not move an item to the Recycle Bin.", Marshal.GetExceptionForHR(result.Value));
            }
        }

        public HRESULT StartOperations()
        {
            return default;
        }

        public HRESULT PreRenameItem(uint dwFlags, IShellItem* psiItem, PCWSTR pszNewName)
        {
            return default;
        }

        public HRESULT PostRenameItem(uint dwFlags, IShellItem* psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem* psiNewlyCreated)
        {
            return default;
        }

        public HRESULT PreMoveItem(uint dwFlags, IShellItem* psiItem, IShellItem* psiDestinationFolder, PCWSTR pszNewName)
        {
            return default;
        }

        public HRESULT PostMoveItem(
            uint dwFlags,
            IShellItem* psiItem,
            IShellItem* psiDestinationFolder,
            PCWSTR pszNewName,
            HRESULT hrMove,
            IShellItem* psiNewlyCreated)
        {
            return default;
        }

        public HRESULT PreCopyItem(uint dwFlags, IShellItem* psiItem, IShellItem* psiDestinationFolder, PCWSTR pszNewName)
        {
            return default;
        }

        public HRESULT PostCopyItem(
            uint dwFlags,
            IShellItem* psiItem,
            IShellItem* psiDestinationFolder,
            PCWSTR pszNewName,
            HRESULT hrCopy,
            IShellItem* psiNewlyCreated)
        {
            return default;
        }

        public HRESULT PreNewItem(uint dwFlags, IShellItem* psiDestinationFolder, PCWSTR pszNewName)
        {
            return default;
        }

        public HRESULT PostNewItem(
            uint dwFlags,
            IShellItem* psiDestinationFolder,
            PCWSTR pszNewName,
            PCWSTR pszTemplateName,
            uint dwFileAttributes,
            HRESULT hrNew,
            IShellItem* psiNewItem)
        {
            return default;
        }

        public HRESULT ResetTimer()
        {
            return default;
        }

        public HRESULT PauseTimer()
        {
            return default;
        }

        public HRESULT ResumeTimer()
        {
            return default;
        }
    }

    // Only exposes the trash callback to COM. CsWin32 supplies the ABI thunks and exception
    // boundary; ComWrappers supplies IUnknown identity and reference counting without RCWs/CCWs
    // from the runtime marshaller. The single vtable lives for this type's lifetime.
    private sealed class TrashProgressSinkWrappers : ComWrappers
    {
        private static readonly TrashProgressSinkWrappers Instance = new();
        private static readonly ComInterfaceEntry* Entry = CreateEntry();

        public static IFileOperationProgressSink* CreateReference(TrashProgressSink sink)
        {
            var unknown = (IUnknown*)Instance.GetOrCreateComInterfaceForObject(sink, CreateComInterfaceFlags.None);
            try
            {
                unknown->QueryInterface(out IFileOperationProgressSink* callback).ThrowOnFailure();
                return callback;
            }
            finally
            {
                unknown->Release();
            }
        }

        private static ComInterfaceEntry* CreateEntry()
        {
            var vtable = (IFileOperationProgressSink.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(
                typeof(TrashProgressSinkWrappers),
                sizeof(IFileOperationProgressSink.Vtbl));
            GetIUnknownImpl(out var queryInterface, out var addRef, out var release);
            var slots = (nint*)vtable;
            slots[0] = queryInterface;
            slots[1] = addRef;
            slots[2] = release;
            IFileOperationProgressSink.PopulateVTable(vtable);

            var entry = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(
                typeof(TrashProgressSinkWrappers),
                sizeof(ComInterfaceEntry));
            entry->IID = IFileOperationProgressSink.IID_Guid;
            entry->Vtable = (nint)vtable;
            return entry;
        }

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            count = 1;
            return Entry;
        }

        protected override object CreateObject(nint externalComObject, CreateObjectFlags flags)
        {
            throw new NotSupportedException();
        }

        protected override void ReleaseObjects(IEnumerable objects)
        {
            throw new NotSupportedException();
        }
    }
}