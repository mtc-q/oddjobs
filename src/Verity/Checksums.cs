using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using OddJobs.IO;

namespace OddJobs.Verity
{
    public sealed class ListedFile
    {
        public string Name;          // as written in the checksum file
        public string Path;          // resolved, or null if it points outside the folder
        public string Digest;        // lowercase hex
        public string Algorithm;
    }

    /// <summary>Reads checksum lists in the two common formats (GNU coreutils and BSD).</summary>
    public static class ChecksumList
    {
        static readonly Regex Gnu = new Regex(@"^\\?(?<hash>[0-9a-fA-F]{32,128})\s+\*?(?<name>.+)$", RegexOptions.Compiled);
        static readonly Regex Bsd = new Regex(@"^(?<alg>[A-Za-z0-9\-]+)\s*\((?<name>.+)\)\s*=\s*(?<hash>[0-9a-fA-F]{32,128})$", RegexOptions.Compiled);

        static readonly string[] ListExtensions = { ".sha256", ".sha256sum", ".sha1", ".sha1sum", ".md5", ".md5sum", ".sha512", ".sha512sum", ".sha384", ".sums", ".digests" };

        /// <summary>True for files like SHA256SUMS, checksums.txt or app.zip.sha256.</summary>
        public static bool LooksLikeList(string path)
        {
            string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
            if (ListExtensions.Any(e => name.EndsWith(e))) return true;
            if (Regex.IsMatch(name, @"^(sha\d*|md5)sums?(\.txt)?$")) return true;
            if (name.Contains("checksum") && (name.EndsWith(".txt") || !name.Contains("."))) return true;
            return false;
        }

        public static List<ListedFile> Parse(string listPath)
        {
            var result = new List<ListedFile>();
            var info = new FileInfo(listPath);
            if (info.Length > 8 * 1024 * 1024) return result;   // no real checksum list is this big
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(listPath));

            foreach (var raw in File.ReadAllLines(listPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                string hash, name, alg;
                var m = Bsd.Match(line);
                if (m.Success)
                {
                    hash = m.Groups["hash"].Value;
                    name = m.Groups["name"].Value;
                    alg = Hashing.GuessFromHexLength(hash.Length);
                }
                else
                {
                    m = Gnu.Match(line);
                    if (!m.Success) continue;
                    hash = m.Groups["hash"].Value;
                    name = m.Groups["name"].Value.Trim();
                    alg = Hashing.GuessFromHexLength(hash.Length);
                }
                if (alg == null) continue;

                string resolved = null;
                try
                {
                    string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, name.Replace('/', '\\')));
                    if (PathUtil.IsInside(candidate, folder)) resolved = candidate;
                }
                catch (Exception) { }

                result.Add(new ListedFile { Name = name, Path = resolved, Digest = hash.ToLowerInvariant(), Algorithm = alg });
            }
            return result;
        }

        /// <summary>Writes a GNU-style SHA256SUMS list that works with sha256sum -c and with Verity.</summary>
        public static string Write(string folder, IEnumerable<(string Path, string Sha256)> files)
        {
            var sb = new StringBuilder();
            foreach (var (path, digest) in files)
                sb.Append(digest).Append("  ").Append(PathUtil.Relative(folder, path).Replace('\\', '/')).Append('\n');
            return sb.ToString();
        }
    }

    public static class FileHasher
    {
        /// <summary>Reads the file once and feeds every requested algorithm from the same buffer.</summary>
        public static Dictionary<string, string> Hash(string path, IReadOnlyCollection<string> algorithms, Action<long> progressed, CancellationToken ct)
        {
            var hashers = algorithms.Distinct().ToDictionary(a => a, Hashing.Create);
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var h in hashers.Values) h.TransformBlock(buffer, 0, read, null, 0);
                        progressed?.Invoke(read);
                    }
                    foreach (var h in hashers.Values) h.TransformFinalBlock(buffer, 0, 0);
                }
                return hashers.ToDictionary(p => p.Key, p => Hashing.Hex(p.Value.Hash));
            }
            finally
            {
                foreach (var h in hashers.Values) h.Dispose();
            }
        }
    }
}
