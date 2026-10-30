using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Tidy
{
    public abstract class Row
    {
        public abstract bool IsHeader { get; }
        public string Name { get; set; }
        public string Detail { get; set; }
    }

    public sealed class GroupRow : Row
    {
        public override bool IsHeader => true;
    }

    public sealed class FileRow : Row
    {
        public override bool IsHeader => false;
        public PlannedMove Move { get; set; }
    }

    public partial class MainWindow : OddWindow
    {
        readonly Settings _settings = new Settings("Tidy");
        readonly TidyRunner _runner = new TidyRunner(Settings.DataFolder("Tidy"));
        string _folder;
        List<PlannedMove> _plan = new List<PlannedMove>();
        bool _ready;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            string start = args.FirstOrDefault(Directory.Exists) ?? _settings.Get("folder");
            if (start == null || !Directory.Exists(start)) start = Shell.DownloadsFolder;
            RecentBox.SelectedIndex = Math.Max(0, Math.Min(4, _settings.GetInt("recent", 1)));
            ByTypeMonth.IsChecked = _settings.Get("grouping") == "typeMonth";
            ByMonth.IsChecked = _settings.Get("grouping") == "month";
#if SCREENSHOT
            if (Environment.GetEnvironmentVariable("ODDJOBS_DEMO") == "1") RecentBox.SelectedIndex = 0;
#endif

            foreach (var r in new[] { ByType, ByTypeMonth, ByMonth }) r.Checked += (s, e) => Replan();
            RecentBox.SelectionChanged += (s, e) => Replan();
            foreach (CheckBox c in KindPanel.Children)
            {
                c.Checked += (s, e) => Replan();
                c.Unchecked += (s, e) => Replan();
            }
            DropTarget.Enable(this, paths =>
            {
                var folder = paths.FirstOrDefault(Directory.Exists);
                if (folder != null) SetFolder(folder);
            });

            Closing += (s, e) =>
            {
                _settings.Set("folder", _folder);
                _settings.Set("recent", RecentBox.SelectedIndex);
                _settings.Set("grouping", ByTypeMonth.IsChecked == true ? "typeMonth" : ByMonth.IsChecked == true ? "month" : "type");
                _settings.Save();
            };

            _ready = true;
            SetFolder(start);
        }

        void ChangeFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Choose a folder to tidy", _folder);
            if (folder != null) SetFolder(folder);
        }

        void SetFolder(string folder)
        {
            _folder = PathUtil.Normalize(folder);
            FolderName.Text = Path.GetFileName(_folder) is string n && n.Length > 0 ? n : _folder;
            FolderPath.Text = _folder;
            FolderPath.ToolTip = _folder;
            Replan();
        }

        TidyOptions ReadOptions()
        {
            var options = new TidyOptions
            {
                Grouping = ByTypeMonth.IsChecked == true ? Grouping.ByTypeAndMonth : ByMonth.IsChecked == true ? Grouping.ByMonth : Grouping.ByType,
                SkipRecentDays = int.Parse((string)((ComboBoxItem)RecentBox.SelectedItem).Tag),
                Kinds = new HashSet<FileKind>(),
            };
            foreach (CheckBox c in KindPanel.Children)
                if (c.IsChecked == true) options.Kinds.Add((FileKind)Enum.Parse(typeof(FileKind), (string)c.Tag));
            return options;
        }

        void Replan()
        {
            if (!_ready || _folder == null) return;
            int recent = 0, other = 0;
            try
            {
                _plan = TidyPlanner.Plan(_folder, ReadOptions(), DateTime.Now, out recent, out other);
            }
            catch (IOException ex)
            {
                _plan = new List<PlannedMove>();
                SetStatus(ex.Message, "Danger");
            }

            var rows = new List<Row>();
            foreach (var group in _plan.GroupBy(m => m.Group).OrderBy(g => g.Key, NaturalComparer.Instance))
            {
                rows.Add(new GroupRow
                {
                    Name = group.Key,
                    Detail = Format.Count(group.Count(), "file") + " · " + Format.Size(group.Sum(m => m.Size)),
                });
                rows.AddRange(group.Select(m => new FileRow
                {
                    Name = Path.GetFileName(m.From) + (Path.GetFileName(m.From) != Path.GetFileName(m.To) ? "  →  " + Path.GetFileName(m.To) : ""),
                    Detail = Format.Size(m.Size) + "  ·  " + m.Modified.ToString("d MMM yyyy"),
                    Move = m,
                }));
            }
            Preview.ItemsSource = rows;

            bool any = _plan.Count > 0;
            Preview.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            int groups = _plan.Select(m => m.Group).Distinct().Count();
            SummaryText.Text = any ? Format.Count(_plan.Count, "file") + " will move into " + Format.Count(groups, "folder") : "Nothing to tidy";

            var notes = new List<string>();
            if (any) notes.Add(Format.Size(_plan.Sum(m => m.Size)) + " in total, nothing is copied or deleted");
            if (recent > 0) notes.Add(Format.Count(recent, "recent file") + " left alone");
            if (other > 0) notes.Add(Format.Number(other) + " skipped");
            SummaryDetail.Text = string.Join(" · ", notes);
            EmptyBody.Text = recent > 0 ? "Only recent files are here, and they are being left alone. Change “Leave recent files” to include them."
                : "There are no loose files here that match these settings.";

            TidyButton.IsEnabled = any;
            TidyButton.Content = any ? "Tidy " + Format.Count(_plan.Count, "file") : "Tidy";
            UpdateUndo();
            if (any) SetStatus("This is a preview. Nothing moves until you press Tidy.", "Accent");
            else SetStatus("All tidy here.", "Success");
        }

        void Preview_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Preview.SelectedItem is FileRow row) Shell.Reveal(row.Move.From);
        }

        void Tidy_Click(object sender, RoutedEventArgs e)
        {
            if (_plan.Count == 0) return;
            var (moved, errors) = _runner.Run(_folder, _plan);
            Replan();
            if (errors.Count == 0)
                SetStatus("Moved " + Format.Count(moved, "file") + " into folders. Changed your mind? Undo last tidy puts them all back.", "Success");
            else
            {
                SetStatus("Moved " + Format.Count(moved, "file") + "; " + Format.Count(errors.Count, "file") + " could not be moved.", "Warning");
                Dialog.Error(this, "Some files were not moved", "They may be open in another app. Everything else was moved, and can be undone.",
                    string.Join("\n", errors.Take(20)));
            }
        }

        void UpdateUndo()
        {
            var last = _runner.History().FirstOrDefault();
            UndoButton.IsEnabled = last != null;
            UndoButton.ToolTip = last == null ? "Nothing to undo"
                : "Put back " + Format.Count(last.Moves.Count, "file") + " tidied in " + Path.GetFileName(last.Folder) + " at " + last.When.ToString("HH:mm, d MMM");
        }

        void Undo_Click(object sender, RoutedEventArgs e)
        {
            var last = _runner.History().FirstOrDefault();
            if (last == null) return;
            if (!Dialog.Confirm(this, "Undo the last tidy?",
                    "Moves " + Format.Count(last.Moves.Count, "file") + " back to " + last.Folder + ", where they were before " + last.When.ToString("HH:mm on d MMM") +
                    ". Folders Tidy created are removed if they end up empty.", "Undo tidy"))
                return;
            var (restored, skipped) = _runner.Undo(last);
            Replan();
            SetStatus("Put back " + Format.Count(restored, "file") + "." + (skipped > 0 ? " " + Format.Count(skipped, "file was", "files were") + " skipped because they were moved, renamed or replaced since." : ""),
                skipped > 0 ? "Warning" : "Success");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
