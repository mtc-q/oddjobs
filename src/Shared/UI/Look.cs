using System.Windows;
using System.Windows.Media;

namespace OddJobs.UI
{
    /// <summary>Attached properties the shared control templates read, so one template serves every variant.</summary>
    public static class Look
    {
        public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
            "HoverBackground", typeof(Brush), typeof(Look), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
        public static Brush GetHoverBackground(DependencyObject o) => (Brush)o.GetValue(HoverBackgroundProperty);
        public static void SetHoverBackground(DependencyObject o, Brush v) => o.SetValue(HoverBackgroundProperty, v);

        public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
            "PressedBackground", typeof(Brush), typeof(Look), new PropertyMetadata(null));
        public static Brush GetPressedBackground(DependencyObject o) => (Brush)o.GetValue(PressedBackgroundProperty);
        public static void SetPressedBackground(DependencyObject o, Brush v) => o.SetValue(PressedBackgroundProperty, v);

        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
            "CornerRadius", typeof(CornerRadius), typeof(Look), new PropertyMetadata(new CornerRadius(5)));
        public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
        public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

        /// <summary>A Segoe Fluent Icons / MDL2 glyph shown before a button's content.</summary>
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(string), typeof(Look), new PropertyMetadata(null));
        public static string GetIcon(DependencyObject o) => (string)o.GetValue(IconProperty);
        public static void SetIcon(DependencyObject o, string v) => o.SetValue(IconProperty, v);

        /// <summary>Grey hint text shown in an empty text box.</summary>
        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
            "Placeholder", typeof(string), typeof(Look), new PropertyMetadata(null));
        public static string GetPlaceholder(DependencyObject o) => (string)o.GetValue(PlaceholderProperty);
        public static void SetPlaceholder(DependencyObject o, string v) => o.SetValue(PlaceholderProperty, v);

        /// <summary>Set while files are being dragged over a drop target.</summary>
        public static readonly DependencyProperty IsDragOverProperty = DependencyProperty.RegisterAttached(
            "IsDragOver", typeof(bool), typeof(Look), new PropertyMetadata(false));
        public static bool GetIsDragOver(DependencyObject o) => (bool)o.GetValue(IsDragOverProperty);
        public static void SetIsDragOver(DependencyObject o, bool v) => o.SetValue(IsDragOverProperty, v);
    }

    /// <summary>Segoe Fluent Icons code points that also exist in Segoe MDL2 Assets (Windows 10).</summary>
    public static class Icons
    {
        public const string Add = "";
        public const string Close = "";
        public const string Delete = "";
        public const string Folder = "";
        public const string FolderOpen = "";
        public const string Refresh = "";
        public const string Copy = "";
        public const string Info = "";
        public const string Check = "";
        public const string Warning = "";
        public const string ChevronDown = "";
        public const string ChevronUp = "";
        public const string ChevronRight = "";
        public const string ChevronLeft = "";
        public const string Undo = "";
        public const string Search = "";
        public const string Document = "";
        public const string Photo = "";
        public const string View = "";
        public const string Hide = "";
        public const string Lock = "";
        public const string Unlock = "";
        public const string Sort = "";
        public const string Sync = "";
        public const string Up = "";
        public const string Down = "";
        public const string Rotate = "";
        public const string Download = "";
        public const string Shield = "";
        public const string Stop = "";
        public const string Back = "";
        public const string OpenFile = "";
        public const string Save = "";
        public const string Clear = "";
        public const string Filter = "";
    }
}
