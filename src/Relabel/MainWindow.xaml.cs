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
using System.Windows.Threading;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Relabel
{
    public partial class MainWindow : OddWindow
    {
        readonly ObservableCollection<RenameItem> _items = new ObservableCollection<RenameItem>();
        readonly HashSet<string> _known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer _debounce;
        readonly RenameRunner _runner = new RenameRunner(Settings.DataFolder("Relabel"));
        readonly Settings _settings = new Settings("Relabel");
        CancellationTokenSource _dateLoad;
        int _added;
        bool _loadingRules;
        bool _justRenamed;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            List.ItemsSource = _items;
            ColumnFill.Apply(List, 3, 0, 3, 2.2, 1.4);
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Replan(); };

            LoadRules();
#if SCREENSHOT
            if (Environment.GetEnvironmentVariable("ODDJOBS_DEMO") == "1")
            {
                TemplateBox.Text = "Lisbon {taken:yyyy-MM-dd} {n:2}";
                ExtLower.IsChecked = true;
                OrderBox.SelectedIndex = 2;
            }
#endif
            HookRuleEvents();
            DropTarget.Enable(this, paths => AddPaths(paths), EmptyState);

            Loaded += (s, e) =>
            {
                int restored = _runner.RecoverInterrupted();
                if (restored > 0)
                    Dialog.Info(this, "A rename was interrupted", "Relabel was closed in the middle of renaming last time. " +
                        Format.Count(restored, "file was", "files were") + " put back to their original names.");
                if (args.Length > 0) AddPaths(args);
                Refresh();
            };
            Closing += (s, e) => SaveRules();
        }

        // ---------- Rules -------------------------------------------------------------

        void HookRuleEvents()
        {
            foreach (var box in new[] { TemplateBox, FindBox, ReplaceBox, StartBox, StepBox })
                box.TextChanged += (s, e) => RulesChanged();
            foreach (var check in new[] { RegexBox, MatchCaseBox, TrimBox, DescendingBox })
            {
                check.Checked += (s, e) => RulesChanged();
                check.Unchecked += (s, e) => RulesChanged();
            }
            foreach (var radio in new[] { SpaceKeep, SpaceDash, SpaceUnderscore, SpaceRemove, ExtKeep, ExtLower, ExtUpper })
                radio.Checked += (s, e) => RulesChanged();
            CaseBox.SelectionChanged += (s, e) => RulesChanged();
            OrderBox.SelectionChanged += (s, e) => RulesChanged();
        }

        void RulesChanged()
        {
            if (_loadingRules) return;
            _justRenamed = false;
            _debounce.Stop();
            _debounce.Start();
        }

        RenameRules ReadRules()
        {
            int.TryParse(StartBox.Text.Trim(), out int start);
            if (!int.TryParse(StepBox.Text.Trim(), out int step)) step = 1;
            return new RenameRules
            {
                Template = TemplateBox.Text,
                Find = FindBox.Text,
                Replace = ReplaceBox.Text,
                UseRegex = RegexBox.IsChecked == true,
                MatchCase = MatchCaseBox.IsChecked == true,
                Case = (CaseMode)Math.Max(0, CaseBox.SelectedIndex),
                Spaces = SpaceDash.IsChecked == true ? SpaceMode.Dash : SpaceUnderscore.IsChecked == true ? SpaceMode.Underscore
                    : SpaceRemove.IsChecked == true ? SpaceMode.Remove : SpaceMode.Keep,
                TrimSpaces = TrimBox.IsChecked == true,
                Extension = ExtLower.IsChecked == true ? ExtMode.Lower : ExtUpper.IsChecked == true ? ExtMode.Upper : ExtMode.Keep,
                CounterStart = start,
                CounterStep = step,
                Order = (OrderMode)Math.Max(0, OrderBox.SelectedIndex),
                Descending = DescendingBox.IsChecked == true,
            };
        }

        void LoadRules()
        {
            _loadingRules = true;
            TemplateBox.Text = _settings.Get("template", "{name}");
            StartBox.Text = _settings.Get("start", "1");
            StepBox.Text = _settings.Get("step", "1");
            CaseBox.SelectedIndex = Clamp(_settings.GetInt("case", 0), 0, 4);
            OrderBox.SelectedIndex = Clamp(_settings.GetInt("order", 0), 0, 4);
            ExtLower.IsChecked = _settings.Get("ext") == "lower";
            ExtUpper.IsChecked = _settings.Get("ext") == "upper";
            SubfoldersBox.IsChecked = _settings.GetBool("subfolders", false);
            _loadingRules = false;
        }

        void SaveRules()
        {
            _settings.Set("template", TemplateBox.Text);
            _settings.Set("start", StartBox.Text);
            _settings.Set("step", StepBox.Text);
            _settings.Set("case", CaseBox.SelectedIndex);
            _settings.Set("order", OrderBox.SelectedIndex);
            _settings.Set("ext", ExtLower.IsChecked == true ? "lower" : ExtUpper.IsChecked == true ? "upper" : "keep");
            _settings.Set("subfolders", SubfoldersBox.IsChecked == true);
            _settings.Save();
        }

        static int Clamp(int v, int lo, int hi) => Math.Max(lo, Math.Min(hi, v));

        void Reset_Click(object sender, RoutedEventArgs e)
        {
            _loadingRules = true;
            TemplateBox.Text = "{name}";
            FindBox.Text = ReplaceBox.Text = "";
            StartBox.Text = StepBox.Text = "1";
            RegexBox.IsChecked = MatchCaseBox.IsChecked = DescendingBox.IsChecked = false;
            TrimBox.IsChecked = true;
            CaseBox.SelectedIndex = OrderBox.SelectedIndex = 0;
            SpaceKeep.IsChecked = ExtKeep.IsChecked = true;
            _loadingRules = false;
            RulesChanged();
        }

        void Token_Click(object sender, RoutedEventArgs e)
        {
            string token = (string)((Button)sender).Tag;
            int caret = TemplateBox.SelectionStart;
            TemplateBox.SelectedText = token;
            TemplateBox.SelectionLength = 0;
            TemplateBox.SelectionStart = caret + token.Length;
            TemplateBox.Focus();
        }

        // ---------- Files -------------------------------------------------------------

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var files = Dialog.PickFiles(this, "Add files to rename", "All files|*.*");
            if (files != null) AddPaths(files);
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Add every file in a folder", _settings.Get("lastFolder"));
            if (folder == null) return;
            _settings.Set("lastFolder", folder);
            AddPaths(new[] { folder });
        }

        void AddPaths(IEnumerable<string> paths)
        {
            bool recurse = SubfoldersBox.IsChecked == true;
            int before = _items.Count;
            var buffer = new List<DirEntry>();
            foreach (var raw in paths)
            {
                string path;
                try { path = Path.GetFullPath(raw); }
                catch (Exception) { continue; }

                if (Directory.Exists(path))
                {
                    if (recurse)
                    {
                        foreach (var (file, entry) in FastDir.WalkFiles(path, false, null))
                            Add(file, entry);
                    }
                    else if (FastDir.TryList(path, buffer, out _))
                    {
                        foreach (var entry in buffer.Where(x => !x.IsDirectory && !x.IsHidden && !x.IsSystem))
                            Add(Path.Combine(path, entry.Name), entry);
                    }
                }
                else if (FastDir.TryGetEntry(path, out var single) && !single.IsDirectory)
                {
                    Add(path, single);
                }
            }
            if (_items.Count != before)
            {
                _justRenamed = false;
                Replan();
            }
        }

        void Add(string path, DirEntry entry)
        {
            if (!_known.Add(path)) return;
            _items.Add(new RenameItem(path, entry, _added++));
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            _dateLoad?.Cancel();
            _items.Clear();
            _known.Clear();
            _justRenamed = false;
            Refresh();
        }

        void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelected();

        void RemoveSelected()
        {
            foreach (var item in List.SelectedItems.Cast<RenameItem>().ToList())
            {
                _items.Remove(item);
                _known.Remove(item.FullPath);
            }
            Replan();
        }

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { RemoveSelected(); e.Handled = true; }
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (List.SelectedItem is RenameItem item) Shell.Reveal(item.FullPath);
        }

        void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (List.SelectedItem is RenameItem item) Shell.Reveal(item.FullPath);
        }

        // ---------- Planning ----------------------------------------------------------

        string _ruleError;

        void Replan()
        {
            if (_justRenamed) { Refresh(); return; }
            var rules = ReadRules();

            if (rules.UsesTakenDate && _items.Any(i => !i.TakenLoaded))
                LoadTakenDates();

            var ordered = RenamePlanner.Sort(_items, rules).ToList();
            _ruleError = RenamePlanner.Plan(ordered, rules);

            // Show the list in numbering order, so the counter reads top to bottom.
            for (int target = 0; target < ordered.Count; target++)
            {
                int current = _items.IndexOf(ordered[target]);
                if (current != target) _items.Move(current, target);
            }
            Refresh();
        }

        void LoadTakenDates()
        {
            if (_dateLoad != null && !_dateLoad.IsCancellationRequested) return;
            var cts = new CancellationTokenSource();
            _dateLoad = cts;
            var pending = _items.Where(i => !i.TakenLoaded).ToList();
            StatusText.Text = "Reading photo dates…";
            Task.Run(() =>
            {
                var results = new List<(RenameItem, DateTime?)>();
                foreach (var item in pending)
                {
                    if (cts.IsCancellationRequested) return results;
                    results.Add((item, PhotoDate.TryRead(item.FullPath)));
                }
                return results;
            }).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.Result == null) return;
                foreach (var (item, date) in t.Result)
                {
                    item.Taken = date;
                    item.TakenLoaded = true;
                }
                _dateLoad = null;
                Replan();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Refresh()
        {
            bool any = _items.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.IsEnabled = any;
            CountText.Text = any ? Format.Count(_items.Count, "file") : "No files yet";

            int ready = _items.Count(i => i.State == ItemState.Ready);
            int problems = _items.Count(i => i.State == ItemState.Problem);
            int done = _items.Count(i => i.State == ItemState.Done);

            RenameButton.Content = ready > 0 ? "Rename " + Format.Count(ready, "file") : "Rename";
            RenameButton.IsEnabled = ready > 0 && problems == 0 && _ruleError == null && !_justRenamed;

            UndoButton.IsEnabled = _runner.TryReadLast(out var when, out var last);
            UndoButton.ToolTip = UndoButton.IsEnabled ? "Put back the " + Format.Count(last.Count, "file") + " renamed at " + when.ToString("HH:mm, d MMM") : "Nothing to undo";

            if (_ruleError != null) SetStatus(_ruleError, "Danger");
            else if (!any) SetStatus("Add files to see their new names before anything changes.", "TextFaint");
            else if (_justRenamed && done > 0) SetStatus("Renamed " + Format.Count(done, "file") + ". Use Undo last rename to put them back.", "Success");
            else if (problems > 0) SetStatus(Format.Count(problems, "problem") + " to fix before renaming. Change the rules or remove those files from the list.", "Danger");
            else if (ready == 0) SetStatus("Every name stays the same with these rules.", "TextFaint");
            else SetStatus(Format.Count(ready, "file") + " will be renamed" + (_items.Count > ready ? ", " + Format.Count(_items.Count - ready, "file") + " unchanged." : "."), "Accent");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }

        // ---------- Rename / undo -----------------------------------------------------

        void Rename_Click(object sender, RoutedEventArgs e)
        {
            var ready = _items.Where(i => i.State == ItemState.Ready).ToList();
            if (ready.Count == 0) return;
            if (ready.Count >= 20 && !Dialog.Confirm(this, "Rename " + Format.Count(ready.Count, "file") + "?",
                    "The new names are shown in the list. You can undo this afterwards with Undo last rename.", "Rename"))
                return;

            var moves = ready.Select(i => new RenameRunner.Move { From = i.FullPath, To = Path.Combine(i.Folder, i.NewName) }).ToList();
            var outcome = _runner.Run(moves);
            if (outcome.Error != null)
            {
                Dialog.Error(this, "Nothing was renamed",
                    "One file could not be renamed, so every file was put back the way it was. A file may be open in another program.",
                    outcome.Error);
                Replan();
                return;
            }

            for (int i = 0; i < ready.Count; i++)
            {
                _known.Remove(ready[i].FullPath);
                ready[i].SetPath(moves[i].To);
                _known.Add(moves[i].To);
                ready[i].NewName = ready[i].Name;
                ready[i].State = ItemState.Done;
                ready[i].Note = "Renamed";
            }
            foreach (var other in _items.Where(i => i.State != ItemState.Done))
            {
                other.State = ItemState.Same;
                other.Note = "No change";
            }
            _justRenamed = true;
            Refresh();
        }

        void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (!_runner.TryReadLast(out var when, out var last)) return;
            if (!Dialog.Confirm(this, "Undo the last rename?",
                    "Puts " + Format.Count(last.Count, "file") + " back to the names they had before " + when.ToString("HH:mm") + ".", "Undo rename"))
                return;

            var outcome = _runner.UndoLast(out int skipped);
            if (outcome.Error != null)
            {
                Dialog.Error(this, "Could not undo", "Nothing was changed. A file may be open in another program.", outcome.Error);
                return;
            }

            // Keep the list in step with the files on disk.
            var map = last.ToDictionary(m => m.To, m => m.From, StringComparer.OrdinalIgnoreCase);
            foreach (var item in _items)
            {
                if (map.TryGetValue(item.FullPath, out var original) && File.Exists(original))
                {
                    _known.Remove(item.FullPath);
                    item.SetPath(original);
                    _known.Add(original);
                }
            }
            _justRenamed = false;
            Replan();
            string message = "Put back " + Format.Count(outcome.Renamed, "file") + ".";
            if (skipped > 0) message += " " + Format.Count(skipped, "file was", "files were") + " skipped because they were moved or renamed since.";
            SetStatus(message, "Success");
        }
    }
}
