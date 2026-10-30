using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OddJobs.IO;

namespace OddJobs.Footprint
{
    public sealed class Node
    {
        public string Name;
        public Node Parent;
        public long Size;
        public long FileCount;
        public bool IsDir;
        public bool IsAggregate;
        public bool Unreadable;
        public FileKind Kind;
        public DateTime Modified;
        public List<Node> Children = Empty;

        static readonly List<Node> Empty = new List<Node>(0);

        public string FullPath => Parent == null ? Name : Path.Combine(Parent.FullPath, Name);
        public bool CanAct => !IsAggregate && Parent != null;

        public IEnumerable<Node> Ancestors()
        {
            for (var n = this; n != null; n = n.Parent) yield return n;
        }

        /// <summary>Removes this node from the tree and subtracts its size from every folder above it.</summary>
        public void Detach()
        {
            if (Parent == null) return;
            Parent.Children = Parent.Children.Where(c => c != this).ToList();
            long files = IsDir ? FileCount : 1;
            for (var p = Parent; p != null; p = p.Parent)
            {
                p.Size -= Size;
                p.FileCount -= files;
            }
        }
    }

    public sealed class ScanStats
    {
        public long Files;
        public long Folders;
        public long Bytes;
        public long CloudOnlyBytes;
        public int UnreadableFolders;
        public volatile string Current;
    }

    /// <summary>
    /// Builds a size tree for a folder or drive. Reads directory listings only (never file contents),
    /// scans the top levels in parallel, and keeps just the largest files per folder in memory.
    /// </summary>
    public static class DiskScanner
    {
        const int FilesKeptPerFolder = 40;

        public static Node Scan(string root, ScanStats stats, CancellationToken ct)
        {
            root = PathUtil.Normalize(root);
            if (root.Length == 2 && root[1] == ':') root += "\\";
            var node = new Node { Name = root, IsDir = true, Modified = DateTime.Now };
            ScanFolder(node, root, 0, stats, ct);
            return node;
        }

        static void ScanFolder(Node node, string path, int depth, ScanStats stats, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var entries = new List<DirEntry>();
            if (!FastDir.TryList(path, entries, out _))
            {
                node.Unreadable = true;
                Interlocked.Increment(ref stats.UnreadableFolders);
                return;
            }
            stats.Current = path;
            Interlocked.Increment(ref stats.Folders);

            var folders = new List<Node>();
            var files = new List<Node>();
            foreach (var e in entries)
            {
                if (e.IsDirectory)
                {
                    // Junctions and symbolic links point at folders counted elsewhere.
                    if (e.IsReparsePoint) continue;
                    folders.Add(new Node { Name = e.Name, Parent = node, IsDir = true, Modified = e.LastWriteLocal });
                    continue;
                }
                if (e.IsCloudOnly)
                {
                    Interlocked.Add(ref stats.CloudOnlyBytes, e.Size);
                    continue;
                }
                files.Add(new Node { Name = e.Name, Parent = node, Size = e.Size, FileCount = 1, Kind = FileKinds.Of(e.Name), Modified = e.LastWriteLocal });
            }

            Interlocked.Add(ref stats.Files, files.Count);
            Interlocked.Add(ref stats.Bytes, files.Sum(f => f.Size));

            if (depth < 3 && folders.Count > 1)
                Parallel.ForEach(folders, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                    f => ScanFolder(f, Path.Combine(path, f.Name), depth + 1, stats, ct));
            else
                foreach (var f in folders) ScanFolder(f, Path.Combine(path, f.Name), depth + 1, stats, ct);

            files.Sort((a, b) => b.Size.CompareTo(a.Size));
            var children = new List<Node>(folders.Count + Math.Min(files.Count, FilesKeptPerFolder + 1));
            children.AddRange(folders);
            children.AddRange(files.Take(FilesKeptPerFolder));
            if (files.Count > FilesKeptPerFolder)
            {
                var rest = files.Skip(FilesKeptPerFolder).ToList();
                children.Add(new Node
                {
                    Name = Format.Number(rest.Count) + " smaller files",
                    Parent = node,
                    Size = rest.Sum(f => f.Size),
                    FileCount = rest.Count,
                    IsAggregate = true,
                    Kind = FileKind.Other,
                });
            }

            node.Size = children.Sum(c => c.Size);
            node.FileCount = folders.Sum(f => f.FileCount) + files.Count;
            children.Sort((a, b) => b.Size.CompareTo(a.Size));
            node.Children = children;
        }

        /// <summary>Total bytes by kind of file under a node (only the files kept in memory are classified).</summary>
        public static Dictionary<FileKind, long> SizeByKind(Node node)
        {
            var totals = new Dictionary<FileKind, long>();
            var stack = new Stack<Node>();
            stack.Push(node);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n.IsDir)
                {
                    foreach (var c in n.Children) stack.Push(c);
                    continue;
                }
                totals[n.Kind] = (totals.TryGetValue(n.Kind, out var t) ? t : 0) + n.Size;
            }
            return totals;
        }
    }

    public struct Tile
    {
        public Node Node;
        public System.Windows.Rect Rect;
    }

    /// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk): tiles stay close to square.</summary>
    public static class Treemap
    {
        public static List<Tile> Layout(IList<Node> nodes, System.Windows.Rect bounds)
        {
            var output = new List<Tile>();
            var items = nodes.Where(n => n.Size > 0).OrderByDescending(n => n.Size).ToList();
            double total = items.Sum(n => (double)n.Size);
            if (total <= 0 || bounds.Width < 1 || bounds.Height < 1) return output;

            double scale = bounds.Width * bounds.Height / total;
            var rect = bounds;
            var row = new List<Node>();
            double rowArea = 0, rowMin = double.MaxValue, rowMax = 0;
            int i = 0;
            while (i < items.Count)
            {
                double area = items[i].Size * scale;
                double side = Math.Min(rect.Width, rect.Height);
                if (row.Count == 0 ||
                    Worst(rowArea + area, Math.Min(rowMin, area), Math.Max(rowMax, area), side) <= Worst(rowArea, rowMin, rowMax, side))
                {
                    row.Add(items[i]);
                    rowArea += area;
                    rowMin = Math.Min(rowMin, area);
                    rowMax = Math.Max(rowMax, area);
                    i++;
                }
                else
                {
                    rect = PlaceRow(row, rowArea, rect, scale, output);
                    row.Clear();
                    rowArea = 0;
                    rowMin = double.MaxValue;
                    rowMax = 0;
                }
            }
            if (row.Count > 0) PlaceRow(row, rowArea, rect, scale, output);
            return output;
        }

        static double Worst(double sum, double min, double max, double side)
        {
            if (sum <= 0 || min <= 0) return double.MaxValue;
            double s2 = sum * sum, w2 = side * side;
            return Math.Max(w2 * max / s2, s2 / (w2 * min));
        }

        static System.Windows.Rect PlaceRow(List<Node> row, double rowArea, System.Windows.Rect rect, double scale, List<Tile> output)
        {
            if (rect.Width >= rect.Height)
            {
                double width = rect.Height > 0 ? rowArea / rect.Height : 0;
                double y = rect.Y;
                foreach (var n in row)
                {
                    double h = width > 0 ? n.Size * scale / width : 0;
                    output.Add(new Tile { Node = n, Rect = new System.Windows.Rect(rect.X, y, width, h) });
                    y += h;
                }
                return new System.Windows.Rect(rect.X + width, rect.Y, Math.Max(0, rect.Width - width), rect.Height);
            }
            else
            {
                double height = rect.Width > 0 ? rowArea / rect.Width : 0;
                double x = rect.X;
                foreach (var n in row)
                {
                    double w = height > 0 ? n.Size * scale / height : 0;
                    output.Add(new Tile { Node = n, Rect = new System.Windows.Rect(x, rect.Y, w, height) });
                    x += w;
                }
                return new System.Windows.Rect(rect.X, rect.Y + height, rect.Width, Math.Max(0, rect.Height - height));
            }
        }
    }
}
