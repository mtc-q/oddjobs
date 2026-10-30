using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Parity
{
    public partial class MainWindow : OddWindow
    {
        readonly Settings _settings = new Settings("Parity");
        string _left, _right;
        List<DiffItem> _items = new List<DiffItem>();
        CancellationTokenSource _work;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            ColumnFill.Apply(List, 0, 3, 1.6, 1.7, 1.7, 1.3);
            if (args.Length >= 2 && Directory.Exists(args[0]) && Directory.Exists(args[1]))
            {
                SetLeft(args[0]);
                SetRight(args[1]);
                Loaded += (s, e) => Compare_Click(null, null);
            }
            else
            {
                if (Directory.Exists(_settings.Get("left") ?? "")) SetLeft(_settings.Get("left"));
                if (Directory.Exists(_settings.Get("right") ?? "")) SetRight(_settings.Get("right"));
            }
            ContentsBox.IsChecked = _settings.GetBool("contents", false);
            Closing += (s, e) =>
            {
                _work?.Cancel();
                _settings.Set("left", _left);
                _settings.Set("right", _right);
                _settings.Set("contents", ContentsBox.IsChecked == true);
                _settings.Save();
            };
            Refresh();
        }

        // ---------- Folders -------------------------------------------------------------

        void PickLeft_Click(object sender, RoutedEventArgs e)
        {
            var f = Dialog.PickFolder(this, "Choose the left folder (usually the original)", _left);
            if (f != null) { SetLeft(f); Reset(); }
        }

        void PickRight_Click(object sender, RoutedEventArgs e)
        {
            var f = Dialog.PickFolder(this, "Choose the right folder (usually the backup)", _right);
            if (f != null) { SetRight(f); Reset(); }
        }

        void Swap_Click(object sender, RoutedEventArgs e)
        {
            var l = _left;
            SetLeft(_right);
            SetRight(l);
            Reset();
        }

        void SetLeft(string path)
        {
            _left = path == null ? null : PathUtil.Normalize(path);
            LeftName.Text = _left == null ? "Choose a folder" : Path.GetFileName(_left) is string n && n.Length > 0 ? n : _left;
            LeftPath.Text = _left ?? "";
        }

        void SetRight(string path)
        {
            _right = path == null ? null : PathUtil.Normalize(path);
            RightName.Text = _right == null ? "Choose a folder" : Path.GetFileName(_right) is string n && n.Length > 0 ? n : _right;
            RightPath.Text = _right ?? "";
        }

        void Reset()
        {
            _items = new List<DiffItem>();
            Refresh();
        }

        // ---------- Compare -------------------------------------------------------------

        async void Compare_Click(object sender, RoutedEventArgs e)
        {
            if (_work != null) return;
            if (_left == null || _right == null)
            {
                SetStatus("Choose both folders first.", "Warning");
                return;
            }
            var options = new CompareOptions { CheckContents = ContentsBox.IsChecked == true };
            var cts = new CancellationTokenSource();
            _work = cts;
            ShowProgress("Comparing", "Reading folders…");
            var progress = new Progress<string>(s => ProgressDetail.Text = s);
            try
            {
                string left = _left, right = _right;
                _items = await Task.Run(() => FolderComparer.Compare(left, right, options, progress, cts.Token));
            }
            catch (OperationCanceledException) { _items = new List<DiffItem>(); }
            catch (InvalidOperationException ex)
            {
                _items = new List<DiffItem>();
                Dialog.Info(this, "Pick two separate folders", ex.Message);
            }
            catch (IOException ex)
            {
                _items = new List<DiffItem>();
                Dialog.Error(this, "Could not read a folder", ex.Message);
            }
            finally
            {
                _work = null;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
            Replan();
        }

        SyncMode Mode => (SyncMode)Math.Max(0, ModeBox.SelectedIndex);

        void Mode_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (IsInitialized) Replan();
        }

        void Replan()
        {
            foreach (var item in _items)
            {
                var op = _left == null ? null : Syncer.Plan(item, Mode, _left, _right);
                item.Actionable = op != null;
                item.Selected = op != null;
                item.Plan = op == null ? "" : op.Remove ? "Move to Recycle Bin" : op.To.StartsWith(_right, StringComparison.OrdinalIgnoreCase)
                    ? (op.Overwrite ? "Replace on right" : "Copy to right")
                    : (op.Overwrite ? "Replace on left" : "Copy to left");
            }
            ApplyFilter();
            Refresh();
        }

        void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

        void ApplyFilter()
        {
            bool showLeft = FilterLeft.IsChecked == true, showChanged = FilterChanged.IsChecked == true,
                 showRight = FilterRight.IsChecked == true, showSame = FilterSame.IsChecked == true;
            List.ItemsSource = _items.Where(i =>
                i.Kind == DiffKind.OnlyLeft ? showLeft :
                i.Kind == DiffKind.OnlyRight ? showRight :
                i.Kind == DiffKind.Same ? showSame : showChanged).ToList();
        }

        void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(List.SelectedItem is DiffItem item)) return;
            Shell.Reveal(Path.Combine(item.Left.HasValue ? _left : _right, item.Relative));
        }

        // ---------- Sync ----------------------------------------------------------------

        async void Sync_Click(object sender, RoutedEventArgs e)
        {
            if (_work != null) return;
            var ops = _items.Where(i => i.Selected && i.Actionable).Select(i => Syncer.Plan(i, Mode, _left, _right)).Where(o => o != null).ToList();
            if (ops.Count == 0) return;

            int copies = ops.Count(o => !o.Remove), replaces = ops.Count(o => o.Overwrite), removals = ops.Count(o => o.Remove);
            long bytes = ops.Sum(o => o.Bytes);
            string body = "Copies " + Format.Count(copies, "file") + " (" + Format.Size(bytes) + ")";
            if (replaces > 0) body += ", replacing " + Format.Count(replaces, "older copy", "older copies");
            if (removals > 0) body += ", and moves " + Format.Count(removals, "file") + " that only exist on the right to the Recycle Bin";
            body += ".";
            if (ops.Any(o => o.RecycleExisting)) body += " Newer files that get replaced go to the Recycle Bin first, so nothing is lost.";
            if (!Dialog.Confirm(this, "Ready to sync?", body, "Sync now", danger: removals > 0 || ops.Any(o => o.RecycleExisting)))
                return;

            var cts = new CancellationTokenSource();
            _work = cts;
            ShowProgress("Copying", "Starting…");
            Progress.IsIndeterminate = false;
            Progress.Maximum = Math.Max(1, bytes);
            Progress.Value = 0;
            var progress = new Progress<(int Done, long Bytes)>(p =>
            {
                Progress.Value = p.Bytes;
                ProgressDetail.Text = Format.Count(p.Done, "file") + " of " + ops.Count + " · " + Format.Size(p.Bytes) + " of " + Format.Size(bytes);
            });

            (int Done, List<string> Errors, List<string> ToRecycle) result;
            try
            {
                result = await Task.Run(() => Syncer.Run(ops, progress, cts.Token));
            }
            finally
            {
                _work = null;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }

            int notRecycled = 0;
            if (result.ToRecycle.Count > 0) notRecycled = RecycleBin.Send(result.ToRecycle, new WindowInteropHelper(this).Handle).Count;

            if (result.Errors.Count > 0)
                Dialog.Error(this, Format.Count(result.Errors.Count, "file was", "files were") + " not synced", "They may be open in another app, or the drive may be full or read-only. Everything else was synced.",
                    string.Join("\n", result.Errors.Take(30)));

            Compare_Click(null, null);
            SetStatus("Synced " + Format.Count(result.Done, "file") + "." + (cts.IsCancellationRequested ? " Stopped early." : "") +
                      (notRecycled > 0 ? " " + notRecycled + " could not be moved to the Recycle Bin." : ""),
                result.Errors.Count > 0 ? "Warning" : "Success");
        }

        void Stop_Click(object sender, RoutedEventArgs e) => _work?.Cancel();

        void ShowProgress(string title, string detail)
        {
            ProgressTitle.Text = title;
            ProgressDetail.Text = detail;
            Progress.IsIndeterminate = true;
            ProgressPanel.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
            List.Visibility = Visibility.Collapsed;
            Filters.Visibility = Visibility.Collapsed;
            CompareButton.IsEnabled = SyncButton.IsEnabled = false;
        }

        void Refresh()
        {
            bool compared = _items.Count > 0;
            List.Visibility = compared ? Visibility.Visible : Visibility.Collapsed;
            Filters.Visibility = compared ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = compared ? Visibility.Collapsed : Visibility.Visible;
            CompareButton.IsEnabled = _left != null && _right != null && _work == null;

            int onlyLeft = _items.Count(i => i.Kind == DiffKind.OnlyLeft);
            int onlyRight = _items.Count(i => i.Kind == DiffKind.OnlyRight);
            int same = _items.Count(i => i.Kind == DiffKind.Same);
            int changed = _items.Count - onlyLeft - onlyRight - same;
            FilterLeft.Content = "Only on the left  " + onlyLeft;
            FilterChanged.Content = "Changed  " + changed;
            FilterRight.Content = "Only on the right  " + onlyRight;
            FilterSame.Content = "Identical  " + same;

            var selected = _items.Where(i => i.Selected && i.Actionable).ToList();
            SyncButton.IsEnabled = selected.Count > 0 && _work == null;
            SyncButton.Content = selected.Count > 0 ? "Sync " + Format.Count(selected.Count, "file") : "Sync";

            if (_work != null) return;
            if (_left == null || _right == null) SetStatus("Choose two folders to compare. Nothing is copied until you press Sync.", "TextFaint");
            else if (!compared) SetStatus("Press Compare to see what's different.", "TextFaint");
            else if (selected.Count == 0 && onlyLeft + onlyRight + changed == 0) SetStatus("The two folders match: " + Format.Count(same, "file") + ", all identical.", "Success");
            else SetStatus(Format.Count(onlyLeft + onlyRight + changed, "difference") + ". " + (selected.Count > 0 ? Format.Count(selected.Count, "file") + " will be synced with this option." : "Nothing to do with this option."), "Accent");
            foreach (var item in _items) item.PropertyChanged -= Item_Changed;
            foreach (var item in _items) item.PropertyChanged += Item_Changed;
        }

        void Item_Changed(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DiffItem.Selected)) return;
            var selected = _items.Count(i => i.Selected && i.Actionable);
            SyncButton.IsEnabled = selected > 0 && _work == null;
            SyncButton.Content = selected > 0 ? "Sync " + Format.Count(selected, "file") : "Sync";
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
