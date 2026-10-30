using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OddJobs.IO
{
    public struct DirEntry
    {
        public string Name;
        public FileAttributes Attributes;
        public long Size;
        public long LastWriteUtcTicks;
        public long CreationUtcTicks;

        public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
        public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
        public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
        public bool IsSystem => (Attributes & FileAttributes.System) != 0;

        /// <summary>OneDrive / cloud placeholder that is not actually stored on this disk.</summary>
        public bool IsCloudOnly => ((uint)Attributes & (FastDir.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FastDir.FILE_ATTRIBUTE_RECALL_ON_OPEN | (uint)FileAttributes.Offline)) != 0;

        public DateTime LastWriteUtc => DateTime.FromFileTimeUtc(LastWriteUtcTicks);
        public DateTime LastWriteLocal => DateTime.FromFileTimeUtc(LastWriteUtcTicks).ToLocalTime();
    }

    /// <summary>
    /// Directory listing straight from FindFirstFileEx: one system call per batch of entries,
    /// sizes and dates included, and long-path safe. Several times faster than DirectoryInfo.
    /// Never follows junctions or symbolic links into other folders.
    /// </summary>
    public static class FastDir
    {
        internal const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
        internal const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

        /// <summary>Lists one folder. Returns false (and an empty list) if it cannot be read.</summary>
        public static bool TryList(string directory, List<DirEntry> into, out int win32Error)
        {
            into.Clear();
            win32Error = 0;
            string pattern = PathUtil.ToLongPath(directory.TrimEnd('\\') + "\\*");
            using (var handle = FindFirstFileExW(pattern, FINDEX_INFO_LEVELS.FindExInfoBasic, out WIN32_FIND_DATAW data,
                       FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH))
            {
                if (handle.IsInvalid)
                {
                    win32Error = Marshal.GetLastWin32Error();
                    // An empty folder reports "file not found"; that is not an error.
                    return win32Error == ERROR_FILE_NOT_FOUND || win32Error == ERROR_NO_MORE_FILES;
                }
                do
                {
                    string name = data.cFileName;
                    if (name == "." || name == "..") continue;
                    into.Add(new DirEntry
                    {
                        Name = name,
                        Attributes = (FileAttributes)data.dwFileAttributes,
                        Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                        LastWriteUtcTicks = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime,
                        CreationUtcTicks = ((long)data.ftCreationTime.dwHighDateTime << 32) | data.ftCreationTime.dwLowDateTime,
                    });
                } while (FindNextFileW(handle, out data));
            }
            return true;
        }

        /// <summary>
        /// Walks a folder tree depth first and yields every file. Folders that cannot be read are reported
        /// through <paramref name="onError"/> and skipped. Reparse-point folders are never entered.
        /// </summary>
        public static IEnumerable<(string Path, DirEntry Entry)> WalkFiles(string root, bool includeHidden, Func<bool> cancelled, Action<string> onError = null, Func<string, DirEntry, bool> enterFolder = null)
        {
            var stack = new Stack<string>();
            stack.Push(root.TrimEnd('\\'));
            var buffer = new List<DirEntry>(256);
            while (stack.Count > 0)
            {
                if (cancelled != null && cancelled()) yield break;
                string dir = stack.Pop();
                if (!TryList(dir, buffer, out _))
                {
                    onError?.Invoke(dir);
                    continue;
                }
                var entries = buffer.ToArray();
                for (int i = entries.Length - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (!includeHidden && (e.IsHidden || e.IsSystem)) continue;
                    string full = dir + "\\" + e.Name;
                    if (e.IsDirectory)
                    {
                        if (e.IsReparsePoint) continue;
                        if (enterFolder != null && !enterFolder(full, e)) continue;
                        stack.Push(full);
                    }
                }
                foreach (var e in entries)
                {
                    if (e.IsDirectory) continue;
                    if (!includeHidden && (e.IsHidden || e.IsSystem)) continue;
                    yield return (dir + "\\" + e.Name, e);
                }
            }
        }

        public static bool TryGetEntry(string path, out DirEntry entry)
        {
            entry = default;
            using (var handle = FindFirstFileExW(PathUtil.ToLongPath(path), FINDEX_INFO_LEVELS.FindExInfoBasic, out WIN32_FIND_DATAW data,
                       FINDEX_SEARCH_OPS.FindExSearchNameMatch, IntPtr.Zero, 0))
            {
                if (handle.IsInvalid) return false;
                entry = new DirEntry
                {
                    Name = data.cFileName,
                    Attributes = (FileAttributes)data.dwFileAttributes,
                    Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                    LastWriteUtcTicks = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime,
                    CreationUtcTicks = ((long)data.ftCreationTime.dwHighDateTime << 32) | data.ftCreationTime.dwLowDateTime,
                };
                return true;
            }
        }

        const int ERROR_FILE_NOT_FOUND = 2;
        const int ERROR_NO_MORE_FILES = 18;
        const int FIND_FIRST_EX_LARGE_FETCH = 2;

        enum FINDEX_INFO_LEVELS { FindExInfoStandard = 0, FindExInfoBasic = 1 }
        enum FINDEX_SEARCH_OPS { FindExSearchNameMatch = 0 }

        [StructLayout(LayoutKind.Sequential)]
        struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public FILETIME ftCreationTime;
            public FILETIME ftLastAccessTime;
            public FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            SafeFindHandle() : base(true) { }
            protected override bool ReleaseHandle() => FindClose(handle);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFindHandle FindFirstFileExW(string lpFileName, FINDEX_INFO_LEVELS fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool FindClose(IntPtr hFindFile);
    }
}
