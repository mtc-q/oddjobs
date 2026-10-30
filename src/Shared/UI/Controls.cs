using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace OddJobs.UI
{
    /// <summary>The app's coloured tile with its glyph, drawn as vectors so it is crisp at any DPI.</summary>
    public class GlyphTile : FrameworkElement
    {
        public static readonly DependencyProperty AppProperty = DependencyProperty.Register(
            nameof(App), typeof(AppInfo), typeof(GlyphTile), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public AppInfo App
        {
            get => (AppInfo)GetValue(AppProperty);
            set => SetValue(AppProperty, value);
        }

        Geometry _glyph;
        string _glyphSource;

        protected override void OnRender(DrawingContext dc)
        {
            var app = App ?? OddApp.Info;
            if (app == null) return;
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;

            if (_glyphSource != app.Glyph)
            {
                _glyph = Geometry.Parse(app.Glyph);
                _glyph.Freeze();
                _glyphSource = app.Glyph;
            }

            var fill = new SolidColorBrush(Theme.Parse(app.Color));
            double radius = size * 0.22;
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, size, size), radius, radius);

            double scale = size * 0.64 / 24.0;
            double offset = (size - 24 * scale) / 2.0;
            dc.PushTransform(new TranslateTransform(offset, offset));
            dc.PushTransform(new ScaleTransform(scale, scale));
            var pen = new Pen(Brushes.White, Math.Max(2, 1.4 / scale))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, _glyph);
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>Top bar shown in every app: tile, name, tagline, then app-specific actions on the right.</summary>
    public class AppHeader : ContentControl
    {
        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
            nameof(Subtitle), typeof(string), typeof(AppHeader), new PropertyMetadata(null));

        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public AppInfo App => OddApp.Info;

        public AppHeader()
        {
            Subtitle = OddApp.Info?.Tagline;
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Help, (s, e) => Dialog.About(Window.GetWindow(this))));
        }
    }

    /// <summary>
    /// Shares a ListView's width between its GridView columns by weight, so there is never a
    /// horizontal scroll bar. Columns with weight 0 keep their own width.
    /// </summary>
    public static class ColumnFill
    {
        public static void Apply(ListView list, params double[] weights)
        {
            list.SizeChanged += (s, e) => Resize(list, weights);
            list.Loaded += (s, e) => Resize(list, weights);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        }

        static void Resize(ListView list, double[] weights)
        {
            if (!(list.View is GridView view) || list.ActualWidth <= 0) return;
            double fixedWidth = 0, totalWeight = 0;
            for (int i = 0; i < view.Columns.Count && i < weights.Length; i++)
            {
                if (weights[i] <= 0) fixedWidth += view.Columns[i].Width;
                else totalWeight += weights[i];
            }
            double available = list.ActualWidth - fixedWidth - 18;
            if (available <= 0 || totalWeight <= 0) return;
            for (int i = 0; i < view.Columns.Count && i < weights.Length; i++)
                if (weights[i] > 0) view.Columns[i].Width = Math.Max(48, Math.Floor(available * weights[i] / totalWeight));
        }
    }

    /// <summary>
    /// Makes any element accept dropped files and folders, with a visual drag-over state.
    /// </summary>
    public static class DropTarget
    {
        /// <param name="highlight">Element that shows the drag-over state; defaults to <paramref name="element"/>.</param>
        public static void Enable(UIElement element, Action<string[]> onDrop, UIElement highlight = null)
        {
            highlight = highlight ?? element;
            element.AllowDrop = true;
            element.DragEnter += (s, e) => Update(highlight, e);
            element.DragOver += (s, e) => Update(highlight, e);
            element.DragLeave += (s, e) => Look.SetIsDragOver(highlight, false);
            element.Drop += (s, e) =>
            {
                Look.SetIsDragOver(highlight, false);
                if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                {
                    e.Handled = true;
                    onDrop(paths);
                }
            };
        }

        static void Update(UIElement element, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            Look.SetIsDragOver(element, ok);
            e.Handled = true;
        }
    }
}
