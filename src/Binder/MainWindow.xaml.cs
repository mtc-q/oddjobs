using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Binder
{
    public sealed class PageItem : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public string Name => System.IO.Path.GetFileName(Path);

        BitmapSource _thumb;
        public BitmapSource Thumb { get => _thumb; set { _thumb = value; Raise(nameof(Thumb)); } }

        int _rotation;
        public int Rotation { get => _rotation; set { _rotation = ((value % 360) + 360) % 360; Raise(nameof(Rotation)); } }

        int _number;
        public int Number { get => _number; set { _number = value; Raise(nameof(Number)); } }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public partial class MainWindow : OddWindow
    {
        const string DragFormat = "OddJobs.Binder.Page";

        readonly ObservableCollection<PageItem> _pages = new ObservableCollection<PageItem>();
        readonly Settings _settings = new Settings("Binder");
        Point _dragStart;
        PageItem _dragItem;
        string _lastPdf;
        bool _busy;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            Pages.ItemsSource = _pages;
            _pages.CollectionChanged += (s, e) => Renumber();
            DropTarget.Enable(this, AddPaths, EmptyState);

            SizeLetter.IsChecked = _settings.Get("size") == "letter";
            SizeFit.IsChecked = _settings.Get("size") == "fit";
            MarginNone.IsChecked = _settings.Get("margin") == "none";
            MarginNormal.IsChecked = _settings.Get("margin") == "normal";
            QualityCompact.IsChecked = _settings.GetBool("compact", false);
            Closing += (s, e) =>
            {
                _settings.Set("size", SizeLetter.IsChecked == true ? "letter" : SizeFit.IsChecked == true ? "fit" : "a4");
                _settings.Set("margin", MarginNone.IsChecked == true ? "none" : MarginNormal.IsChecked == true ? "normal" : "small");
                _settings.Set("compact", QualityCompact.IsChecked == true);
                _settings.Save();
            };
            Loaded += (s, e) => { if (args.Length > 0) AddPaths(args); };
            Refresh();
        }

        // ---------- Pages ------------------------------------------------------------

        void Add_Click(object sender, RoutedEventArgs e)
        {
            var files = Dialog.PickFiles(this, "Add images", "Images|*.jpg;*.jpeg;*.jpe;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.heif;*.webp|All files|*.*");
            if (files != null) AddPaths(files);
        }

        void AddPaths(string[] paths)
        {
            var files = new List<string>();
            foreach (var raw in paths)
            {
                string path;
                try { path = System.IO.Path.GetFullPath(raw); } catch (Exception) { continue; }
                if (Directory.Exists(path))
                    files.AddRange(FastDir.WalkFiles(path, false, null).Select(f => f.Path).OrderBy(f => f, NaturalComparer.Instance));
                else if (File.Exists(path)) files.Add(path);
            }
            var added = new List<PageItem>();
            foreach (var f in files.Where(f => PageMaker.Extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())))
            {
                var page = new PageItem { Path = f };
                _pages.Add(page);
                added.Add(page);
            }
            if (files.Count > 0 && added.Count == 0)
                SetStatus("Those files aren't images Binder can use. Try JPG, PNG, HEIC, TIFF or WebP.", "Warning");
            foreach (var page in added)
            {
                var p = page;
                Task.Run(() => Thumbnails.Load(p.Path, 320)).ContinueWith(t => p.Thumb = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
            }
            Refresh();
        }

        void Renumber()
        {
            for (int i = 0; i < _pages.Count; i++) _pages[i].Number = i + 1;
        }

        List<PageItem> SelectedPages => Pages.SelectedItems.Cast<PageItem>().OrderBy(p => _pages.IndexOf(p)).ToList();

        void MoveLeft_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
        void MoveRight_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

        void MoveSelected(int by)
        {
            var selected = SelectedPages;
            if (selected.Count == 0) return;
            var ordered = by < 0 ? selected : Enumerable.Reverse(selected).ToList();
            foreach (var page in ordered)
            {
                int i = _pages.IndexOf(page), j = i + by;
                if (j < 0 || j >= _pages.Count || selected.Contains(_pages[j])) continue;
                _pages.Move(i, j);
            }
        }

        void RotateRight_Click(object sender, RoutedEventArgs e)
        {
            foreach (var page in SelectedPages) page.Rotation += 90;
        }

        void Remove_Click(object sender, RoutedEventArgs e)
        {
            var selected = SelectedPages;
            int first = selected.Count > 0 ? _pages.IndexOf(selected[0]) : -1;
            foreach (var page in selected) _pages.Remove(page);
            if (_pages.Count > 0 && first >= 0) Pages.SelectedIndex = Math.Min(first, _pages.Count - 1);
            Refresh();
        }

        void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e) => PageTools.IsEnabled = Pages.SelectedItems.Count > 0;

        void Pages_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { Remove_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control) { RotateRight_Click(null, null); e.Handled = true; }
        }

        // Drag to reorder.
        void Pages_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _dragStart = e.GetPosition(null);
            _dragItem = ItemAt(e.OriginalSource as DependencyObject);
        }

        void Pages_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
            var delta = e.GetPosition(null) - _dragStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var item = _dragItem;
            _dragItem = null;
            DragDrop.DoDragDrop(Pages, new DataObject(DragFormat, item), DragDropEffects.Move);
        }

        void Pages_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DragFormat)) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        void Pages_Drop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DragFormat) is PageItem moving)) return;
            e.Handled = true;
            var target = ItemAt(e.OriginalSource as DependencyObject);
            int from = _pages.IndexOf(moving);
            int to = target != null ? _pages.IndexOf(target) : _pages.Count - 1;
            if (from >= 0 && to >= 0 && from != to) _pages.Move(from, to);
            Pages.SelectedItem = moving;
        }

        PageItem ItemAt(DependencyObject element)
        {
            while (element != null && !(element is ListBoxItem)) element = VisualTreeHelper.GetParent(element);
            return (element as ListBoxItem)?.DataContext as PageItem;
        }

        // ---------- Options ------------------------------------------------------------

        void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            Refresh();
        }

        BindOptions ReadOptions() => new BindOptions
        {
            Size = SizeFit.IsChecked == true ? PageSize.FitImage : SizeLetter.IsChecked == true ? PageSize.Letter : PageSize.A4,
            Margin = MarginNone.IsChecked == true ? PageMargin.None : MarginNormal.IsChecked == true ? PageMargin.Normal : PageMargin.Small,
            Compact = QualityCompact.IsChecked == true,
        };

        // ---------- Create -------------------------------------------------------------

        async void Create_Click(object sender, RoutedEventArgs e)
        {
            if (_pages.Count == 0 || _busy) return;
            string folder = System.IO.Path.GetDirectoryName(_pages[0].Path);
            string suggested = System.IO.Path.GetFileName(folder) is string n && n.Length > 0 ? n + ".pdf" : "Scans.pdf";
            string target = Dialog.SaveFile(this, "Save PDF", "PDF document|*.pdf", suggested, folder);
            if (target == null) return;

            var options = ReadOptions();
            var jobs = _pages.Select(p => (p.Path, p.Rotation)).ToList();
            _busy = true;
            Refresh();
            SetStatus("Making " + Format.Count(jobs.Count, "page") + "…", "Accent");
            try
            {
                long size = await Task.Run(() =>
                {
                    var made = new PdfPage[jobs.Count];
                    Parallel.For(0, jobs.Count, new ParallelOptions { MaxDegreeOfParallelism = 3 }, i => made[i] = PageMaker.Make(jobs[i].Path, jobs[i].Rotation, options));
                    AtomicFile.Write(target, s => PdfWriter.Write(s, made));
                    return new FileInfo(target).Length;
                });
                _lastPdf = target;
                OpenButton.Visibility = Visibility.Visible;
                SetStatus("Saved " + System.IO.Path.GetFileName(target) + ": " + Format.Count(jobs.Count, "page") + ", " + Format.Size(size) + ".", "Success");
            }
            catch (Exception ex)
            {
                string message = ex is AggregateException agg ? agg.InnerExceptions[0].Message : ex.Message;
                Dialog.Error(this, "The PDF was not created", "One of the images could not be read. If it's a HEIC photo, install “HEIF Image Extensions” from the Microsoft Store.", message);
                SetStatus("The PDF was not created.", "Danger");
            }
            finally
            {
                _busy = false;
                Refresh(keepStatus: true);
            }
        }

        void OpenPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_lastPdf != null && File.Exists(_lastPdf)) Shell.OpenSafely(_lastPdf);
        }

        void Refresh(bool keepStatus = false)
        {
            bool any = _pages.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            Pages.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            CountText.Text = any ? Format.Count(_pages.Count, "page") : "No pages yet";
            CreateButton.IsEnabled = any && !_busy;
            CreateButton.Content = _busy ? "Working…" : "Create PDF";

            bool fit = SizeFit.IsChecked == true;
            MarginBar.IsEnabled = !fit;
            MarginLabel.Opacity = fit ? 0.4 : 1;
            QualityHint.Text = QualityCompact.IsChecked == true
                ? "Pages are scaled down to 2000 pixels and compressed more. Good for email and forms with upload limits."
                : "JPEG photos go in untouched, and screenshots stay pixel-perfect. Nothing is recompressed unless it has to be rotated.";

            if (keepStatus) return;
            if (!any) SetStatus("Add images to start. Nothing leaves this PC.", "TextFaint");
            else SetStatus("Pages print in this order. Select a page to move, rotate or remove it.", "Accent");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
