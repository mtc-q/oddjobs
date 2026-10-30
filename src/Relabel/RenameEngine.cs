using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OddJobs.IO;

namespace OddJobs.Relabel
{
    public enum CaseMode { Keep, Lower, Upper, Title, Sentence }
    public enum SpaceMode { Keep, Dash, Underscore, Remove }
    public enum ExtMode { Keep, Lower, Upper }
    public enum OrderMode { Name, Modified, Taken, Size, Added }
    public enum ItemState { Same, Ready, Problem, Done, Failed }

    public sealed class RenameRules
    {
        public string Template = "{name}";
        public string Find = "";
        public string Replace = "";
        public bool UseRegex;
        public bool MatchCase;
        public CaseMode Case;
        public SpaceMode Spaces;
        public ExtMode Extension;
        public bool TrimSpaces = true;
        public int CounterStart = 1;
        public int CounterStep = 1;
        public OrderMode Order;
        public bool Descending;

        public bool UsesTakenDate =>
            Order == OrderMode.Taken || (Template ?? "").IndexOf("{taken", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public sealed class RenameItem : INotifyPropertyChanged
    {
        public RenameItem(string path, DirEntry entry, int addedIndex)
        {
            AddedIndex = addedIndex;
            Size = entry.Size;
            Modified = entry.LastWriteLocal;
            Created = DateTime.FromFileTimeUtc(entry.CreationUtcTicks).ToLocalTime();
            SetPath(path);
            _newName = Name;
        }

        public string FullPath { get; private set; }
        public string Folder { get; private set; }
        public string FolderName => Path.GetFileName(Folder) is string f && f.Length > 0 ? f : Folder;
        public string Name { get; private set; }
        public string Stem { get; private set; }
        public string Extension { get; private set; }
        public long Size { get; }
        public DateTime Modified { get; }
        public DateTime Created { get; }
        public int AddedIndex { get; }

        /// <summary>Photo "date taken", once read. Null if the file has none.</summary>
        public DateTime? Taken { get; set; }
        public bool TakenLoaded { get; set; }

        string _newName;
        public string NewName
        {
            get => _newName;
            set { if (_newName != value) { _newName = value; Raise(nameof(NewName)); Raise(nameof(IsChanged)); } }
        }

        public bool IsChanged => !string.Equals(_newName, Name, StringComparison.Ordinal);

        ItemState _state;
        public ItemState State
        {
            get => _state;
            set { if (_state != value) { _state = value; Raise(nameof(State)); } }
        }

        string _note;
        public string Note
        {
            get => _note;
            set { if (_note != value) { _note = value; Raise(nameof(Note)); } }
        }

        internal void SetPath(string path)
        {
            FullPath = path;
            Folder = Path.GetDirectoryName(path);
            Name = Path.GetFileName(path);
            Extension = Path.GetExtension(Name);
            Stem = Extension.Length > 0 ? Name.Substring(0, Name.Length - Extension.Length) : Name;
            Raise(nameof(FullPath));
            Raise(nameof(Name));
            Raise(nameof(IsChanged));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public static class RenamePlanner
    {
        public static IEnumerable<RenameItem> Sort(IEnumerable<RenameItem> items, RenameRules rules)
        {
            IOrderedEnumerable<RenameItem> sorted;
            switch (rules.Order)
            {
                case OrderMode.Modified: sorted = items.OrderBy(i => i.Modified); break;
                case OrderMode.Taken: sorted = items.OrderBy(i => i.Taken ?? i.Modified); break;
                case OrderMode.Size: sorted = items.OrderBy(i => i.Size); break;
                case OrderMode.Added: sorted = items.OrderBy(i => i.AddedIndex); break;
                default: sorted = items.OrderBy(i => i.Name, NaturalComparer.Instance); break;
            }
            var list = sorted.ThenBy(i => i.Name, NaturalComparer.Instance).ToList();
            if (rules.Descending) list.Reverse();
            return list;
        }

        /// <summary>Computes every new name, then marks each item Same, Ready or Problem.</summary>
        /// <returns>An error about the rules themselves (for example a bad regex), or null.</returns>
        public static string Plan(IList<RenameItem> ordered, RenameRules rules, Func<string, bool> existsOnDisk = null)
        {
            existsOnDisk = existsOnDisk ?? FileOps.Exists;
            string ruleError = null;

            Regex regex = null;
            if (!string.IsNullOrEmpty(rules.Find) && rules.UseRegex)
            {
                try
                {
                    regex = new Regex(rules.Find, (rules.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    ruleError = "Find pattern is not a valid regular expression: " + ex.Message;
                }
            }

            long counter = rules.CounterStart;
            foreach (var item in ordered)
            {
                string stem = item.Stem;
                try
                {
                    if (regex != null) stem = regex.Replace(stem, rules.Replace ?? "");
                    else if (!string.IsNullOrEmpty(rules.Find) && ruleError == null)
                        stem = ReplaceLiteral(stem, rules.Find, rules.Replace ?? "", rules.MatchCase);
                }
                catch (RegexMatchTimeoutException)
                {
                    ruleError = "The find pattern took too long to run. Try a simpler pattern.";
                }

                string name = Expand(string.IsNullOrEmpty(rules.Template) ? "{name}" : rules.Template, item, stem, counter);
                counter += rules.CounterStep;
                name = ApplySpaces(ApplyCase(name, rules.Case), rules.Spaces, rules.TrimSpaces);

                string ext = rules.Extension == ExtMode.Lower ? item.Extension.ToLowerInvariant()
                    : rules.Extension == ExtMode.Upper ? item.Extension.ToUpperInvariant()
                    : item.Extension;
                item.NewName = name + ext;
            }

            Validate(ordered, existsOnDisk);
            return ruleError;
        }

        static void Validate(IList<RenameItem> items, Func<string, bool> existsOnDisk)
        {
            var leaving = new HashSet<string>(items.Where(i => i.IsChanged).Select(i => i.FullPath), StringComparer.OrdinalIgnoreCase);
            var targets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                string target = Path.Combine(item.Folder, item.NewName ?? "");
                targets[target] = targets.TryGetValue(target, out int n) ? n + 1 : 1;
            }

            foreach (var item in items)
            {
                if (!item.IsChanged)
                {
                    item.State = ItemState.Same;
                    item.Note = "No change";
                    continue;
                }

                string invalid = PathUtil.ValidateFileName(item.NewName);
                if (invalid != null)
                {
                    item.State = ItemState.Problem;
                    item.Note = invalid;
                    continue;
                }

                string target = Path.Combine(item.Folder, item.NewName);
                if (targets[target] > 1)
                {
                    item.State = ItemState.Problem;
                    item.Note = "Same new name as " + Format.Count(targets[target] - 1, "other file");
                    continue;
                }

                bool caseOnly = string.Equals(target, item.FullPath, StringComparison.OrdinalIgnoreCase);
                if (!caseOnly && existsOnDisk(target) && !leaving.Contains(target))
                {
                    item.State = ItemState.Problem;
                    item.Note = "A file with this name already exists";
                    continue;
                }

                if (target.Length > 32000)
                {
                    item.State = ItemState.Problem;
                    item.Note = "Path too long";
                    continue;
                }

                item.State = ItemState.Ready;
                item.Note = caseOnly ? "Capitalisation change" : "Ready";
            }
        }

        static string ReplaceLiteral(string input, string find, string replace, bool matchCase)
        {
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var sb = new StringBuilder();
            int start = 0;
            while (true)
            {
                int i = input.IndexOf(find, start, comparison);
                if (i < 0) break;
                sb.Append(input, start, i - start).Append(replace);
                start = i + find.Length;
            }
            sb.Append(input, start, input.Length - start);
            return sb.ToString();
        }

        /// <summary>Expands {name}, {n}, {n:3}, {date[:fmt]}, {created[:fmt]}, {taken[:fmt]} and {folder}.</summary>
        public static string Expand(string template, RenameItem item, string stem, long counter)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    int close = template.IndexOf('}', i + 1);
                    if (close > i)
                    {
                        string token = template.Substring(i + 1, close - i - 1);
                        string value = Token(token, item, stem, counter);
                        if (value != null)
                        {
                            sb.Append(value);
                            i = close + 1;
                            continue;
                        }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static string Token(string token, RenameItem item, string stem, long counter)
        {
            int colon = token.IndexOf(':');
            string key = (colon >= 0 ? token.Substring(0, colon) : token).Trim().ToLowerInvariant();
            string arg = colon >= 0 ? token.Substring(colon + 1) : null;

            switch (key)
            {
                case "name":
                    return stem;
                case "n":
                    int pad = 0;
                    if (arg != null && (!int.TryParse(arg, out pad) || pad < 0 || pad > 12)) return null;
                    return counter < 0 ? "-" + (-counter).ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0')
                        : counter.ToString(CultureInfo.InvariantCulture).PadLeft(pad, '0');
                case "date":
                    return FormatDate(item.Modified, arg);
                case "created":
                    return FormatDate(item.Created, arg);
                case "taken":
                    return FormatDate(item.Taken ?? item.Modified, arg);
                case "folder":
                    return item.FolderName;
                default:
                    return null;
            }
        }

        static string FormatDate(DateTime d, string format)
        {
            try { return d.ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format, CultureInfo.InvariantCulture); }
            catch (FormatException) { return null; }
        }

        public static string ApplyCase(string s, CaseMode mode)
        {
            switch (mode)
            {
                case CaseMode.Lower: return s.ToLower(CultureInfo.CurrentCulture);
                case CaseMode.Upper: return s.ToUpper(CultureInfo.CurrentCulture);
                case CaseMode.Title: return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture));
                case CaseMode.Sentence:
                    if (s.Length == 0) return s;
                    string lower = s.ToLower(CultureInfo.CurrentCulture);
                    int first = 0;
                    while (first < lower.Length && !char.IsLetter(lower[first])) first++;
                    if (first >= lower.Length) return lower;
                    return lower.Substring(0, first) + char.ToUpper(lower[first], CultureInfo.CurrentCulture) + lower.Substring(first + 1);
                default: return s;
            }
        }

        static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);

        public static string ApplySpaces(string s, SpaceMode mode, bool trim)
        {
            if (trim) s = Whitespace.Replace(s, " ").Trim();
            switch (mode)
            {
                case SpaceMode.Dash: return Whitespace.Replace(s, "-");
                case SpaceMode.Underscore: return Whitespace.Replace(s, "_");
                case SpaceMode.Remove: return Whitespace.Replace(s, "");
                default: return s;
            }
        }
    }

    /// <summary>
    /// Performs a batch of renames safely: every file first gets a unique temporary name, then its final
    /// name, so swaps (a→b, b→a) work. If anything fails, everything already done is put back.
    /// A journal on disk means even a crash or power cut can be recovered on next launch.
    /// </summary>
    public sealed class RenameRunner
    {
        public sealed class Move
        {
            public string From;
            public string To;
            internal string Temp;
        }

        public sealed class Outcome
        {
            public int Renamed;
            public string Error;
            public bool RolledBack;
        }

        readonly string _journalFolder;

        public RenameRunner(string journalFolder)
        {
            _journalFolder = journalFolder;
        }

        string PendingPath => Path.Combine(_journalFolder, "pending.txt");
        string LastPath => Path.Combine(_journalFolder, "last.txt");

        public Outcome Run(IList<Move> moves)
        {
            var outcome = new Outcome();
            if (moves.Count == 0) return outcome;

            string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
            for (int i = 0; i < moves.Count; i++)
                moves[i].Temp = Path.Combine(Path.GetDirectoryName(moves[i].From), ".relabel-" + tag + "-" + i + ".tmp");

            Directory.CreateDirectory(_journalFolder);
            AtomicFile.WriteAllText(PendingPath, string.Join("\n", moves.Select(m => m.From + "\t" + m.Temp + "\t" + m.To)));

            int phase1 = 0, phase2 = 0;
            try
            {
                for (; phase1 < moves.Count; phase1++) FileOps.Move(moves[phase1].From, moves[phase1].Temp);
                for (; phase2 < moves.Count; phase2++) FileOps.Move(moves[phase2].Temp, moves[phase2].To);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                outcome.Error = ex.Message;
                outcome.RolledBack = true;
                // Undo in reverse: finished files go back from their final name, the rest from their temp name.
                for (int i = phase2 - 1; i >= 0; i--) TryMove(moves[i].To, moves[i].From);
                for (int i = Math.Max(phase1, phase2) - 1; i >= phase2; i--) TryMove(moves[i].Temp, moves[i].From);
                TryDelete(PendingPath);
                return outcome;
            }

            outcome.Renamed = moves.Count;
            AtomicFile.WriteAllText(LastPath, DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + "\n" +
                string.Join("\n", moves.Select(m => m.From + "\t" + m.To)));
            TryDelete(PendingPath);
            return outcome;
        }

        /// <summary>The previous batch, for undo: (when, list of from→to).</summary>
        public bool TryReadLast(out DateTime when, out List<Move> moves)
        {
            when = default;
            moves = null;
            try
            {
                if (!File.Exists(LastPath)) return false;
                var lines = File.ReadAllLines(LastPath, Encoding.UTF8);
                if (lines.Length < 2 || !DateTime.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when)) return false;
                moves = lines.Skip(1).Select(l => l.Split('\t')).Where(p => p.Length == 2).Select(p => new Move { From = p[0], To = p[1] }).ToList();
                return moves.Count > 0;
            }
            catch (IOException) { return false; }
        }

        /// <summary>Reverses the previous batch. Files that were moved or renamed since are left alone.</summary>
        public Outcome UndoLast(out int skipped)
        {
            skipped = 0;
            if (!TryReadLast(out _, out var last)) return new Outcome();
            var reverse = new List<Move>();
            foreach (var m in last)
            {
                if (FileOps.Exists(m.To) && (!FileOps.Exists(m.From) || string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase)))
                    reverse.Add(new Move { From = m.To, To = m.From });
                else skipped++;
            }
            var outcome = Run(reverse);
            if (outcome.Error == null) TryDelete(LastPath);
            return outcome;
        }

        /// <summary>If a previous run was interrupted, puts every file back to its original name.</summary>
        public int RecoverInterrupted()
        {
            if (!File.Exists(PendingPath)) return 0;
            int restored = 0;
            try
            {
                var entries = File.ReadAllLines(PendingPath, Encoding.UTF8)
                    .Select(l => l.Split('\t')).Where(p => p.Length == 3).ToList();
                // Several passes, because in a swap one file can only go home after the other has left.
                bool progress = true;
                while (progress && entries.Count > 0)
                {
                    progress = false;
                    foreach (var p in entries.ToList())
                    {
                        string from = p[0], temp = p[1], to = p[2];
                        bool done = false;
                        if (FileOps.Exists(from) && !FileOps.Exists(temp)) done = true;   // never moved, or already back
                        else if (FileOps.Exists(temp) && !FileOps.Exists(from) && TryMove(temp, from)) { restored++; done = true; }
                        else if (!FileOps.Exists(temp) && FileOps.Exists(to) && !FileOps.Exists(from) && TryMove(to, from)) { restored++; done = true; }
                        if (done)
                        {
                            entries.Remove(p);
                            progress = true;
                        }
                    }
                }
            }
            catch (IOException) { }
            TryDelete(PendingPath);
            return restored;
        }

        static bool TryMove(string from, string to)
        {
            try { FileOps.Move(from, to); return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
