using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace OddJobs.IO
{
    public static class PathUtil
    {
        static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

        static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>Adds the \\?\ prefix so Win32 calls accept paths longer than 260 characters.</summary>
        public static string ToLongPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        /// <summary>Returns null if the name is a valid Windows file name, otherwise a short reason.</summary>
        public static string ValidateFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Name is empty";
            if (name.IndexOfAny(InvalidNameChars) >= 0)
            {
                char bad = name.First(c => InvalidNameChars.Contains(c));
                return char.IsControl(bad) ? "Contains a control character" : "Contains \u201C" + bad + "\u201D";
            }
            if (name.EndsWith(".") || name.EndsWith(" ")) return "Ends with a dot or space";
            if (name == "." || name == "..") return "Reserved name";
            string stem = name.Split('.')[0].TrimEnd();
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return "\u201C" + stem + "\u201D is reserved by Windows";
            if (name.Length > 255) return "Longer than 255 characters";
            return null;
        }

        /// <summary>Replaces characters Windows does not allow in file names.</summary>
        public static string SanitizeFileName(string name, char replacement = '_')
        {
            var chars = name.Select(c => InvalidNameChars.Contains(c) ? replacement : c).ToArray();
            string result = new string(chars).TrimEnd('.', ' ');
            if (result.Length == 0) result = "untitled";
            string stem = result.Split('.')[0];
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) result = "_" + result;
            return result;
        }

        /// <summary>"photo.jpg" becomes "photo (2).jpg", "photo (3).jpg", ... until the name is free.</summary>
        public static string UniquePath(string directory, string fileName, Func<string, bool> isTaken = null)
        {
            isTaken = isTaken ?? (p => File.Exists(p) || Directory.Exists(p));
            string candidate = Path.Combine(directory, fileName);
            if (!isTaken(candidate)) return candidate;
            string stem = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 2; i < 100000; i++)
            {
                candidate = Path.Combine(directory, stem + " (" + i + ")" + ext);
                if (!isTaken(candidate)) return candidate;
            }
            throw new IOException("Could not find a free name for " + fileName);
        }

        /// <summary>True if <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
        public static bool IsInside(string path, string folder)
        {
            string p = Normalize(path), f = Normalize(folder);
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase);
        }

        public static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\');

        /// <summary>Path of <paramref name="path"/> relative to <paramref name="root"/>.</summary>
        public static string Relative(string root, string path)
        {
            string r = Normalize(root) + "\\";
            return path.StartsWith(r, StringComparison.OrdinalIgnoreCase) ? path.Substring(r.Length) : path;
        }

        public static bool SameVolume(string a, string b) =>
            string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

        static readonly string[] RiskyExtensions =
        {
            ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
            ".msi", ".msp", ".scr", ".pif", ".lnk", ".url", ".jar", ".reg", ".cpl", ".msc", ".appref-ms", ".application",
        };

        /// <summary>Files that run code when opened. The apps reveal these in Explorer instead of opening them.</summary>
        public static bool IsRunnable(string path) =>
            RiskyExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        /// <summary>Sorts like File Explorer: "file2" before "file10".</summary>
        public static int NaturalCompare(string a, string b) => StrCmpLogicalW(a ?? "", b ?? "");
    }

    public sealed class NaturalComparer : System.Collections.Generic.IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        public int Compare(string x, string y) => PathUtil.NaturalCompare(x, y);
    }
}
