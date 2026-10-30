using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Scrub
{
    public sealed class PhotoTag
    {
        public string Text { get; set; }
        public bool IsWarning { get; set; }
        public bool IsGood { get; set; }
    }

    public sealed class PhotoItem : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);
        public long Size { get; set; }

        BitmapSource _thumb;
        public BitmapSource Thumb { get => _thumb; set { _thumb = value; Raise(nameof(Thumb)); } }

        MetadataReport _report;
        public MetadataReport Report { get => _report; set { _report = value; Raise(nameof(Report)); } }

        List<PhotoTag> _tags = new List<PhotoTag> { new PhotoTag { Text = "Reading…" } };
        public List<PhotoTag> Tags { get => _tags; set { _tags = value; Raise(nameof(Tags)); } }

        string _state = "";
        public string StateText { get => _state; set { _state = value; Raise(nameof(StateText)); } }

        public string CleanPath { get; set; }
        public string Error { get; set; }
        public bool Done => CleanPath != null;

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public partial class MainWindow : OddWindow
    {
        readonly ObservableCollection<PhotoItem> _items = new ObservableCollection<PhotoItem>();
        readonly HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Settings _settings = new Settings("Scrub");
        CancellationTokenSource _preview;
        string _lastOutput;
        bool _busy;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            List.ItemsSource = _items;
            DropTarget.Enable(this, AddPaths, EmptyState);
            DestinationBox.SelectedIndex = Math.Max(0, Math.Min(1, _settings.GetInt("destination", 0)));
            SizeBox.SelectedIndex = Math.Max(0, Math.Min(4, _settings.GetInt("size", 0)));
            QualitySlider.Value = Math.Max(50, Math.Min(100, _settings.GetInt("quality", 90)));
            Closing += (s, e) =>
            {
                _settings.Set("destination", Math.Min(1, DestinationBox.SelectedIndex));   // never remember "replace originals"
                _settings.Set("size", SizeBox.SelectedIndex);
                _settings.Set("quality", (int)QualitySlider.Value);
                _settings.Save();
            };
            Loaded += (s, e) => { if (args.Length > 0) AddPaths(args); };
            Refresh();
        }

        void Quality_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (QualityText != null) QualityText.Text = ((int)e.NewValue).ToString();
        }

        // ---------- Adding ------------------------------------------------------------

        void AddPhotos_Click(object sender, RoutedEventArgs e)
        {
            var files = Dialog.PickFiles(this, "Add photos", "Photos|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.tif;*.tiff;*.heic;*.heif;*.webp;*.bmp;*.gif|All files|*.*");
            if (files != null) AddPaths(files);
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Add every photo in a folder");
            if (folder != null) AddPaths(new[] { folder });
        }

        void AddPaths(string[] paths)
        {
            var added = new List<PhotoItem>();
            foreach (var raw in paths)
            {
                string path;
                try { path = System.IO.Path.GetFullPath(raw); } catch (Exception) { continue; }
                IEnumerable<(string, DirEntry)> files;
                if (Directory.Exists(path)) files = FastDir.WalkFiles(path, false, null).Where(f => !string.Equals(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(f.Path)), "Cleaned", StringComparison.OrdinalIgnoreCase));
                else if (FastDir.TryGetEntry(path, out var single)) files = new[] { (path, single) };
                else continue;

                foreach (var (file, entry) in files)
                {
                    if (!PhotoCleaner.IsSupported(file) || !_known.Add(file)) continue;
                    var item = new PhotoItem { Path = file, Size = entry.Size };
                    _items.Add(item);
                    added.Add(item);
                }
            }
            if (added.Count == 0) { Refresh(); return; }
            if (List.SelectedIndex < 0) List.SelectedIndex = 0;
            Refresh();
            Inspect(added);
        }

        void Inspect(List<PhotoItem> items)
        {
            Task.Run(() =>
            {
                Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 3 }, item =>
                {
                    var report = PhotoCleaner.Inspect(item.Path);
                    var thumb = Thumbnails.Load(item.Path, 120);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        item.Report = report;
                        item.Thumb = thumb;
                        item.Tags = TagsFor(report);
                        if (List.SelectedItem == item) ShowDetails(item);
                        Refresh();
                    }));
                });
            });
        }

        static List<PhotoTag> TagsFor(MetadataReport r)
        {
            var tags = new List<PhotoTag>();
            if (r.Error != null) return new List<PhotoTag> { new PhotoTag { Text = "Can't read", IsWarning = true } };
            if (r.HasLocation) tags.Add(new PhotoTag { Text = "Location", IsWarning = true });
            if (r.Camera != null) tags.Add(new PhotoTag { Text = "Device" });
            if (r.Taken != null) tags.Add(new PhotoTag { Text = "Date & time" });
            if (r.Author != null || r.Copyright != null) tags.Add(new PhotoTag { Text = "Name", IsWarning = true });
            if (r.Software != null) tags.Add(new PhotoTag { Text = "Software" });
            if (r.Comment != null) tags.Add(new PhotoTag { Text = "Comment" });
            if (tags.Count == 0 && r.Blocks.Count > 0) tags.Add(new PhotoTag { Text = string.Join(", ", r.Blocks) });
            if (tags.Count == 0) tags.Add(new PhotoTag { Text = "Nothing hidden", IsGood = true });
            return tags;
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _items.Clear();
            _known.Clear();
            OpenOutputButton.Visibility = Visibility.Collapsed;
            ShowDetails(null);
            Refresh();
        }

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || _busy) return;
            if (List.SelectedItem is PhotoItem item)
            {
                int index = List.SelectedIndex;
                _items.Remove(item);
                _known.Remove(item.Path);
                List.SelectedIndex = Math.Min(index, _items.Count - 1);
                Refresh();
            }
        }

        // ---------- Details ------------------------------------------------------------

        void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(List.SelectedItem as PhotoItem);

        void ShowDetails(PhotoItem item)
        {
            _preview?.Cancel();
            Details.Visibility = item == null ? Visibility.Collapsed : Visibility.Visible;
            DetailsHint.Visibility = item == null ? Visibility.Visible : Visibility.Collapsed;
            if (item == null) return;

            DetailName.Text = item.Name;
            var r = item.Report;
            DetailSize.Text = (r != null && r.Width > 0 ? r.Width + " × " + r.Height + " px · " : "") + Format.Size(item.Size);

            if (item.Done)
                SetVerdict("Clean copy saved and checked byte by byte: it contains only the picture.\n" + item.CleanPath, "SuccessSoft", "Success");
            else if (item.Error != null)
                SetVerdict(item.Error, "DangerSoft", "Danger");
            else if (r == null)
                SetVerdict("Reading…", "Surface2", "TextMuted");
            else if (r.Error != null)
                SetVerdict(r.Error, "DangerSoft", "Danger");
            else if (r.HasLocation)
                SetVerdict("This photo reveals where it was taken. Anyone you send it to can put it on a map.", "DangerSoft", "Danger");
            else if (r.HasAnything)
                SetVerdict("No location, but it still carries details about the device and when it was taken.", "WarningSoft", "Warning");
            else
                SetVerdict("Nothing hidden was found in this picture.", "SuccessSoft", "Success");

            Facts.Children.Clear();
            Facts.RowDefinitions.Clear();
            Facts.ColumnDefinitions.Clear();
            Facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            Facts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (r != null && r.Error == null)
            {
                AddFact("Location", r.LocationText, true);
                AddFact("Device", r.Camera);
                AddFact("Taken", r.Taken);
                AddFact("Software", r.Software);
                AddFact("Author", r.Author, true);
                AddFact("Copyright", r.Copyright);
                AddFact("Comment", r.Comment);
                AddFact("Hidden blocks", r.Blocks.Count > 0 ? string.Join(", ", r.Blocks) : null);
                if (Facts.RowDefinitions.Count == 0) AddFact("", "Nothing");
            }

            PreviewImage.Source = item.Thumb;
            var cts = new CancellationTokenSource();
            _preview = cts;
            string path = item.Path;
            Task.Run(() => Thumbnails.Load(path, 700)).ContinueWith(t =>
            {
                if (!cts.IsCancellationRequested && t.Result != null) PreviewImage.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetVerdict(string text, string background, string foreground)
        {
            VerdictText.Text = text;
            Verdict.SetResourceReference(Border.BackgroundProperty, background);
            VerdictText.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        }

        void AddFact(string label, string value, bool sensitive = false)
        {
            if (string.IsNullOrEmpty(value)) return;
            int row = Facts.RowDefinitions.Count;
            Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Margin = new Thickness(0, 0, 12, 8), FontSize = 12 };
            l.SetResourceReference(TextBlock.ForegroundProperty, "TextFaint");
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            if (sensitive) { v.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); v.FontWeight = FontWeights.SemiBold; }
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            Facts.Children.Add(l);
            Facts.Children.Add(v);
        }

        // ---------- Cleaning -----------------------------------------------------------

        async void Clean_Click(object sender, RoutedEventArgs e)
        {
            var todo = _items.Where(i => !i.Done && i.Report != null && i.Report.Error == null).ToList();
            if (todo.Count == 0 || _busy) return;

            string chosenFolder = null;
            bool replace = DestinationBox.SelectedIndex == 2;
            if (DestinationBox.SelectedIndex == 1)
            {
                chosenFolder = Dialog.PickFolder(this, "Save clean copies to", _settings.Get("lastOutput"));
                if (chosenFolder == null) return;
                _settings.Set("lastOutput", chosenFolder);
            }
            if (replace && !Dialog.Confirm(this, "Replace " + Format.Count(todo.Count, "original") + "?",
                    "Each photo is replaced by its clean copy. The originals, with all their hidden data, go to the Recycle Bin, so you can still get them back.",
                    "Replace originals", danger: true))
                return;

            var options = new CleanOptions
            {
                MaxEdge = int.Parse((string)((ComboBoxItem)SizeBox.SelectedItem).Tag),
                Quality = (int)QualitySlider.Value,
            };

            _busy = true;
            Refresh();
            int done = 0, failed = 0;
            foreach (var item in todo) item.StateText = "Waiting";
            SetStatus("Cleaning…", "Accent");

            await Task.Run(() => Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 3 }, item =>
            {
                Dispatcher.BeginInvoke(new Action(() => item.StateText = "Cleaning…"));
                string output = null, error = null, warning = null;
                try
                {
                    string folder = chosenFolder ?? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path), "Cleaned");
                    output = PhotoCleaner.Clean(item.Path, folder, options, replace, out warning);
                    Interlocked.Increment(ref done);
                }
                catch (Exception ex)
                {
                    error = PhotoCleaner.FriendlyError(item.Path, ex);
                    Interlocked.Increment(ref failed);
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (output != null)
                    {
                        item.CleanPath = output;
                        item.StateText = "Clean";
                        item.Tags = new List<PhotoTag> { new PhotoTag { Text = "Clean copy saved", IsGood = true } };
                        _lastOutput = output;
                        if (warning != null) item.Error = warning;
                    }
                    else
                    {
                        item.Error = error;
                        item.StateText = "Failed";
                    }
                    if (List.SelectedItem == item) ShowDetails(item);
                }));
            }));

            _busy = false;
            OpenOutputButton.Visibility = _lastOutput != null ? Visibility.Visible : Visibility.Collapsed;
            Refresh();
            string where = replace ? "in place of the originals" : chosenFolder != null ? "in " + chosenFolder : "in “Cleaned” folders next to the originals";
            if (failed == 0) SetStatus("Cleaned " + Format.Count(done, "photo") + ", saved " + where + ". Each one was checked to contain nothing but pixels.", "Success");
            else SetStatus("Cleaned " + Format.Count(done, "photo") + ". " + Format.Count(failed, "photo") + " could not be cleaned; select it to see why.", "Warning");
        }

        void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            if (_lastOutput != null) Shell.Reveal(_lastOutput);
        }

        void Refresh()
        {
            bool any = _items.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.IsEnabled = any && !_busy;

            int located = _items.Count(i => i.Report?.HasLocation == true && !i.Done);
            int pending = _items.Count(i => !i.Done && i.Report != null && i.Report.Error == null);
            CountText.Text = any ? Format.Count(_items.Count, "photo") : "No photos yet";
            CountDetail.Text = !any ? "" : located > 0 ? Format.Count(located, "photo reveals where it was", "photos reveal where they were") + " taken" : "None of these reveal a location";
            CountDetail.SetResourceReference(TextBlock.ForegroundProperty, located > 0 ? "Danger" : "TextMuted");

            CleanButton.IsEnabled = pending > 0 && !_busy;
            CleanButton.Content = _busy ? "Cleaning…" : pending > 0 ? "Clean " + Format.Count(pending, "photo") : "Clean photos";

            if (!_busy && !any) SetStatus("Add photos to see what they give away. Nothing is changed until you press Clean.", "TextFaint");
            else if (!_busy && pending > 0) SetStatus("Clean copies keep the picture and drop everything else: location, device, dates, names and thumbnails.", "Accent");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
