using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using OddJobs.IO;

namespace OddJobs.Tidy
{
    public enum Grouping { ByType, ByTypeAndMonth, ByMonth }

    public sealed class TidyOptions
    {
        public Grouping Grouping;
        public int SkipRecentDays = 1;
        public HashSet<FileKind> Kinds = new HashSet<FileKind>((FileKind[])Enum.GetValues(typeof(FileKind)));
    }

    public sealed class PlannedMove
    {
        public string From;
        public string To;
        public string Group;       // destination folder, relative to the tidied folder
        public FileKind Kind;
        public long Size;
        public DateTime Modified;
    }

    public static class TidyPlanner
    {
        public static string FolderFor(FileKind kind)
        {
            switch (kind)
            {
                case FileKind.Photo: return "Images";
                case FileKind.Video: return "Videos";
                case FileKind.Audio: return "Music";
                case FileKind.Document: return "Documents";
                case FileKind.Archive: return "Archives";
                case FileKind.Installer: return "Installers";
                case FileKind.Code: return "Code";
                case FileKind.Font: return "Fonts";
                case FileKind.DiskImage: return "Disk images";
                default: return "Other";
            }
        }

        /// <summary>Works out where every loose file in <paramref name="folder"/> should go. Touches nothing.</summary>
        public static List<PlannedMove> Plan(string folder, TidyOptions options, DateTime now, out int skippedRecent, out int skippedOther)
        {
            skippedRecent = 0;
            skippedOther = 0;
            var entries = new List<DirEntry>();
            if (!FastDir.TryList(folder, entries, out _)) throw new IOException("Can't read " + folder);

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var moves = new List<PlannedMove>();
            foreach (var e in entries.OrderBy(x => x.Name, NaturalComparer.Instance))
            {
                if (e.IsDirectory) continue;
                if (e.IsHidden || e.IsSystem || e.IsCloudOnly ||
                    FileKinds.IsPartialDownload(e.Name) ||
                    string.Equals(e.Name, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    skippedOther++;
                    continue;
                }
                if (options.SkipRecentDays > 0 && (now - e.LastWriteLocal).TotalDays < options.SkipRecentDays)
                {
                    skippedRecent++;
                    continue;
                }
                var kind = FileKinds.Of(e.Name);
                if (kind == FileKind.System) kind = FileKind.Other;
                if (!options.Kinds.Contains(kind))
                {
                    skippedOther++;
                    continue;
                }

                string month = e.LastWriteLocal.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                string group = options.Grouping == Grouping.ByType ? FolderFor(kind)
                    : options.Grouping == Grouping.ByTypeAndMonth ? Path.Combine(FolderFor(kind), month)
                    : month;
                string destination = Path.Combine(folder, group);
                string target = PathUtil.UniquePath(destination, e.Name, p => taken.Contains(p) || FileOps.Exists(p));
                taken.Add(target);
                moves.Add(new PlannedMove
                {
                    From = Path.Combine(folder, e.Name),
                    To = target,
                    Group = group,
                    Kind = kind,
                    Size = e.Size,
                    Modified = e.LastWriteLocal,
                });
            }
            return moves;
        }
    }

    /// <summary>
    /// Moves files and keeps a journal of every move in %LOCALAPPDATA%\OddJobs\Tidy\history, so any tidy
    /// can be undone later, even after a crash halfway through.
    /// </summary>
    public sealed class TidyRunner
    {
        public sealed class Journal
        {
            public string Path;
            public DateTime When;
            public string Folder;
            public List<(string From, string To)> Moves = new List<(string, string)>();
            public List<string> CreatedFolders = new List<string>();
        }

        readonly string _historyFolder;

        public TidyRunner(string dataFolder)
        {
            _historyFolder = Path.Combine(dataFolder, "history");
        }

        public (int Moved, List<string> Errors) Run(string folder, IList<PlannedMove> moves)
        {
            Directory.CreateDirectory(_historyFolder);
            string journalPath = Path.Combine(_historyFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".tsv");

            var created = new List<string>();
            foreach (var dir in moves.Select(m => Path.GetDirectoryName(m.To)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length))
            {
                // Remember every folder level we create, so undo can remove the empty ones again.
                var chain = new Stack<string>();
                for (var d = dir; d != null && !Directory.Exists(d) && PathUtil.IsInside(d, folder); d = Path.GetDirectoryName(d)) chain.Push(d);
                foreach (var d in chain)
                {
                    Directory.CreateDirectory(d);
                    created.Add(d);
                }
            }

            // Write the full plan first: if anything is interrupted, undo can still find every file.
            WriteJournal(journalPath, folder, moves.Select(m => (m.From, m.To)), created);

            var done = new List<(string, string)>();
            var errors = new List<string>();
            foreach (var m in moves)
            {
                try
                {
                    FileOps.Move(m.From, m.To);
                    done.Add((m.From, m.To));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    errors.Add(Path.GetFileName(m.From) + ": " + ex.Message);
                }
            }

            if (done.Count == 0) File.Delete(journalPath);
            else WriteJournal(journalPath, folder, done, created);
            foreach (var d in created.OrderByDescending(d => d.Length)) TryRemoveEmpty(d);
            return (done.Count, errors);
        }

        static void WriteJournal(string path, string folder, IEnumerable<(string From, string To)> moves, List<string> created)
        {
            var sb = new StringBuilder();
            sb.Append("#folder\t").Append(folder).Append('\n');
            sb.Append("#when\t").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var d in created) sb.Append("#created\t").Append(d).Append('\n');
            foreach (var (from, to) in moves) sb.Append(from).Append('\t').Append(to).Append('\n');
            AtomicFile.WriteAllText(path, sb.ToString());
        }

        public List<Journal> History()
        {
            var list = new List<Journal>();
            if (!Directory.Exists(_historyFolder)) return list;
            foreach (var file in Directory.GetFiles(_historyFolder, "*.tsv").OrderByDescending(f => f))
            {
                try
                {
                    var j = new Journal { Path = file };
                    foreach (var line in File.ReadAllLines(file, Encoding.UTF8))
                    {
                        var parts = line.Split('\t');
                        if (parts.Length != 2) continue;
                        if (parts[0] == "#folder") j.Folder = parts[1];
                        else if (parts[0] == "#when") DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out j.When);
                        else if (parts[0] == "#created") j.CreatedFolders.Add(parts[1]);
                        else j.Moves.Add((parts[0], parts[1]));
                    }
                    if (j.Moves.Count > 0) list.Add(j);
                }
                catch (IOException) { }
            }
            return list;
        }

        /// <summary>Moves every file back where it came from. Files moved or deleted since are skipped.</summary>
        public (int Restored, int Skipped) Undo(Journal journal)
        {
            int restored = 0, skipped = 0;
            foreach (var (from, to) in Enumerable.Reverse(journal.Moves))
            {
                if (FileOps.Exists(to) && !FileOps.Exists(from))
                {
                    try
                    {
                        FileOps.Move(to, from);
                        restored++;
                        continue;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                skipped++;
            }
            foreach (var d in journal.CreatedFolders.OrderByDescending(d => d.Length)) TryRemoveEmpty(d);
            if (skipped == 0) File.Delete(journal.Path);
            else
            {
                // Keep only what could not be undone, in case it can be later.
                var left = journal.Moves.Where(m => FileOps.Exists(m.To)).ToList();
                if (left.Count == 0) File.Delete(journal.Path);
                else WriteJournal(journal.Path, journal.Folder, left, journal.CreatedFolders);
            }
            return (restored, skipped);
        }

        static void TryRemoveEmpty(string dir)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
