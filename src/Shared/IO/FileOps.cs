using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace OddJobs.IO
{
    public static class FileOps
    {
        /// <summary>
        /// Renames or moves a file. Never overwrites: fails if the destination exists.
        /// Moves across drives are done as copy + delete by Windows, preserving timestamps.
        /// </summary>
        public static void Move(string from, string to)
        {
            if (!MoveFileExW(PathUtil.ToLongPath(from), PathUtil.ToLongPath(to), MOVEFILE_COPY_ALLOWED | MOVEFILE_WRITE_THROUGH))
                throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message + " (" + Path.GetFileName(from) + ")");
        }

        public static bool Exists(string path) => FastDir.TryGetEntry(path, out _);

        const uint MOVEFILE_COPY_ALLOWED = 0x2;
        const uint MOVEFILE_WRITE_THROUGH = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool MoveFileExW(string existing, string newName, uint flags);
    }
}
