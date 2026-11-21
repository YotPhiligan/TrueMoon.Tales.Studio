using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TrueMoon.Tales.Studio.Backend;

internal static class WindowsRecycleBin
{
    [SupportedOSPlatform("windows6.2")]
    internal static void MoveFile(string path)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Recycle(path);
                completed.SetResult();
            }
            catch (Exception error) { completed.SetException(error); }
        });
        // IFileOperation must execute in a Shell-compatible single-threaded apartment.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Task.GetAwaiter().GetResult();
    }

    [SupportedOSPlatform("windows6.2")]
    private static void Recycle(string path)
    {
        var itemId = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
        var operationType = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), throwOnError: true)!;
        var operation = (IFileOperation)Activator.CreateInstance(operationType)!;
        try
        {
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, in itemId, out var pointer));
            try
            {
                const uint silentWithoutConfirmation = 0x0004 | 0x0010 | 0x0400 | 0x00100000;
                const uint recycleOnDelete = 0x00080000;
                operation.SetOperationFlags(silentWithoutConfirmation | recycleOnDelete);
                operation.DeleteItem(pointer, 0);
                operation.PerformOperations();
                operation.GetAnyOperationsAborted(out var aborted);
                if (aborted || File.Exists(path)) throw new IOException("Windows could not recycle the file.");
            }
            finally { Marshal.Release(pointer); }
        }
        finally { Marshal.ReleaseComObject(operation); }
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);

    // Unused slots are kept to match the IFileOperation vtable.
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise();
        void Unadvise();
        void SetOperationFlags(uint flags);
        void SetProgressMessage();
        void SetProgressDialog();
        void SetProperties();
        void SetOwnerWindow();
        void ApplyPropertiesToItem();
        void ApplyPropertiesToItems();
        void RenameItem();
        void RenameItems();
        void MoveItem();
        void MoveItems();
        void CopyItem();
        void CopyItems();
        void DeleteItem(nint item, nint sink);
        void DeleteItems();
        void NewItem();
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
