using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Untangle
{
    public partial class MainWindow : OddWindow
    {
        const long MaxFileBytes = 64L * 1024 * 1024;

        readonly ObservableCollection<Step> _steps = new ObservableCollection<Step>();
        readonly DispatcherTimer _debounce;
        CancellationTokenSource _run;
        string _result = "";

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
#endif
            StepList.ItemsSource = _steps;
            _steps.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null) foreach (Step step in e.NewItems) step.PropertyChanged += Step_PropertyChanged;
                if (e.OldItems != null) foreach (Step step in e.OldItems) step.PropertyChanged -= Step_PropertyChanged;
                NoSteps.Visibility = _steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Schedule();
            };
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _debounce.Tick += (s, e) => { _debounce.Stop(); Run(); };

            LoadRecipe(0);
            DropTarget.Enable(Input, paths => LoadFile(paths[0]));
            Loaded += (s, e) =>
            {
                if (args.Length > 0 && File.Exists(args[0])) LoadFile(args[0]);
                Input.Focus();
            };
            Schedule();
        }

        void Step_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(Step.Error)) Schedule();
        }

        void Schedule()
        {
            _debounce.Stop();
            _debounce.Start();
        }

        // ---------- Pipeline ------------------------------------------------------------

        void Run()
        {
            _run?.Cancel();
            var cts = new CancellationTokenSource();
            _run = cts;
            string input = Normalize(Input.Text);
            var steps = _steps.Where(s => s.Enabled).ToList();
            var errors = new Dictionary<Step, string>();

            Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                string text = input;
                foreach (var step in steps)
                {
                    if (cts.IsCancellationRequested) return (null, watch.Elapsed);
                    try { text = step.Apply(text); }
                    catch (RegexMatchTimeoutException) { errors[step] = "This pattern takes too long on this text; it was skipped."; }
                    catch (ArgumentException ex) { errors[step] = "Not a valid pattern: " + ex.Message; }
                }
                return (text, watch.Elapsed);
            }).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested || t.Result.Item1 == null) return;
                foreach (var step in _steps) step.Error = errors.TryGetValue(step, out var err) ? err : null;
                _result = t.Result.Item1;
                Output.Text = _result;
                InputStats.Text = Stats(input);
                OutputStats.Text = Stats(_result) + Difference(input, _result);
                if (input.Length == 0) SetStatus("Paste some text on the left. The clean version appears on the right as you go.", "TextFaint");
                else if (errors.Count > 0) SetStatus(Format.Count(errors.Count, "step") + " could not run. See the step for details.", "Danger");
                else SetStatus(Format.Count(steps.Count, "step") + " applied in " + Math.Max(1, (int)t.Result.Item2.TotalMilliseconds) + " ms. Nothing leaves this window unless you copy or save it.", "Success");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

        static string Stats(string text)
        {
            if (text.Length == 0) return "Empty";
            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            int words = Regex.Matches(text, @"\S+").Count;
            return Format.Count(lines, "line") + " · " + Format.Count(words, "word") + " · " + Format.Count(text.Length, "character");
        }

        static string Difference(string before, string after)
        {
            int a = before.Count(c => c == '\n'), b = after.Count(c => c == '\n');
            if (before.Length == 0 || a == b) return "";
            return "  (" + (b < a ? "−" + Format.Count(a - b, "line") : "+" + Format.Count(b - a, "line")) + ")";
        }

        // ---------- Steps ---------------------------------------------------------------

        void AddStep_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var (name, create) in StepCatalog.All)
            {
                var item = new MenuItem { Header = name };
                var factory = create;
                item.Click += (s, a) => _steps.Add(factory());
                menu.Items.Add(item);
            }
            menu.PlacementTarget = (Button)sender;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void Recipes_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            for (int i = 0; i < StepCatalog.Recipes.Length; i++)
            {
                int index = i;
                var item = new MenuItem { Header = StepCatalog.Recipes[i].Name };
                item.Click += (s, a) => LoadRecipe(index);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
            var clear = new MenuItem { Header = "Start with no steps" };
            clear.Click += (s, a) => _steps.Clear();
            menu.Items.Add(clear);
            menu.PlacementTarget = RecipeButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void LoadRecipe(int index)
        {
            _steps.Clear();
            foreach (var step in StepCatalog.Recipes[index].Create()) _steps.Add(step);
        }

        void StepUp_Click(object sender, RoutedEventArgs e) => MoveStep((Step)((Button)sender).Tag, -1);
        void StepDown_Click(object sender, RoutedEventArgs e) => MoveStep((Step)((Button)sender).Tag, 1);
        void StepRemove_Click(object sender, RoutedEventArgs e) => _steps.Remove((Step)((Button)sender).Tag);

        void MoveStep(Step step, int by)
        {
            int i = _steps.IndexOf(step), j = i + by;
            if (i >= 0 && j >= 0 && j < _steps.Count) _steps.Move(i, j);
        }

        // ---------- Input / output ----------------------------------------------------------

        void Input_TextChanged(object sender, TextChangedEventArgs e) => Schedule();

        void Paste_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText()) Input.Text = Clipboard.GetText();
            }
            catch (COMException) { SetStatus("The clipboard is busy. Try again.", "Warning"); }
        }

        void ClearInput_Click(object sender, RoutedEventArgs e)
        {
            Input.Clear();
            Input.Focus();
        }

        void Open_Click(object sender, RoutedEventArgs e)
        {
            var files = Dialog.PickFiles(this, "Open a text file", "Text files|*.txt;*.csv;*.tsv;*.md;*.log;*.json;*.xml;*.html;*.htm|All files|*.*", false);
            if (files != null) LoadFile(files[0]);
        }

        void LoadFile(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    Dialog.Info(this, "That file is too big", "Untangle works on text up to " + Format.Size(MaxFileBytes) + ".");
                    return;
                }
                Input.Text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialog.Error(this, "Could not open the file", ex.Message);
            }
        }

        void UseAsInput_Click(object sender, RoutedEventArgs e)
        {
            Input.Text = _result;
            LoadRecipe(0);
            _steps.Clear();
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            string text = _result.Replace("\n", "\r\n");
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    SetStatus("Copied " + Format.Count(text.Length, "character") + " to the clipboard.", "Success");
                    return;
                }
                catch (COMException) { Thread.Sleep(60); }
            }
            SetStatus("The clipboard is busy. Try again.", "Warning");
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var path = Dialog.SaveFile(this, "Save the result", "Text file|*.txt|All files|*.*", "cleaned.txt");
            if (path == null) return;
            try
            {
                AtomicFile.WriteAllText(path, _result.Replace("\n", "\r\n"));
                SetStatus("Saved to " + path, "Success");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialog.Error(this, "Could not save", ex.Message);
            }
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
