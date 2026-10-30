using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using OddJobs.IO;

namespace OddJobs.Twins
{
    public abstract class Row : INotifyPropertyChanged
    {
        public abstract bool IsHeader { get; }
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class GroupRow : Row
    {
        public GroupRow(DupGroup group)
        {
            Group = group;
            Files = group.Files.Select(f => new FileRow(f, this)).ToList();
        }

        public DupGroup Group { get; }
        public List<FileRow> Files { get; }
        public override bool IsHeader => true;

        public string Title => Files.Count + " identical copies · " + Format.Size(Group.Size) + " each";
        public string Detail
        {
            get
            {
                int marked = Files.Count(f => f.Marked);
                return marked == 0
                    ? Format.Size(Group.Size * (Files.Count - 1)) + " to free"
                    : "Removing " + marked + " · frees " + Format.Size(Group.Size * marked);
            }
        }

        internal void Refresh() => Raise(nameof(Detail));
    }

    public sealed class FileRow : Row
    {
        /// <summary>Raised when someone tries to mark the last remaining copy of a file.</summary>
        public static event Action KeepOneRefused;
        public static event Action MarksChanged;

        public FileRow(DupFile file, GroupRow group)
        {
            File = file;
            Group = group;
        }

        public DupFile File { get; }
        public GroupRow Group { get; }
        public override bool IsHeader => false;

        /// <summary>The folders that were searched, so paths can be shown relative to them.</summary>
        public static List<string> Roots = new List<string>();

        public string Name => Path.GetFileName(File.Path);
        public string Folder => Path.GetDirectoryName(File.Path);

        /// <summary>"Downloads\old" rather than "C:\Users\someone\Downloads\old".</summary>
        public string FolderShort
        {
            get
            {
                string folder = Folder;
                var root = Roots.Where(r => PathUtil.IsInside(folder, r)).OrderByDescending(r => r.Length).FirstOrDefault();
                if (root == null) return folder;
                string rootName = Path.GetFileName(root.TrimEnd('\\'));
                if (string.IsNullOrEmpty(rootName)) rootName = root;
                string rest = PathUtil.Relative(root, folder);
                return string.Equals(rest, folder, StringComparison.OrdinalIgnoreCase) || rest.Length == 0 ? rootName : rootName + "\\" + rest;
            }
        }
        public string ModifiedText => File.Modified.ToString("d MMM yyyy");

        bool _marked;
        public bool Marked
        {
            get => _marked;
            set
            {
                if (value == _marked) return;
                if (value && Group.Files.All(f => f == this || f._marked))
                {
                    // Never let every copy be removed. Re-read the old value once the binding has finished.
                    KeepOneRefused?.Invoke();
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => Raise(nameof(Marked))));
                    return;
                }
                _marked = value;
                Raise(nameof(Marked));
                Group.Refresh();
                MarksChanged?.Invoke();
            }
        }

        /// <summary>Sets the mark without the keep-one check; callers guarantee one copy stays.</summary>
        internal void SetMarked(bool value)
        {
            if (_marked == value) return;
            _marked = value;
            Raise(nameof(Marked));
        }
    }
}
