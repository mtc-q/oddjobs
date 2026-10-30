using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using OddJobs.IO;

namespace OddJobs.Parity
{
    public enum DiffKind { Same, OnlyLeft, OnlyRight, LeftNewer, RightNewer, Different }

    public sealed class DiffItem : INotifyPropertyChanged
    {
        public string Relative { get; set; }
        public DirEntry? Left { get; set; }
        public DirEntry? Right { get; set; }
        public DiffKind Kind { get; set; }

        bool _selected;
        public bool Selected { get => _selected; set { if (_selected != value) { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } } }

        bool _actionable;
        public bool Actionable { get => _actionable; set { _actionable = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Actionable))); } }

        string _plan;
        public string Plan { get => _plan; set { _plan = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Plan))); } }

        public string Name => Path.GetFileName(Relative);
        public string Folder => Path.GetDirectoryName(Relative);
        public string LeftText => Left.HasValue ? Format.Size(Left.Value.Size) + " · " + Left.Value.LastWriteLocal.ToString("d MMM yyyy HH:mm") : "—";
        public string RightText => Right.HasValue ? Format.Size(Right.Value.Size) + " · " + Right.Value.LastWriteLocal.ToString("d MMM yyyy HH:mm") : "—";

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case DiffKind.OnlyLeft: return "Only on the left";
                    case DiffKind.OnlyRight: return "Only on the right";
                    case DiffKind.LeftNewer: return "Newer on the left";
                    case DiffKind.RightNewer: return "Newer on the right";
                    case DiffKind.Different: return "Different, same date";
                    default: return "Identical";
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class CompareOptions
    {
        public bool CheckContents;
        public bool IncludeHidden;
    }

    public static class FolderComparer
    {
        static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System Volume Information", "$RECYCLE.BIN", "Thumbs.db", "desktop.ini", ".DS_Store", "ehthumbs.db",
        };

        public static List<DiffItem> Compare(string left, string right, CompareOptions options, IProgress<string> progress, CancellationToken ct)
        {
            if (PathUtil.IsInside(left, right) || PathUtil.IsInside(right, left))
                throw new InvalidOperationException("One folder is inside the other. Pick two separate folders.");

            progress?.Report("Reading the left folder…");
            var l = List(left, options, ct);
            progress?.Report("Reading the right folder…");
            var r = List(right, options, ct);

            var items = new List<DiffItem>();
            foreach (var key in l.Keys.Union(r.Keys, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                bool inL = l.TryGetValue(key, out var a), inR = r.TryGetValue(key, out var b);
                var item = new DiffItem { Relative = key, Left = inL ? a : (DirEntry?)null, Right = inR ? b : (DirEntry?)null };
                if (!inR) item.Kind = DiffKind.OnlyLeft;
                else if (!inL) item.Kind = DiffKind.OnlyRight;
                else item.Kind = Classify(a, b);
                items.Add(item);
            }

            if (options.CheckContents)
            {
                var sameSize = items.Where(i => i.Left.HasValue && i.Right.HasValue && i.Left.Value.Size == i.Right.Value.Size).ToList();
                int n = 0;
                foreach (var item in sameSize)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report("Comparing contents " + (++n) + " of " + sameSize.Count + "…");
                    bool equal = SameContents(Path.Combine(left, item.Relative), Path.Combine(right, item.Relative), ct);
                    if (equal) item.Kind = DiffKind.Same;
                    else if (item.Kind == DiffKind.Same) item.Kind = DiffKind.Different;
                }
            }

            return items.OrderBy(i => i.Kind == DiffKind.Same ? 1 : 0).ThenBy(i => i.Relative, NaturalComparer.Instance).ToList();
        }

        /// <summary>
        /// Size and modified time decide. Two seconds of slack covers FAT/exFAT drives, which store times
        /// in 2-second steps, and an exact one-hour gap is ignored because FAT drives shift by an hour at DST.
        /// </summary>
        public static DiffKind Classify(DirEntry a, DirEntry b)
        {
            double seconds = (a.LastWriteUtc - b.LastWriteUtc).TotalSeconds;
            bool sameTime = Math.Abs(seconds) <= 2 || Math.Abs(Math.Abs(seconds) - 3600) <= 2;
            if (a.Size == b.Size && sameTime) return DiffKind.Same;
            if (sameTime) return DiffKind.Different;
            return seconds > 0 ? DiffKind.LeftNewer : DiffKind.RightNewer;
        }

        static Dictionary<string, DirEntry> List(string root, CompareOptions options, CancellationToken ct)
        {
            var map = new Dictionary<string, DirEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, entry) in FastDir.WalkFiles(root, options.IncludeHidden, () => ct.IsCancellationRequested, null,
                         (dir, e) => !Ignored.Contains(e.Name)))
            {
                if (Ignored.Contains(entry.Name) || entry.IsCloudOnly) continue;
                map[PathUtil.Relative(root, path)] = entry;
            }
            ct.ThrowIfCancellationRequested();
            return map;
        }

        static bool SameContents(string a, string b, CancellationToken ct)
        {
            try
            {
                using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
                using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
                {
                    if (fa.Length != fb.Length) return false;
                    var ba = new byte[1 << 20];
                    var bb = new byte[1 << 20];
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();
                        int na = Fill(fa, ba), nb = Fill(fb, bb);
                        if (na != nb) return false;
                        if (na == 0) return true;
                        for (int i = 0; i < na; i++) if (ba[i] != bb[i]) return false;
                    }
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static int Fill(Stream s, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = s.Read(buffer, total, buffer.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }
    }

    public enum SyncMode { UpdateRight, BothWays, MirrorToRight }

    public sealed class SyncOp
    {
        public DiffItem Item;
        public string From;      // null for a removal
        public string To;
        public bool Overwrite;
        public bool RecycleExisting;
        public bool Remove;
        public long Bytes;
    }

    public static class Syncer
    {
        /// <summary>What a mode would do with one item, or null for nothing.</summary>
        public static SyncOp Plan(DiffItem item, SyncMode mode, string left, string right)
        {
            string l = Path.Combine(left, item.Relative), r = Path.Combine(right, item.Relative);
            SyncOp ToRight(bool recycle = false) => new SyncOp { Item = item, From = l, To = r, Overwrite = item.Right.HasValue, RecycleExisting = recycle, Bytes = item.Left.Value.Size };
            SyncOp ToLeft() => new SyncOp { Item = item, From = r, To = l, Overwrite = item.Left.HasValue, Bytes = item.Right.Value.Size };

            switch (item.Kind)
            {
                case DiffKind.OnlyLeft: return ToRight();
                case DiffKind.LeftNewer: return ToRight();
                case DiffKind.OnlyRight:
                    if (mode == SyncMode.BothWays) return ToLeft();
                    if (mode == SyncMode.MirrorToRight) return new SyncOp { Item = item, To = r, Remove = true };
                    return null;
                case DiffKind.RightNewer:
                    if (mode == SyncMode.BothWays) return ToLeft();
                    if (mode == SyncMode.MirrorToRight) return ToRight(recycle: true);
                    return null;
                case DiffKind.Different:
                    if (mode == SyncMode.MirrorToRight) return ToRight(recycle: true);
                    return null;   // can't tell which is newer: leave for a person to decide
                default:
                    return null;
            }
        }

        /// <summary>
        /// Copies via a temporary file and swaps it in, so a half-copied file never replaces a good one.
        /// Files that would be overwritten while newer, or removed, go to the Recycle Bin.
        /// </summary>
        public static (int Done, List<string> Errors, List<string> ToRecycle) Run(IList<SyncOp> ops, IProgress<(int, long)> progress, CancellationToken ct)
        {
            var errors = new List<string>();
            var recycle = new List<string>();
            int done = 0;
            long bytes = 0;
            foreach (var op in ops)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    if (op.Remove)
                    {
                        recycle.Add(op.To);
                        done++;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(op.To));
                    string temp = Path.Combine(Path.GetDirectoryName(op.To), "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".parity.tmp");
                    try
                    {
                        File.Copy(op.From, temp, false);   // keeps the source's modified time and attributes
                        if (File.Exists(op.To))
                        {
                            if (op.RecycleExisting)
                            {
                                string old = Path.Combine(Path.GetDirectoryName(op.To), Path.GetFileNameWithoutExtension(op.To) + " (replaced by Parity)" + Path.GetExtension(op.To));
                                old = PathUtil.UniquePath(Path.GetDirectoryName(old), Path.GetFileName(old));
                                File.Move(op.To, old);
                                recycle.Add(old);
                            }
                            else
                            {
                                File.SetAttributes(op.To, FileAttributes.Normal);
                                try
                                {
                                    File.Replace(temp, op.To, null, true);   // one atomic swap where the drive supports it
                                }
                                catch (IOException)
                                {
                                    File.Delete(op.To);
                                    File.Move(temp, op.To);
                                }
                            }
                        }
                        if (File.Exists(temp)) File.Move(temp, op.To);
                        File.SetLastWriteTimeUtc(op.To, File.GetLastWriteTimeUtc(op.From));
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                    done++;
                    bytes += op.Bytes;
                    progress?.Report((done, bytes));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    errors.Add(op.Item.Relative + ": " + ex.Message);
                }
            }
            return (done, errors, recycle);
        }
    }
}
