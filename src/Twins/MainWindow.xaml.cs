using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Twins
{
    public partial class MainWindow : OddWindow
    {
        static readonly FileKind?[] Kinds = { null, FileKind.Photo, FileKind.Video, FileKind.Audio, FileKind.Document, FileKind.Archive, FileKind.Installer };

        readonly ObservableCollection<string> _folders = new ObservableCollection<string>();
        readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        readonly Settings _settings = new Settings("Twins");
        List<GroupRow> _groups = new List<GroupRow>();
        CancellationTokenSource _scan;
        CancellationTokenSource _preview;
        ScanResult _last;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            FolderList.ItemsSource = _folders;
            Results.ItemsSource = _rows;
            DropTarget.Enable(this, paths => { foreach (var p in paths) AddFolder(p); });

            FileRow.KeepOneRefused += () => SetStatus("At least one copy of every file is always kept.", "Warning");
            FileRow.MarksChanged += UpdateSelectionSummary;

            KindBox.SelectedIndex = Math.Max(0, Math.Min(Kinds.Length - 1, _settings.GetInt("kind", 0)));
            MinSizeBox.SelectedIndex = Math.Max(0, Math.Min(5, _settings.GetInt("minSize", 1)));
            HiddenBox.IsChecked = _settings.GetBool("hidden", false);

            if (args.Length > 0) foreach (var a in args) AddFolder(a);
            else
            {
                foreach (var f in (_settings.Get("folders") ?? "").Split('|').Where(Directory.Exists)) AddFolder(f);
                if (_folders.Count == 0)
                {
                    string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    if (Directory.Exists(pictures)) AddFolder(pictures);
                }
            }

            Closing += (s, e) =>
            {
                _scan?.Cancel();
                _settings.Set("folders", string.Join("|", _folders));
                _settings.Set("kind", KindBox.SelectedIndex);
                _settings.Set("minSize", MinSizeBox.SelectedIndex);
                _settings.Set("hidden", HiddenBox.IsChecked == true);
                _settings.Save();
            };

            UpdateEmptyState();
            SetStatus("Choose folders and press Find duplicates.", "TextFaint");
#if SCREENSHOT
            if (args.Length > 0) Loaded += (s, e) => Scan_Click(null, null);
#endif
        }

        // ---------- Folders ---------------------------------------------------------

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Add a folder to search", _folders.LastOrDefault());
            if (folder != null) AddFolder(folder);
        }

        void AddFolder(string path)
        {
            try
            {
                path = PathUtil.Normalize(path);
                if (File.Exists(path)) path = Path.GetDirectoryName(path);
                if (!Directory.Exists(path)) return;
                if (_folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))) return;
                _folders.Add(path);
            }
            catch (Exception) { }
        }

        void RemoveFolder_Click(object sender, RoutedEventArgs e) => _folders.Remove((string)((Button)sender).Tag);

        // ---------- Scan -----------------------------------------------------------

        async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_scan != null)
            {
                _scan.Cancel();
                return;
            }
            if (_folders.Count == 0)
            {
                SetStatus("Add at least one folder to search.", "Warning");
                return;
            }

            var options = new ScanOptions
            {
                Folders = _folders.ToList(),
                MinSize = long.Parse((string)((ComboBoxItem)MinSizeBox.SelectedItem).Tag),
                Kind = Kinds[Math.Max(0, KindBox.SelectedIndex)],
                IncludeHidden = HiddenBox.IsChecked == true,
            };

            _scan = new CancellationTokenSource();
            ScanButton.Content = "Stop";
            ProgressPanel.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            PhaseText.Text = "Looking at files";
            DetailText.Text = "";
            ClearResults();
            SetStatus("Searching…", "Accent");

            var progress = new Progress<ScanProgress>(p =>
            {
                PhaseText.Text = p.Phase;
                if (p.BytesToHash > 0)
                {
                    Progress.IsIndeterminate = false;
                    Progress.Maximum = p.BytesToHash;
                    Progress.Value = p.BytesHashed;
                    DetailText.Text = Format.Size(p.BytesHashed) + " of " + Format.Size(p.BytesToHash);
                }
                else
                {
                    DetailText.Text = Format.Count(p.FilesSeen, "file") + " so far";
                }
            });

            var token = _scan.Token;
            try
            {
                _last = await Task.Run(() => DuplicateScanner.Scan(options, progress, token), token);
                ShowResults(_last);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Search stopped.", "TextFaint");
            }
            finally
            {
                _scan = null;
                ScanButton.Content = "Find duplicates";
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        void ClearResults()
        {
            _groups = new List<GroupRow>();
            _rows.Clear();
            SelectBar.Visibility = Visibility.Collapsed;
            SummaryText.Text = "Searching…";
            SummaryDetail.Text = "";
            ShowPreview(null);
            UpdateEmptyState();
            UpdateSelectionSummary();
        }

        void ShowResults(ScanResult result)
        {
            FileRow.Roots = DuplicateScanner.DistinctRoots(_folders);
            _groups = result.Groups.Select(g => new GroupRow(g)).ToList();
            Rebuild();

            var notes = new List<string> { "Searched " + Format.Count(result.FilesScanned, "file") + " in " + Format.Duration(result.Elapsed) };
            if (result.Unreadable > 0) notes.Add(Format.Number(result.Unreadable) + " could not be read");
            if (result.CloudOnlySkipped > 0) notes.Add(Format.Number(result.CloudOnlySkipped) + " online-only skipped");
            SummaryDetail.Text = string.Join(" · ", notes);
            UpdateSummary();
            UpdateSelectionSummary();
#if SCREENSHOT
            if (Environment.GetEnvironmentVariable("ODDJOBS_DEMO") == "1" && _groups.Count > 0)
            {
                KeepOldest_Click(null, null);
                Results.SelectedIndex = 2;
            }
#endif
        }

        void Rebuild()
        {
            _rows.Clear();
            foreach (var g in _groups)
            {
                _rows.Add(g);
                foreach (var f in g.Files) _rows.Add(f);
            }
            SelectBar.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateEmptyState();
        }

        void UpdateSummary()
        {
            if (_groups.Count == 0)
            {
                SummaryText.Text = _last == null ? "No search yet" : "No duplicates found";
                return;
            }
            long wasted = _groups.Sum(g => g.Group.Size * (g.Files.Count - 1));
            SummaryText.Text = Format.Count(_groups.Count, "file has", "files have") + " copies · " + Format.Size(wasted) + " could be freed";
        }

        void UpdateEmptyState()
        {
            bool empty = _rows.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            Results.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (_last != null && _groups.Count == 0)
            {
                EmptyTitle.Text = "No duplicates";
                EmptyBody.Text = "Every file in these folders is unique. Nice and tidy.";
            }
        }

        // ---------- Selection helpers ----------------------------------------------------

        void KeepNewest_Click(object sender, RoutedEventArgs e) =>
            MarkAllBut(g => g.Files.OrderByDescending(f => f.File.Modified).ThenBy(f => f.File.Path.Length).First());

        void KeepOldest_Click(object sender, RoutedEventArgs e) =>
            MarkAllBut(g => g.Files.OrderBy(f => f.File.Modified).ThenBy(f => f.File.Path.Length).First());

        void KeepFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Keep the copies inside this folder", _folders.FirstOrDefault());
            if (folder == null) return;
            int sets = 0;
            foreach (var g in _groups)
            {
                var inside = g.Files.Where(f => PathUtil.IsInside(f.File.Path, folder)).ToList();
                if (inside.Count == 0) continue;
                sets++;
                foreach (var f in g.Files) f.SetMarked(!inside.Contains(f));
                g.Refresh();
            }
            UpdateSelectionSummary();
            if (sets == 0) SetStatus("No duplicates have a copy inside " + folder + ".", "Warning");
        }

        void ClearMarks_Click(object sender, RoutedEventArgs e)
        {
            foreach (var g in _groups)
            {
                foreach (var f in g.Files) f.SetMarked(false);
                g.Refresh();
            }
            UpdateSelectionSummary();
        }

        void MarkAllBut(Func<GroupRow, FileRow> keep)
        {
            foreach (var g in _groups)
            {
                var kept = keep(g);
                foreach (var f in g.Files) f.SetMarked(f != kept);
                g.Refresh();
            }
            UpdateSelectionSummary();
        }

        void UpdateSelectionSummary()
        {
            var marked = _groups.SelectMany(g => g.Files).Where(f => f.Marked).ToList();
            RecycleButton.IsEnabled = marked.Count > 0 && _scan == null;
            RecycleButton.Content = marked.Count > 0 ? "Move " + Format.Count(marked.Count, "file") + " to Recycle Bin" : "Move to Recycle Bin";
            if (marked.Count > 0)
                SetStatus(Format.Count(marked.Count, "copy", "copies") + " selected · " + Format.Size(marked.Sum(f => f.File.Size)) + " will be freed", "Danger");
            else if (_groups.Count > 0)
                SetStatus("Tick the copies you don't need, or use the Select buttons.", "Accent");
            else if (_last != null)
                SetStatus("Nothing to clean up.", "Success");
        }

        void Results_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && Results.SelectedItem is FileRow row)
            {
                row.Marked = !row.Marked;
                e.Handled = true;
            }
        }

        // ---------- Preview -------------------------------------------------------------

        void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPreview(Results.SelectedItem as FileRow);

        void ShowPreview(FileRow row)
        {
            _preview?.Cancel();
            PreviewPanel.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
            PreviewHint.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
            PreviewImage.Source = null;
            PreviewGlyph.Visibility = Visibility.Visible;
            if (row == null) return;

            PreviewName.Text = row.Name;
            PreviewFolder.Text = row.Folder;
            PreviewSize.Text = Format.Size(row.File.Size);
            PreviewDate.Text = Format.Date(row.File.Modified);
            PreviewHash.Text = row.File.Hash;
            PreviewHash.ToolTip = row.File.Hash;

            if (FileKinds.Of(row.File.Path) != FileKind.Photo) return;
            var cts = new CancellationTokenSource();
            _preview = cts;
            string path = row.File.Path;
            Task.Run(() => Thumbnails.Load(path, 560)).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.Result == null) return;
                PreviewImage.Source = t.Result;
                PreviewGlyph.Visibility = Visibility.Collapsed;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (Results.SelectedItem is FileRow row) Shell.Reveal(row.File.Path);
        }

        void Open_Click(object sender, RoutedEventArgs e)
        {
            if (Results.SelectedItem is FileRow row) Shell.OpenSafely(row.File.Path);
        }

        void Results_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem is FileRow row) Shell.OpenSafely(row.File.Path);
        }

        // ---------- Remove ----------------------------------------------------------------

        void Recycle_Click(object sender, RoutedEventArgs e)
        {
            var marked = _groups.SelectMany(g => g.Files).Where(f => f.Marked).ToList();
            if (marked.Count == 0) return;
            long bytes = marked.Sum(f => f.File.Size);
            if (!Dialog.Confirm(this, "Move " + Format.Count(marked.Count, "file") + " to the Recycle Bin?",
                    "This frees " + Format.Size(bytes) + ". At least one copy of every file stays where it is, and anything you remove can be restored from the Recycle Bin.",
                    "Move to Recycle Bin", danger: true))
                return;

            // Only touch files that are still exactly as they were when scanned.
            var changed = new List<FileRow>();
            var safe = new List<FileRow>();
            foreach (var row in marked)
            {
                if (FastDir.TryGetEntry(row.File.Path, out var now) && now.Size == row.File.Size &&
                    Math.Abs((now.LastWriteLocal - row.File.Modified).TotalSeconds) < 2)
                    safe.Add(row);
                else changed.Add(row);
            }

            var failed = new HashSet<string>(RecycleBin.Send(safe.Select(r => r.File.Path), new WindowInteropHelper(this).Handle), StringComparer.OrdinalIgnoreCase);
            var removed = safe.Where(r => !failed.Contains(r.File.Path)).ToList();

            foreach (var row in removed) row.Group.Files.Remove(row);
            foreach (var row in changed) row.SetMarked(false);
            _groups = _groups.Where(g => g.Files.Count > 1).ToList();
            foreach (var g in _groups) g.Refresh();
            Rebuild();
            UpdateSummary();
            UpdateSelectionSummary();

            string message = "Moved " + Format.Count(removed.Count, "file") + " to the Recycle Bin, freeing " + Format.Size(removed.Sum(r => r.File.Size)) + ".";
            if (failed.Count > 0) message += " " + Format.Count(failed.Count, "file") + " could not be moved (open in another app?).";
            if (changed.Count > 0) message += " " + Format.Count(changed.Count, "file") + " changed since the search and were left alone.";
            SetStatus(message, failed.Count + changed.Count > 0 ? "Warning" : "Success");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
