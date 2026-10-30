using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace OddJobs.UI
{
    /// <summary>Themed message dialogs and the native Windows folder picker.</summary>
    public static class Dialog
    {
        public static bool Confirm(Window owner, string title, string message, string confirmText, bool danger = false, string details = null)
            => Show(owner, title, message, details, confirmText, "Cancel", danger) == true;

        public static void Info(Window owner, string title, string message, string details = null)
            => Show(owner, title, message, details, "OK", null, false);

        public static void Error(Window owner, string title, string message, string details = null)
            => Show(owner, title, message, details, "OK", null, false, isError: true);

        public static void About(Window owner)
        {
            var app = OddApp.Info;
            Show(owner, app.Name + " " + Brand.Version,
                app.Tagline + ".\n\n" +
                "Part of " + Brand.Suite + ", a set of small, free tools for Windows.\n\n" +
                "Everything happens on this PC. " + app.Name + " has no network code, no accounts, no telemetry and no ads, " +
                "and never asks for administrator rights.\n\n" +
                "Free and open source under the MIT License.",
                null, "Close", null, false);
        }

        static bool? Show(Window owner, string title, string message, string details, string primary, string secondary, bool danger, bool isError = false)
        {
            var window = new OddWindow
            {
                Title = OddApp.Info?.Name ?? title,
                Width = 460,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = owner != null && owner.IsVisible ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            };
            if (owner != null && owner.IsVisible) window.Owner = owner;

            var root = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            if (isError) heading.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            root.Children.Add(heading);

            var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), LineHeight = 19 };
            body.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            root.Children.Add(body);

            if (!string.IsNullOrEmpty(details))
            {
                var box = new TextBox
                {
                    Text = details,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 180,
                    Margin = new Thickness(0, 12, 0, 0),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    FontSize = 12,
                };
                box.SetResourceReference(Control.FontFamilyProperty, "MonoFont");
                root.Children.Add(box);
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            bool? result = null;
            if (secondary != null)
            {
                var cancel = new Button { Content = secondary, IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0) };
                cancel.Click += (s, e) => { result = false; window.Close(); };
                buttons.Children.Add(cancel);
            }
            var ok = new Button { Content = primary, IsDefault = true, MinWidth = 88 };
            ok.SetResourceReference(FrameworkElement.StyleProperty, danger ? "DangerButton" : "PrimaryButton");
            if (secondary == null) ok.IsCancel = true;
            ok.Click += (s, e) => { result = true; window.Close(); };
            buttons.Children.Add(ok);
            root.Children.Add(buttons);

            window.Content = root;
            window.Loaded += (s, e) => ok.Focus();
            window.ShowDialog();
            return result;
        }

        // --- Native folder picker (IFileOpenDialog) --------------------------------------

        public static string PickFolder(Window owner, string title = "Choose a folder", string startIn = null)
        {
            IFileDialog dialog = null;
            try
            {
                dialog = (IFileDialog)new FileOpenDialogRcw();
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);
                dialog.SetTitle(title);
                if (!string.IsNullOrEmpty(startIn) && System.IO.Directory.Exists(startIn))
                {
                    var iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(startIn, IntPtr.Zero, ref iid, out IShellItem item) == 0)
                        dialog.SetFolder(item);
                }
                IntPtr hwnd = owner != null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
                if (dialog.Show(hwnd) != 0) return null;
                dialog.GetResult(out IShellItem result);
                result.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr namePtr);
                try { return Marshal.PtrToStringUni(namePtr); }
                finally { Marshal.FreeCoTaskMem(namePtr); }
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                if (dialog != null) Marshal.ReleaseComObject(dialog);
            }
        }

        public static string[] PickFiles(Window owner, string title, string filter, bool multiple = true)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                Multiselect = multiple,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(owner) == true ? dialog.FileNames : null;
        }

        public static string SaveFile(Window owner, string title, string filter, string fileName, string initialDirectory = null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = fileName,
                OverwritePrompt = true,
                AddExtension = true,
            };
            if (!string.IsNullOrEmpty(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
                dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        const uint FOS_NOCHANGEDIR = 0x8;
        const uint FOS_PICKFOLDERS = 0x20;
        const uint FOS_FORCEFILESYSTEM = 0x40;
        const uint FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRcw { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
