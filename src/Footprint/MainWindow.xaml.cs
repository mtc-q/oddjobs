using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Footprint
{
    public partial class MainWindow : OddWindow
    {
        sealed class DriveCard
        {
            public string Root { get; set; }
            public string Title { get; set; }
            public string FreeText { get; set; }
            public double UsedWidth { get; set; }
            public Brush BarBrush { get; set; }
        }

        sealed class Crumb
        {
            public string Name { get; set; }
            public Node Node { get; set; }
            public bool ShowChevron { get; set; }
        }

        sealed class NodeRow
        {
            public Node Node { get; set; }
            public string Name => Node.Name;
            public string SizeText => Format.Size(Node.Size);
            public double ShareWidth { get; set; }
            public string ShareText { get; set; }
            public string FilesText => Node.IsDir ? Format.Number(Node.FileCount) : Node.IsAggregate ? Format.Number(Node.FileCount) : "";
            public Brush Swatch { get; set; }
            public FontWeight Weight => Node.IsDir ? FontWeights.SemiBold : FontWeights.Normal;
        }

        sealed class LegendItem
        {
            public Brush Swatch { get; set; }
            public string Label { get; set; }
            public string SizeText { get; set; }
        }

        Node _tree;
        Node _current;
        Node _selected;
        CancellationTokenSource _scan;
        ScanStats _stats;
        readonly DispatcherTimer _tick;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            ColumnFill.Apply(List, 3, 1.35, 1.9, 1);
            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _tick.Tick += (s, e) => UpdateScanProgress();

            Map.HoverChanged += node => ShowHover(node);
            Map.Clicked += node => Select(node);
            Map.DoubleClicked += node => { if (node.IsDir) Open(node); else if (node.CanAct) Shell.Reveal(node.FullPath); };
            Map.RightClicked += (node, p) => { Select(node); ShowMenu(node); };
            Theme.Changed += LoadDrives;

            DropTarget.Enable(this, paths =>
            {
                var folder = paths.FirstOrDefault(Directory.Exists);
                if (folder != null) StartScan(folder);
            });

            LoadDrives();
            SetStatus("Pick a drive or folder to scan. Footprint only reads folder listings, never file contents.", "TextFaint");
            Loaded += (s, e) =>
            {
                var folder = args.FirstOrDefault(Directory.Exists);
                if (folder != null) StartScan(folder);
            };
            Closing += (s, e) => _scan?.Cancel();
        }

        // ---------- Drives ----------------------------------------------------------

        void LoadDrives()
        {
            var cards = new List<DriveCard>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable)) continue;
                    double used = d.TotalSize > 0 ? 1 - (double)d.TotalFreeSpace / d.TotalSize : 0;
                    string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : "Local disk") : d.VolumeLabel;
                    cards.Add(new DriveCard
                    {
                        Root = d.RootDirectory.FullName,
                        Title = d.Name.TrimEnd('\\') + "  " + label,
                        FreeText = Format.Size(d.TotalFreeSpace) + " free",
                        UsedWidth = Math.Max(2, 174 * used),
                        BarBrush = used > 0.9 ? Theme.Brush("Danger") : Theme.Brush("Accent"),
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            Drives.ItemsSource = cards;
        }

        void Drive_Click(object sender, RoutedEventArgs e) => StartScan((string)((Button)sender).Tag);

        void PickFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Choose a folder to scan", _tree?.FullPath);
            if (folder != null) StartScan(folder);
        }

        // ---------- Scanning --------------------------------------------------------

        async void StartScan(string root)
        {
            _scan?.Cancel();
            var cts = new CancellationTokenSource();
            _scan = cts;
            _stats = new ScanStats();
            var stats = _stats;

            _tree = _current = _selected = null;
            Map.Root = null;
            List.ItemsSource = null;
            Legend.ItemsSource = null;
            Crumbs.ItemsSource = null;
            TotalText.Text = "";
            EmptyState.Visibility = Visibility.Collapsed;
            ScanPanel.Visibility = Visibility.Visible;
            ScanTitle.Text = "Scanning " + root;
            UpdateButtons();
            SetStatus("Scanning…", "Accent");
            _tick.Start();

            try
            {
                var started = DateTime.UtcNow;
                var tree = await Task.Run(() => DiskScanner.Scan(root, stats, cts.Token), cts.Token);
                if (cts != _scan) return;
                _tree = tree;
                ScanPanel.Visibility = Visibility.Collapsed;
                Open(tree);

                var note = "Scanned " + Format.Count(stats.Files, "file") + " in " + Format.Count(stats.Folders, "folder") + " in " + Format.Duration(DateTime.UtcNow - started) + ".";
                if (stats.UnreadableFolders > 0) note += " " + Format.Count(stats.UnreadableFolders, "folder") + " could not be read (normal for system folders without admin rights).";
                if (stats.CloudOnlyBytes > 0) note += " " + Format.Size(stats.CloudOnlyBytes) + " of online-only OneDrive files aren't counted.";
                SetStatus(note, "Success");
            }
            catch (OperationCanceledException)
            {
                if (cts != _scan) return;
                ScanPanel.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = Visibility.Visible;
                SetStatus("Scan stopped.", "TextFaint");
            }
            finally
            {
                if (cts == _scan)
                {
                    _tick.Stop();
                    _scan = null;
                }
            }
        }

        void UpdateScanProgress()
        {
            var s = _stats;
            if (s == null) return;
            ScanDetail.Text = Format.Count(s.Files, "file") + " · " + Format.Size(s.Bytes);
            ScanPath.Text = s.Current;
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _scan?.Cancel();

        // ---------- Navigation ------------------------------------------------------

        void Open(Node folder)
        {
            if (folder == null || !folder.IsDir) return;
            _current = folder;
            _selected = null;
            Map.Root = folder;
            Map.Selected = null;

            double total = Math.Max(1, folder.Size);
            List.ItemsSource = folder.Children.Select(n => new NodeRow
            {
                Node = n,
                ShareWidth = Math.Max(n.Size > 0 ? 1 : 0, 48 * n.Size / total),
                ShareText = Format.Percent(n.Size / total),
                Swatch = SwatchFor(n),
            }).ToList();

            var crumbs = folder.Ancestors().Reverse().ToList();
            Crumbs.ItemsSource = crumbs.Select((n, i) => new Crumb { Name = i == 0 ? n.Name.TrimEnd('\\') : n.Name, Node = n, ShowChevron = i > 0 }).ToList();
            TotalText.Text = Format.Size(folder.Size) + " · " + Format.Count(folder.FileCount, "file");
            UpButton.IsEnabled = folder.Parent != null;

            var byKind = DiskScanner.SizeByKind(folder);
            Legend.ItemsSource = byKind.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => new LegendItem
            {
                Swatch = KindBrush(p.Key),
                Label = FileKinds.Label(p.Key),
                SizeText = Format.Size(p.Value),
            }).ToList();

            EmptyState.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }

        static Brush KindBrush(FileKind kind)
        {
            var b = new SolidColorBrush(TreemapView.KindColors[kind]);
            b.Freeze();
            return b;
        }

        static Brush SwatchFor(Node n) => n.IsDir ? Theme.Brush("BorderStrong") : KindBrush(n.IsAggregate ? FileKind.Other : n.Kind);

        void Up_Click(object sender, RoutedEventArgs e)
        {
            if (_current?.Parent != null)
            {
                var from = _current;
                Open(_current.Parent);
                Select(from);
            }
        }

        void Crumb_Click(object sender, RoutedEventArgs e) => Open((Node)((Button)sender).Tag);

        void Select(Node node)
        {
            _selected = node;
            Map.Selected = node;
            var row = (List.ItemsSource as List<NodeRow>)?.FirstOrDefault(r => r.Node == node);
            if (row != null && List.SelectedItem != row)
            {
                List.SelectedItem = row;
                List.ScrollIntoView(row);
            }
            ShowHover(node);
            UpdateButtons();
        }

        void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (List.SelectedItem is NodeRow row && row.Node != _selected) Select(row.Node);
        }

        void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (List.SelectedItem is NodeRow row && row.Node.IsDir) Open(row.Node);
        }

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && List.SelectedItem is NodeRow row && row.Node.IsDir) { Open(row.Node); e.Handled = true; }
            else if (e.Key == Key.Back) { Up_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Delete) { Recycle_Click(null, null); e.Handled = true; }
        }

        void ShowHover(Node node)
        {
            var n = node ?? _selected;
            if (n == null || _current == null) return;
            string what = n.IsAggregate ? n.Name + " in " + n.Parent.FullPath : n.FullPath;
            string share = _current.Size > 0 ? " · " + Format.Percent((double)n.Size / _current.Size) + " of this folder" : "";
            string extra = n.IsDir ? " · " + Format.Count(n.FileCount, "file") : n.IsAggregate ? "" : " · modified " + Format.Date(n.Modified);
            SetStatus(what + "  —  " + Format.Size(n.Size) + share + extra, n.IsDir ? "BorderStrong" : "Accent");
        }

        void ShowMenu(Node node)
        {
            if (!node.CanAct) return;
            var menu = new ContextMenu();
            if (node.IsDir)
            {
                var open = new MenuItem { Header = "Open this folder in the map" };
                open.Click += (s, e) => Open(node);
                menu.Items.Add(open);
            }
            var reveal = new MenuItem { Header = "Show in Explorer" };
            reveal.Click += (s, e) => Shell.Reveal(node.FullPath);
            menu.Items.Add(reveal);
            menu.Items.Add(new Separator());
            var recycle = new MenuItem { Header = "Move to Recycle Bin…" };
            recycle.Click += (s, e) => Recycle_Click(null, null);
            menu.Items.Add(recycle);
            menu.PlacementTarget = Map;
            menu.IsOpen = true;
        }

        void UpdateButtons()
        {
            bool can = _selected != null && _selected.CanAct && _scan == null;
            RevealButton.IsEnabled = can;
            RecycleButton.IsEnabled = can;
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null && _selected.CanAct) Shell.Reveal(_selected.FullPath);
        }

        void Recycle_Click(object sender, RoutedEventArgs e)
        {
            var node = _selected;
            if (node == null || !node.CanAct) return;
            string path = node.FullPath;
            string what = node.IsDir ? "the folder “" + node.Name + "” and everything in it" : "“" + node.Name + "”";
            if (!Dialog.Confirm(this, "Move to the Recycle Bin?",
                    "This moves " + what + " (" + Format.Size(node.Size) + ") to the Recycle Bin. You can restore it from there.\n\n" + path,
                    "Move to Recycle Bin", danger: true))
                return;

            var failed = RecycleBin.Send(new[] { path }, new WindowInteropHelper(this).Handle);
            if (failed.Count > 0)
            {
                Dialog.Error(this, "Could not move it", "Windows would not move this to the Recycle Bin. It may be open in another app, or you may not have permission.");
                return;
            }
            long freed = node.Size;
            node.Detach();
            _selected = null;
            Open(_current);
            SetStatus("Moved " + node.Name + " to the Recycle Bin (" + Format.Size(freed) + ").", "Success");
            LoadDrives();
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
