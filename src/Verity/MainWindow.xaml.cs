using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Verity
{
    public enum Verdict { None, Match, Mismatch, Missing, Error }

    public sealed class HashLine
    {
        public string Algorithm { get; set; }
        public string Value { get; set; }
        public bool IsMatch { get; set; }
    }

    public sealed class FileCheck : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string ListedDigest { get; set; }     // from a checksum file
        public string ListedAlgorithm { get; set; }
        public string ListName { get; set; }
        public readonly Dictionary<string, string> Hashes = new Dictionary<string, string>();

        double _progress;
        public double Progress { get => _progress; set { _progress = value; Raise(nameof(Progress)); } }

        bool _busy;
        public bool Busy { get => _busy; set { _busy = value; Raise(nameof(Busy)); } }

        string _detail;
        public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }

        List<HashLine> _lines = new List<HashLine>();
        public List<HashLine> Lines { get => _lines; set { _lines = value; Raise(nameof(Lines)); } }

        Verdict _verdict;
        public Verdict Verdict { get => _verdict; set { _verdict = value; Raise(nameof(Verdict)); Raise(nameof(HasVerdict)); } }
        public bool HasVerdict => _verdict != Verdict.None;

        string _verdictText;
        public string VerdictText { get => _verdictText; set { _verdictText = value; Raise(nameof(VerdictText)); } }

        public string Error { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public partial class MainWindow : OddWindow
    {
        readonly ObservableCollection<FileCheck> _files = new ObservableCollection<FileCheck>();
        readonly SemaphoreSlim _queue = new SemaphoreSlim(1, 1);
        readonly CancellationTokenSource _closing = new CancellationTokenSource();
        string _expected;
        string _expectedAlgorithm;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
            if (Environment.GetEnvironmentVariable("ODDJOBS_DEMO") == "1" && args.Length > 0)
                Loaded += (s, e) => ExpectedBox.Text = FileHasher.Hash(args[0], new[] { "SHA-256" }, null, CancellationToken.None)["SHA-256"].ToUpperInvariant();
#endif
            Files.ItemsSource = _files;
            DropTarget.Enable(this, AddPaths, EmptyState);
            Closing += (s, e) => _closing.Cancel();
            Loaded += (s, e) => { if (args.Length > 0) AddPaths(args); };
            Refresh();
        }

        IReadOnlyCollection<string> Selected =>
            new[] { AlgMD5, AlgSHA1, AlgSHA256, AlgSHA384, AlgSHA512 }.Where(t => t.IsChecked == true).Select(t => (string)t.Tag).ToList();

        // ---------- Adding -------------------------------------------------------------

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var files = Dialog.PickFiles(this, "Choose files to check", "All files|*.*");
            if (files != null) AddPaths(files);
        }

        void AddPaths(string[] paths)
        {
            foreach (var raw in paths)
            {
                string path;
                try { path = System.IO.Path.GetFullPath(raw); } catch (Exception) { continue; }
                if (Directory.Exists(path))
                {
                    foreach (var (file, _) in FastDir.WalkFiles(path, false, null).Take(5000)) AddFile(file, null);
                    continue;
                }
                if (!File.Exists(path)) continue;
                if (ChecksumList.LooksLikeList(path)) AddList(path);
                else AddFile(path, null);
            }
            Refresh();
        }

        void AddList(string listPath)
        {
            List<ListedFile> entries;
            try { entries = ChecksumList.Parse(listPath); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialog.Error(this, "Could not read the checksum file", ex.Message);
                return;
            }
            if (entries.Count == 0)
            {
                AddFile(listPath, null);   // not a list after all; just fingerprint it
                return;
            }
            foreach (var entry in entries)
            {
                var check = new FileCheck
                {
                    Path = entry.Path,
                    Name = entry.Name,
                    ListedDigest = entry.Digest,
                    ListedAlgorithm = entry.Algorithm,
                    ListName = System.IO.Path.GetFileName(listPath),
                };
                if (entry.Path == null || !File.Exists(entry.Path))
                {
                    check.Detail = entry.Path == null ? "Listed in " + check.ListName + ", but outside its folder, so not checked" : "Listed in " + check.ListName + ", but not found";
                    check.Verdict = Verdict.Missing;
                    check.VerdictText = "Not found";
                    _files.Add(check);
                    continue;
                }
                check.Size = new FileInfo(entry.Path).Length;
                check.Detail = Format.Size(check.Size) + " · checking against " + check.ListName;
                _files.Add(check);
                Enqueue(check);
            }
        }

        void AddFile(string path, string listed)
        {
            if (_files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase) && f.ListedDigest == null)) return;
            var check = new FileCheck { Path = path, Name = System.IO.Path.GetFileName(path), Size = new FileInfo(path).Length };
            check.Detail = Format.Size(check.Size) + " · " + System.IO.Path.GetDirectoryName(path);
            _files.Add(check);
            Enqueue(check);
        }

        // ---------- Hashing ------------------------------------------------------------

        async void Enqueue(FileCheck check)
        {
            var needed = Needed(check);
            if (needed.Count == 0) { Evaluate(check); return; }

            check.Busy = true;
            check.Progress = 0;
            await _queue.WaitAsync();
            try
            {
                if (!_files.Contains(check)) return;
                long done = 0;
                var started = DateTime.UtcNow;
                var progress = new Progress<long>(bytes =>
                {
                    check.Progress = check.Size > 0 ? (double)bytes / check.Size : 1;
                    double seconds = (DateTime.UtcNow - started).TotalSeconds;
                    if (seconds > 0.5) check.Detail = Format.Size(bytes) + " of " + Format.Size(check.Size) + " · " + Format.Rate(bytes / seconds);
                });
                IProgress<long> report = progress;
                var lastReport = DateTime.UtcNow;
                var result = await Task.Run(() => FileHasher.Hash(check.Path, needed, n =>
                {
                    done += n;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100) { lastReport = DateTime.UtcNow; report.Report(done); }
                }, _closing.Token));
                foreach (var pair in result) check.Hashes[pair.Key] = pair.Value;
                check.Detail = Format.Size(check.Size) + " · " + (check.ListName != null ? "checked against " + check.ListName : System.IO.Path.GetDirectoryName(check.Path));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                check.Error = ex.Message;
                check.Detail = ex.Message;
            }
            finally
            {
                check.Busy = false;
                _queue.Release();
            }
            Evaluate(check);
            Refresh();
        }

        List<string> Needed(FileCheck check)
        {
            var wanted = new HashSet<string>(Selected);
            if (check.ListedAlgorithm != null) wanted.Add(check.ListedAlgorithm);
            if (_expectedAlgorithm != null) wanted.Add(_expectedAlgorithm);
            return wanted.Where(a => !check.Hashes.ContainsKey(a)).ToList();
        }

        void Evaluate(FileCheck check)
        {
            if (check.Error != null)
            {
                check.Verdict = Verdict.Error;
                check.VerdictText = "Can't read";
                check.Lines = new List<HashLine>();
                return;
            }
            if (check.Hashes.Count == 0) return;

            string target = check.ListedDigest ?? _expected;
            string targetAlg = check.ListedAlgorithm ?? _expectedAlgorithm;
            var selected = Selected;
            check.Lines = Hashing.Names
                .Where(a => check.Hashes.ContainsKey(a) && (selected.Contains(a) || a == targetAlg))
                .Select(a => new HashLine { Algorithm = a, Value = check.Hashes[a], IsMatch = target != null && a == targetAlg && Hashing.SameDigest(check.Hashes[a], target) })
                .ToList();

            if (target == null || targetAlg == null || !check.Hashes.ContainsKey(targetAlg))
            {
                check.Verdict = Verdict.None;
                return;
            }
            bool match = Hashing.SameDigest(check.Hashes[targetAlg], target);
            check.Verdict = match ? Verdict.Match : Verdict.Mismatch;
            check.VerdictText = match ? "✓  " + targetAlg + " matches" : "✗  Does not match";
        }

        // ---------- Expected value -------------------------------------------------------

        void Expected_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = ExpectedBox.Text;
            _expected = Hashing.NormalizeDigest(text, out _expectedAlgorithm);
            if (string.IsNullOrWhiteSpace(text)) ExpectedKind.Text = "";
            else if (_expected == null) ExpectedKind.Text = "That doesn't look like a checksum";
            else ExpectedKind.Text = "Looks like " + _expectedAlgorithm;

            foreach (var f in _files)
            {
                if (f.ListedDigest != null) continue;
                if (Needed(f).Count > 0 && !f.Busy && f.Error == null) Enqueue(f);
                else Evaluate(f);
            }
            Refresh();
        }

        void Algorithm_Click(object sender, RoutedEventArgs e)
        {
            if (Selected.Count == 0) ((ToggleButton)sender).IsChecked = true;
            foreach (var f in _files.Where(f => f.Error == null && !f.Busy && f.Path != null && File.Exists(f.Path)))
            {
                if (Needed(f).Count > 0) Enqueue(f);
                else Evaluate(f);
            }
        }

        // ---------- Actions -------------------------------------------------------------

        void CopyHash_Click(object sender, RoutedEventArgs e)
        {
            string value = (string)((Button)sender).Tag;
            try
            {
                Clipboard.SetDataObject(value, true);
                SetStatus("Copied " + value.Substring(0, Math.Min(16, value.Length)) + "… to the clipboard.", "Success");
            }
            catch (COMException) { SetStatus("The clipboard is busy. Try again.", "Warning"); }
        }

        void Remove_Click(object sender, RoutedEventArgs e)
        {
            _files.Remove((FileCheck)((Button)sender).Tag);
            Refresh();
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            _files.Clear();
            ExpectedBox.Clear();
            Refresh();
        }

        void SaveList_Click(object sender, RoutedEventArgs e)
        {
            var ready = _files.Where(f => f.Path != null && f.Hashes.ContainsKey("SHA-256") && f.ListedDigest == null).ToList();
            if (ready.Count == 0)
            {
                AlgSHA256.IsChecked = true;
                Algorithm_Click(AlgSHA256, null);
                SetStatus("Calculating SHA-256 first. Try again in a moment.", "Warning");
                return;
            }
            string folder = System.IO.Path.GetDirectoryName(ready[0].Path);
            string path = Dialog.SaveFile(this, "Save checksum file", "Checksum list|*.txt;*.sha256|All files|*.*", "SHA256SUMS.txt", folder);
            if (path == null) return;
            try
            {
                string listFolder = System.IO.Path.GetDirectoryName(path);
                AtomicFile.WriteAllText(path, ChecksumList.Write(listFolder, ready.Select(f => (f.Path, f.Hashes["SHA-256"]))));
                SetStatus("Saved " + Format.Count(ready.Count, "checksum") + " to " + path, "Success");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialog.Error(this, "Could not save", ex.Message);
            }
        }

        void Refresh()
        {
            bool any = _files.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            ClearButton.IsEnabled = any;
            SaveListButton.IsEnabled = _files.Any(f => f.ListedDigest == null && f.Path != null);

            // One pasted checksum is a question about which file is the right one. Once a file matches,
            // the others aren't "wrong", they are just other files.
            bool pastedMatched = _files.Any(f => f.ListedDigest == null && f.Verdict == Verdict.Match);
            foreach (var f in _files.Where(f => f.ListedDigest == null && f.Verdict == Verdict.Mismatch && pastedMatched))
            {
                f.Verdict = Verdict.None;
                f.VerdictText = "";
            }

            int busy = _files.Count(f => f.Busy);
            int match = _files.Count(f => f.Verdict == Verdict.Match);
            int mismatch = _files.Count(f => f.Verdict == Verdict.Mismatch);
            int missing = _files.Count(f => f.Verdict == Verdict.Missing || f.Verdict == Verdict.Error);
            int judged = match + mismatch;

            Summary.Visibility = Visibility.Collapsed;
            if (busy > 0) SetStatus("Reading " + Format.Count(busy, "file") + "…", "Accent");
            else if (judged > 0)
            {
                if (mismatch == 0 && missing == 0)
                    ShowSummary(true, judged == 1 ? "It matches" : "All " + judged + " files match",
                        "Every byte is exactly what the published checksum describes. The file was not damaged or changed on the way to you.");
                else if (mismatch > 0)
                    ShowSummary(false, Format.Count(mismatch, "file does", "files do") + " not match",
                        "A different checksum means a different file: an incomplete download, a newer version, or a file someone swapped. Don't open it until you know why. Download it again from the official site and check once more.");
                else
                    ShowSummary(false, Format.Count(missing, "listed file was", "listed files were") + " not checked",
                        "The checksum file mentions files that are not in its folder.");
                SetStatus(Format.Count(match, "match", "matches") + ", " + Format.Count(mismatch, "mismatch", "mismatches") + (missing > 0 ? ", " + missing + " not found" : "") + ".", mismatch > 0 ? "Danger" : "Success");
            }
            else if (_files.Count == 2 && _files.All(f => f.Hashes.ContainsKey("SHA-256")))
            {
                bool same = Hashing.SameDigest(_files[0].Hashes["SHA-256"], _files[1].Hashes["SHA-256"]);
                SetStatus(same ? "These two files are identical, byte for byte." : "These two files are different.", same ? "Success" : "Warning");
            }
            else if (any) SetStatus("Paste a checksum above to compare, or copy a fingerprint to share it.", "TextFaint");
            else SetStatus("Files are only read, never changed. Nothing is uploaded anywhere.", "TextFaint");
        }

        void ShowSummary(bool good, string title, string body)
        {
            Summary.Visibility = Visibility.Visible;
            Summary.SetResourceReference(Border.BackgroundProperty, good ? "SuccessSoft" : "DangerSoft");
            SummaryTitle.Text = title;
            SummaryBody.Text = body;
            SummaryGlyph.Text = good ? Icons.Check : Icons.Warning;
            foreach (var t in new[] { SummaryTitle, SummaryBody, SummaryGlyph })
                t.SetResourceReference(TextBlock.ForegroundProperty, good ? "Success" : "Danger");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
