using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Lockbox
{
    public sealed class LockItem : INotifyPropertyChanged
    {
        public string Path { get; set; }
        public bool IsFolder { get; set; }
        public bool IsLocked { get; set; }
        public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\'));
        public string Glyph => IsLocked ? Icons.Lock : IsFolder ? Icons.Folder : Icons.Document;

        string _detail;
        public string Detail { get => _detail; set { _detail = value; Raise(nameof(Detail)); } }

        string _state = "";
        public string StateText { get => _state; set { _state = value; Raise(nameof(StateText)); } }

        double _progress;
        public double Progress { get => _progress; set { _progress = value; Raise(nameof(Progress)); } }

        bool _busy, _good, _bad;
        public bool Busy { get => _busy; set { _busy = value; Raise(nameof(Busy)); } }
        public bool Good { get => _good; set { _good = value; Raise(nameof(Good)); } }
        public bool Bad { get => _bad; set { _bad = value; Raise(nameof(Bad)); } }
        public bool Finished => Good;

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public partial class MainWindow : OddWindow
    {
        readonly ObservableCollection<LockItem> _items = new ObservableCollection<LockItem>();
        readonly Settings _settings = new Settings("Lockbox");
        CancellationTokenSource _work;

        public MainWindow(string[] args)
        {
            InitializeComponent();
#if SCREENSHOT
            ApplyShotSize(this);
            if (Environment.GetEnvironmentVariable("ODDJOBS_DEMO") == "1")
                Loaded += (s, e) => { Password.Password = "correct horse battery staple 42"; Confirm.Password = Password.Password; };
#endif
            List.ItemsSource = _items;
            DropTarget.Enable(this, AddPaths, EmptyState);
            DestinationBox.SelectedIndex = Math.Max(0, Math.Min(1, _settings.GetInt("destination", 0)));
            Closing += (s, e) =>
            {
                _work?.Cancel();
                _settings.Set("destination", DestinationBox.SelectedIndex);
                _settings.Save();
                Password.Clear();
                Confirm.Clear();
                PasswordPlain.Clear();
            };
            Loaded += (s, e) => { if (args.Length > 0) AddPaths(args); };
            Refresh();
        }

        bool UnlockMode => _items.Count > 0 && _items.All(i => i.IsLocked);
        bool Mixed => _items.Any(i => i.IsLocked) && _items.Any(i => !i.IsLocked);

        // ---------- Items ---------------------------------------------------------------

        void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var files = Dialog.PickFiles(this, "Add files to lock or unlock", "All files|*.*|Lockbox files|*.lockbox");
            if (files != null) AddPaths(files);
        }

        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = Dialog.PickFolder(this, "Add a folder to lock");
            if (folder != null) AddPaths(new[] { folder });
        }

        void AddPaths(string[] paths)
        {
            if (_work != null) return;
            foreach (var raw in paths)
            {
                string path;
                try { path = System.IO.Path.GetFullPath(raw); } catch (Exception) { continue; }
                if (_items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
                if (Directory.Exists(path))
                {
                    var files = FastDir.WalkFiles(path, true, null).ToList();
                    _items.Add(new LockItem { Path = path, IsFolder = true, Detail = "Folder · " + Format.Count(files.Count, "file") + " · " + Format.Size(files.Sum(f => f.Entry.Size)) });
                }
                else if (File.Exists(path))
                {
                    bool locked = LockboxFormat.IsLockbox(path);
                    _items.Add(new LockItem { Path = path, IsLocked = locked, Detail = (locked ? "Locked · " : "") + Format.Size(new FileInfo(path).Length) });
                }
            }
            Refresh();
        }

        void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (_work != null) { _work.Cancel(); return; }
            _items.Clear();
            Refresh();
        }

        void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && _work == null && List.SelectedItem is LockItem item)
            {
                _items.Remove(item);
                Refresh();
            }
        }

        // ---------- Password ------------------------------------------------------------

        SecureString CurrentPassword()
        {
            if (PasswordPlain.Visibility != Visibility.Visible) return Password.SecurePassword;
            var s = new SecureString();
            foreach (char c in PasswordPlain.Text) s.AppendChar(c);
            s.MakeReadOnly();
            return s;
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (PasswordPlain.Visibility == Visibility.Visible)
            {
                Password.Password = PasswordPlain.Text;
                PasswordPlain.Clear();
                PasswordPlain.Visibility = Visibility.Collapsed;
                Password.Visibility = Visibility.Visible;
                RevealButton.Content = "Show";
            }
            else
            {
                PasswordPlain.Text = Password.Password;
                PasswordPlain.Visibility = Visibility.Visible;
                Password.Visibility = Visibility.Collapsed;
                RevealButton.Content = "Hide";
            }
        }

        void Generate_Click(object sender, RoutedEventArgs e)
        {
            string generated = Secret.Generate();
            if (PasswordPlain.Visibility != Visibility.Visible) Reveal_Click(null, null);
            PasswordPlain.Text = generated;
            Confirm.Password = generated;
            SetStatus("Suggested a random password. Save it in your password manager before you lock anything.", "Warning");
        }

        void Password_Changed(object sender, RoutedEventArgs e) => Refresh();
        void PasswordPlain_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

        // ---------- Go ------------------------------------------------------------------

        async void Go_Click(object sender, RoutedEventArgs e)
        {
            if (_work != null) return;
            var todo = _items.Where(i => !i.Finished).ToList();
            if (todo.Count == 0) return;
            bool unlocking = UnlockMode;

            string chosen = null;
            if (DestinationBox.SelectedIndex == 1)
            {
                chosen = Dialog.PickFolder(this, unlocking ? "Unlock into" : "Save locked files to", _settings.Get("lastFolder"));
                if (chosen == null) return;
                _settings.Set("lastFolder", chosen);
            }

            byte[] password;
            using (var secure = CurrentPassword()) password = Secret.ToUtf8(secure);
            bool recycle = !unlocking && RecycleBox.IsChecked == true;
            var cts = new CancellationTokenSource();
            _work = cts;
            Refresh();
            SetStatus(unlocking ? "Checking the password…" : "Locking…", "Accent");

            int done = 0, failed = 0, wrong = 0;
            var originals = new List<string>();
            try
            {
                foreach (var item in todo)
                {
                    if (cts.IsCancellationRequested) break;
                    item.Busy = true;
                    item.Bad = false;
                    item.StateText = unlocking ? "Unlocking…" : "Locking…";
                    var progress = new Progress<double>(p => item.Progress = p);
                    IProgress<double> report = progress;
                    string folder = chosen ?? System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\'));
                    try
                    {
                        string result = await Task.Run(() =>
                        {
                            if (unlocking) return LockboxFormat.Unlock(item.Path, folder, password, p => report.Report(p), cts.Token);
                            string output = PathUtil.UniquePath(folder, item.Name + LockboxFormat.Extension);
                            LockboxFormat.Lock(item.Path, output, password, LockboxFormat.DefaultIterations, p => report.Report(p), cts.Token);
                            return output;
                        });
                        done++;
                        item.Good = true;
                        item.StateText = unlocking ? "Unlocked" : "Locked";
                        item.Detail = (unlocking ? "Saved as " : "Saved as ") + System.IO.Path.GetFileName(result.TrimEnd('\\'));
                        if (recycle) originals.Add(item.Path);
                    }
                    catch (WrongPasswordException)
                    {
                        wrong++;
                        item.Bad = true;
                        item.StateText = "Wrong password";
                        item.Detail = "Wrong password, or the file was changed or damaged.";
                    }
                    catch (OperationCanceledException)
                    {
                        item.StateText = "Stopped";
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is System.Security.Cryptography.CryptographicException)
                    {
                        failed++;
                        item.Bad = true;
                        item.StateText = "Failed";
                        item.Detail = ex.Message;
                    }
                    finally
                    {
                        item.Busy = false;
                    }
                }
            }
            finally
            {
                LockboxFormat.Wipe(password);
                _work = null;
            }

            int notRecycled = 0;
            if (originals.Count > 0) notRecycled = RecycleBin.Send(originals, new WindowInteropHelper(this).Handle).Count;

            if (done > 0 && wrong == 0 && failed == 0)
            {
                Password.Clear();
                Confirm.Clear();
                PasswordPlain.Clear();
            }
            Refresh();
            string verb = unlocking ? "Unlocked" : "Locked";
            if (wrong > 0 && done == 0)
                SetStatus("Nothing was unlocked: wrong password, or the file was changed. Nothing was written to disk.", "Danger");
            else if (wrong > 0)
                SetStatus(verb + " " + done + ". " + Format.Count(wrong, "file") + " did not open: wrong password, or the file was changed.", "Danger");
            else if (failed > 0)
                SetStatus(verb + " " + done + ". " + Format.Count(failed, "item") + " failed; see the list.", "Danger");
            else
                SetStatus(verb + " " + Format.Count(done, "item") + (unlocking ? "." : ", and each one was checked to open again with this password.") +
                    (originals.Count > 0 ? " Originals moved to the Recycle Bin" + (notRecycled > 0 ? " (" + notRecycled + " could not be)." : ".") : ""), "Success");
        }

        void Refresh()
        {
            bool any = _items.Count > 0;
            bool busy = _work != null;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            List.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.Content = busy ? "Stop" : "Clear list";
            ClearButton.IsEnabled = any;

            bool unlocking = UnlockMode;
            int pending = _items.Count(i => !i.Finished);
            ModeTitle.Text = !any ? "Lock or unlock" : unlocking ? "Unlock " + Format.Count(pending, "file") : "Lock " + Format.Count(pending, "item");
            ModeHint.Text = !any ? "Add files on the left. Lockbox works out whether to lock or unlock them."
                : unlocking ? "Enter the password the files were locked with." : "Choose a password. You will need it to open these again.";
            CountText.Text = any ? Format.Count(_items.Count, "item") : "Nothing added yet";
            CountDetail.Text = Mixed ? "Lock and unlock separately: this list has both locked and normal files." : "";
            CountDetail.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Danger");

            ConfirmPanel.Visibility = unlocking ? Visibility.Collapsed : Visibility.Visible;
            StrengthPanel.Visibility = unlocking ? Visibility.Collapsed : Visibility.Visible;
            RecycleBox.Visibility = unlocking ? Visibility.Collapsed : Visibility.Visible;

            bool ready;
            using (var pw = CurrentPassword())
            {
                if (unlocking) ready = pw.Length > 0;
                else
                {
                    double bits = Secret.EstimateBits(pw);
                    int level = pw.Length == 0 ? 0 : bits < 40 ? 1 : bits < 60 ? 2 : bits < 80 ? 3 : 4;
                    var bars = new[] { Bar1, Bar2, Bar3, Bar4 };
                    string color = level <= 1 ? "Danger" : level == 2 ? "Warning" : "Success";
                    for (int i = 0; i < bars.Length; i++) bars[i].SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, i < level ? color : "Surface3");
                    StrengthText.Text = pw.Length == 0 ? "At least 10 characters"
                        : pw.Length < 10 ? "Too short: use at least 10 characters"
                        : level <= 1 ? "Weak: easy to guess" : level == 2 ? "Fair: longer is better" : level == 3 ? "Strong" : "Very strong";

                    bool matches;
                    using (var confirm = Confirm.SecurePassword) matches = Secret.Equal(pw, confirm);
                    ConfirmText.Text = Confirm.SecurePassword.Length > 0 && !matches ? "The two passwords are different" : "";
                    ready = pw.Length >= 10 && matches;
                }
            }

            GoButton.IsEnabled = any && pending > 0 && ready && !busy && !Mixed;
            GoButton.Content = busy ? "Working…" : unlocking ? "Unlock" : "Lock";

            if (!busy && !any) SetStatus("Everything happens on this PC. Lockbox never sees the internet.", "TextFaint");
        }

        void SetStatus(string text, string brush)
        {
            StatusText.Text = text;
            StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        }
    }
}
