using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace OddJobs.IO
{
    public static class Shell
    {
        /// <summary>Opens Explorer with the item selected.</summary>
        public static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            // Windows paths cannot contain quotes, so this argument cannot be broken out of.
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else if (Directory.Exists(Path.GetDirectoryName(path)))
                OpenFolder(Path.GetDirectoryName(path));
        }

        public static void OpenFolder(string folder)
        {
            if (Directory.Exists(folder))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }

        /// <summary>
        /// Opens a document with its default app. Anything that could run code is revealed in Explorer instead,
        /// so a stray double-click in a list can never launch a program.
        /// </summary>
        public static void OpenSafely(string path)
        {
            if (!File.Exists(path)) return;
            if (PathUtil.IsRunnable(path)) { Reveal(path); return; }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        public static string DownloadsFolder
        {
            get
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    try { return Marshal.PtrToStringUni(p); }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);
    }

    /// <summary>
    /// Moves files and folders to the Recycle Bin using the same engine as Explorer (IFileOperation),
    /// so everything can be restored. Nothing in this project ever deletes permanently.
    /// </summary>
    public static class RecycleBin
    {
        /// <summary>Returns the paths that are still present afterwards (could not be recycled).</summary>
        public static List<string> Send(IEnumerable<string> paths, IntPtr ownerWindow)
        {
            var list = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) return new List<string>();

            // The shell's file operation object wants a single-threaded apartment. Background callers get one.
            if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
            {
                List<string> result = null;
                Exception error = null;
                var thread = new System.Threading.Thread(() =>
                {
                    try { result = Send(list, ownerWindow); }
                    catch (Exception ex) { error = ex; }
                });
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                thread.Join();
                if (error != null) throw new IOException("Could not use the Recycle Bin: " + error.Message, error);
                return result;
            }

            var op = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_FileOperation));
            try
            {
                op.SetOperationFlags(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING | FOFX_RECYCLEONDELETE);
                if (ownerWindow != IntPtr.Zero) op.SetOwnerWindow(ownerWindow);
                var iid = typeof(IShellItem).GUID;
                foreach (var path in list)
                {
                    if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item) == 0)
                    {
                        op.DeleteItem(item, IntPtr.Zero);
                        Marshal.ReleaseComObject(item);
                    }
                }
                try { op.PerformOperations(); }
                catch (COMException) { /* partial failure: reported below */ }
            }
            finally
            {
                Marshal.ReleaseComObject(op);
            }
            return list.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        }

        static readonly Guid CLSID_FileOperation = new Guid("3ad05575-8857-4850-9277-11b85bdb8e09");

        const uint FOF_SILENT = 0x0004;
        const uint FOF_NOCONFIRMATION = 0x0010;
        const uint FOF_ALLOWUNDO = 0x0040;
        const uint FOF_NOERRORUI = 0x0400;
        // If something can't go to the Recycle Bin (USB stick, network drive, too large), Windows asks first instead of deleting it.
        const uint FOF_WANTNUKEWARNING = 0x4000;
        const uint FOFX_RECYCLEONDELETE = 0x00080000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOperation
        {
            uint Advise(IntPtr pfops);
            void Unadvise(uint dwCookie);
            void SetOperationFlags(uint dwOperationFlags);
            void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
            void SetProgressDialog(IntPtr popd);
            void SetProperties(IntPtr pproparray);
            void SetOwnerWindow(IntPtr hwndOwner);
            void ApplyPropertiesToItem(IShellItem psiItem);
            void ApplyPropertiesToItems(IntPtr punkItems);
            void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
            void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
            void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
            void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
            void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
            void DeleteItems(IntPtr punkItems);
            uint NewItem(IShellItem psiDestinationFolder, FileAttributes dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
            void PerformOperations();
            [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
        }
    }
}
