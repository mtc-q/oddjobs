using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using OddJobs.IO;

namespace OddJobs.Twins
{
    public sealed class ScanOptions
    {
        public List<string> Folders = new List<string>();
        public long MinSize = 1;
        public FileKind? Kind;
        public bool IncludeHidden;
    }

    public sealed class ScanProgress
    {
        public string Phase;
        public long FilesSeen;
        public long BytesToHash;
        public long BytesHashed;
        public string Current;
    }

    public sealed class DupFile
    {
        public string Path;
        public long Size;
        public DateTime Modified;
        public string Hash;
    }

    public sealed class DupGroup
    {
        public long Size;
        public string Hash;
        public List<DupFile> Files = new List<DupFile>();
        public long Wasted => Size * (Files.Count - 1);
    }

    public sealed class ScanResult
    {
        public List<DupGroup> Groups = new List<DupGroup>();
        public long FilesScanned;
        public int Unreadable;
        public int CloudOnlySkipped;
        public TimeSpan Elapsed;
    }

    /// <summary>
    /// Finds files with identical contents. Cheap checks first: only files of the same size are
    /// compared, then a fingerprint of their first and last 16 KB, and only the survivors are read in
    /// full and compared by SHA-256. Files are opened read-only and never modified.
    /// </summary>
    public static class DuplicateScanner
    {
        const int SampleSize = 16 * 1024;

        public static ScanResult Scan(ScanOptions options, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            var started = DateTime.UtcNow;
            var result = new ScanResult();
            var bySize = new Dictionary<long, List<DupFile>>();
            var report = new ScanProgress { Phase = "Looking at files" };
            var lastReport = DateTime.UtcNow;

            foreach (var root in DistinctRoots(options.Folders))
            {
                foreach (var (path, entry) in FastDir.WalkFiles(root, options.IncludeHidden, () => ct.IsCancellationRequested, _ => result.Unreadable++))
                {
                    result.FilesScanned++;
                    if (entry.IsCloudOnly) { result.CloudOnlySkipped++; continue; }   // reading would download it
                    if (entry.IsReparsePoint) continue;
                    if (entry.Size < Math.Max(1, options.MinSize)) continue;
                    if (options.Kind.HasValue && FileKinds.Of(path) != options.Kind.Value) continue;

                    if (!bySize.TryGetValue(entry.Size, out var list)) bySize[entry.Size] = list = new List<DupFile>(2);
                    list.Add(new DupFile { Path = path, Size = entry.Size, Modified = entry.LastWriteLocal });

                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                    {
                        report.FilesSeen = result.FilesScanned;
                        report.Current = path;
                        progress?.Report(report);
                        lastReport = DateTime.UtcNow;
                    }
                }
            }
            ct.ThrowIfCancellationRequested();

            var candidates = bySize.Values.Where(l => l.Count > 1).SelectMany(l => l).ToList();
            bySize.Clear();

            // Stage 2: sample fingerprint (also weeds out hard links to the same physical file).
            var sampled = new ConcurrentBag<(DupFile File, string Sample, string Identity)>();
            int unreadable = 0;
            report.Phase = "Comparing";
            report.BytesToHash = candidates.Sum(f => Math.Min(f.Size, 2L * SampleSize));
            report.BytesHashed = 0;
            long hashed = 0;
            Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Parallelism }, file =>
            {
                try
                {
                    var (sample, identity) = SampleHash(file.Path, file.Size);
                    if (file.Size <= 2L * SampleSize) file.Hash = sample;   // the sample was the whole file
                    sampled.Add((file, sample, identity));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref unreadable);
                }
                long done = Interlocked.Add(ref hashed, Math.Min(file.Size, 2L * SampleSize));
                Throttled(progress, report, done);
            });

            var needFull = new List<DupFile>();
            var groups = new List<List<DupFile>>();
            foreach (var bucket in sampled.GroupBy(s => (s.File.Size, s.Sample)))
            {
                // Two paths to one physical file (hard links) are not duplicates: deleting one frees nothing.
                var distinct = bucket.GroupBy(s => s.Identity).Select(g => g.First().File).ToList();
                if (distinct.Count < 2) continue;
                if (distinct[0].Hash != null) groups.Add(distinct);
                else needFull.AddRange(distinct);
            }

            // Stage 3: full SHA-256 of the remaining candidates.
            report.Phase = "Reading files in full";
            report.BytesToHash = needFull.Sum(f => f.Size);
            report.BytesHashed = 0;
            hashed = 0;
            Parallel.ForEach(needFull, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Parallelism }, file =>
            {
                try
                {
                    file.Hash = FullHash(file.Path, ct, n => Throttled(progress, report, Interlocked.Add(ref hashed, n)));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref unreadable);
                }
            });
            ct.ThrowIfCancellationRequested();

            groups.AddRange(needFull.Where(f => f.Hash != null).GroupBy(f => (f.Size, f.Hash)).Where(g => g.Count() > 1).Select(g => g.ToList()));

            result.Groups = groups
                .Select(g => new DupGroup
                {
                    Size = g[0].Size,
                    Hash = g[0].Hash,
                    Files = g.OrderBy(f => f.Path, NaturalComparer.Instance).ToList(),
                })
                .OrderByDescending(g => g.Wasted)
                .ToList();
            result.Unreadable += unreadable;
            result.Elapsed = DateTime.UtcNow - started;
            return result;
        }

        static int Parallelism => Math.Max(2, Math.Min(4, Environment.ProcessorCount));

        static DateTime _lastProgress;
        static void Throttled(IProgress<ScanProgress> progress, ScanProgress report, long done)
        {
            if (progress == null) return;
            var now = DateTime.UtcNow;
            if ((now - _lastProgress).TotalMilliseconds < 80) return;
            _lastProgress = now;
            progress.Report(new ScanProgress { Phase = report.Phase, BytesToHash = report.BytesToHash, BytesHashed = done, FilesSeen = report.FilesSeen });
        }

        /// <summary>Drops folders that are inside another selected folder, so nothing is counted twice.</summary>
        public static List<string> DistinctRoots(IEnumerable<string> folders)
        {
            var normalized = folders.Select(PathUtil.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f.Length).ToList();
            var roots = new List<string>();
            foreach (var f in normalized)
                if (!roots.Any(r => PathUtil.IsInside(f, r))) roots.Add(f);
            return roots;
        }

        static FileStream OpenRead(string path, int buffer) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer, FileOptions.SequentialScan);

        static (string Sample, string Identity) SampleHash(string path, long size)
        {
            using (var fs = OpenRead(path, 4096))
            using (var sha = Hashing.Sha256())
            {
                string identity = FileIdentity(fs.SafeFileHandle) ?? path.ToUpperInvariant();
                var buffer = new byte[SampleSize];
                if (size <= 2L * SampleSize)
                {
                    var all = new byte[size];
                    ReadExactly(fs, all, (int)size);
                    return (Hex(sha.ComputeHash(all)), identity);
                }
                ReadExactly(fs, buffer, SampleSize);
                sha.TransformBlock(buffer, 0, SampleSize, null, 0);
                fs.Seek(-SampleSize, SeekOrigin.End);
                ReadExactly(fs, buffer, SampleSize);
                sha.TransformFinalBlock(buffer, 0, SampleSize);
                return (Hex(sha.Hash), identity);
            }
        }

        static string FullHash(string path, CancellationToken ct, Action<long> progressed)
        {
            using (var fs = OpenRead(path, 1 << 20))
            using (var sha = Hashing.Sha256())
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    progressed(read);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Hex(sha.Hash);
            }
        }

        static void ReadExactly(Stream s, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int n = s.Read(buffer, offset, count - offset);
                if (n <= 0) throw new IOException("File changed while being read");
                offset += n;
            }
        }

        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

        /// <summary>Volume serial + file index: the same value for every hard link to one file.</summary>
        static string FileIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info)) return null;
            return info.dwVolumeSerialNumber.ToString("x8") + ":" + info.nFileIndexHigh.ToString("x8") + info.nFileIndexLow.ToString("x8");
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);
    }
}
