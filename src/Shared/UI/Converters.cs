using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace OddJobs.UI
{
    /// <summary>Splits a path for display: the folder's own name, and where it lives.</summary>
    public sealed class PathParts : IValueConverter
    {
        public static readonly PathParts Name = new PathParts(true);
        public static readonly PathParts Parent = new PathParts(false);

        readonly bool _name;
        PathParts(bool name) { _name = name; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var path = (value as string ?? "").TrimEnd('\\');
            if (path.Length == 0) return "";
            string name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return _name ? path : "";
            return _name ? name : Path.GetDirectoryName(path) ?? "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }

    /// <summary>True becomes Visible, false Collapsed. Pass "invert" as the parameter to flip it.</summary>
    public sealed class VisibleWhen : IValueConverter
    {
        public static readonly VisibleWhen Instance = new VisibleWhen();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool on = value is bool b ? b : value != null;
            if (parameter as string == "invert") on = !on;
            return on ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }
}
