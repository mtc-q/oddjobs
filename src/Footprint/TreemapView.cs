using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Footprint
{
    /// <summary>
    /// Draws a folder as nested rectangles sized by bytes. Folders get a slim header with their name;
    /// files are coloured by kind. Hit-testing returns the deepest tile under the mouse.
    /// </summary>
    public sealed class TreemapView : FrameworkElement
    {
        const int MaxDepth = 4;
        const double Header = 18;

        readonly List<Tile> _hits = new List<Tile>();
        Node _root;
        Node _hover;
        Node _selected;

        public event Action<Node> HoverChanged;
        public event Action<Node> Clicked;
        public event Action<Node> DoubleClicked;
        public event Action<Node, Point> RightClicked;

        public static readonly Dictionary<FileKind, Color> KindColors = new Dictionary<FileKind, Color>
        {
            [FileKind.Photo] = Color.FromRgb(0x4F, 0x8D, 0xF0),
            [FileKind.Video] = Color.FromRgb(0xE8, 0x6F, 0x4B),
            [FileKind.Audio] = Color.FromRgb(0xB0, 0x68, 0xD8),
            [FileKind.Document] = Color.FromRgb(0x3C, 0xAE, 0x7A),
            [FileKind.Archive] = Color.FromRgb(0xE2, 0xB1, 0x3C),
            [FileKind.Installer] = Color.FromRgb(0xE0, 0x5A, 0x8A),
            [FileKind.Code] = Color.FromRgb(0x3F, 0xB4, 0xC6),
            [FileKind.DiskImage] = Color.FromRgb(0x84, 0x74, 0xE4),
            [FileKind.Font] = Color.FromRgb(0x9C, 0xB9, 0x4A),
            [FileKind.System] = Color.FromRgb(0x8E, 0x95, 0x9C),
            [FileKind.Other] = Color.FromRgb(0xA7, 0xAD, 0xB3),
        };

        static readonly Dictionary<FileKind, Brush> KindBrushes = new Dictionary<FileKind, Brush>();

        static TreemapView()
        {
            foreach (var pair in KindColors)
            {
                var b = new SolidColorBrush(pair.Value);
                b.Freeze();
                KindBrushes[pair.Key] = b;
            }
        }

        public TreemapView()
        {
            ClipToBounds = true;
            Theme.Changed += InvalidateVisual;
        }

        public Node Root
        {
            get => _root;
            set { _root = value; _hover = null; InvalidateVisual(); }
        }

        public Node Selected
        {
            get => _selected;
            set { _selected = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            _hits.Clear();
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(Theme.Brush("Bg"), null, bounds);
            if (_root == null || _root.Size <= 0) return;

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Draw(dc, _root, bounds, 0, pixelsPerDip);

            if (_selected != null)
            {
                foreach (var t in _hits)
                {
                    if (t.Node != _selected) continue;
                    var pen = new Pen(Theme.Brush("Text"), 2);
                    dc.DrawRectangle(null, pen, Inflate(t.Rect, -1));
                    break;
                }
            }
        }

        void Draw(DrawingContext dc, Node folder, Rect area, int depth, double ppd)
        {
            var tiles = Treemap.Layout(folder.Children, area);
            var folderBg = Theme.Brush(depth % 2 == 0 ? "Surface2" : "Surface3");
            var collapsedFolder = Theme.Brush("BorderStrong");
            var muted = Theme.Brush("TextMuted");
            var text = Theme.Brush("Text");

            foreach (var tile in tiles)
            {
                var r = Inflate(tile.Rect, -1);
                if (r.Width < 1.5 || r.Height < 1.5) continue;
                var node = tile.Node;
                _hits.Add(new Tile { Node = node, Rect = r });

                if (node.IsDir)
                {
                    bool roomForChildren = depth < MaxDepth && r.Width > 36 && r.Height > Header + 16 && node.Children.Count > 0;
                    dc.DrawRoundedRectangle(roomForChildren ? folderBg : collapsedFolder, null, r, 2, 2);
                    if (r.Width > 48 && r.Height > 14)
                        Label(dc, node.Name + "  " + Format.Size(node.Size), new Point(r.X + 5, r.Y + 2), r.Width - 10, roomForChildren ? text : muted, 11, ppd, true);
                    if (roomForChildren)
                        Draw(dc, node, new Rect(r.X + 2, r.Y + Header, r.Width - 4, r.Height - Header - 2), depth + 1, ppd);
                }
                else
                {
                    var fill = KindBrushes[node.IsAggregate ? FileKind.Other : node.Kind];
                    dc.DrawRoundedRectangle(fill, null, r, 2, 2);
                    if (r.Width > 56 && r.Height > 18)
                        Label(dc, node.Name, new Point(r.X + 5, r.Y + 3), r.Width - 10, Brushes.White, 11, ppd, false);
                }
            }
        }

        static Rect Inflate(Rect r, double by)
        {
            double w = Math.Max(0, r.Width + 2 * by), h = Math.Max(0, r.Height + 2 * by);
            return new Rect(r.X - by, r.Y - by, w, h);
        }

        static readonly Typeface Regular = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static readonly Typeface Semibold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        static void Label(DrawingContext dc, string s, Point at, double maxWidth, Brush brush, double size, double ppd, bool bold)
        {
            if (maxWidth < 12) return;
            var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? Semibold : Regular, size, brush, ppd)
            {
                MaxTextWidth = maxWidth,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            dc.DrawText(ft, at);
        }

        Node HitTest(Point p)
        {
            for (int i = _hits.Count - 1; i >= 0; i--)
                if (_hits[i].Rect.Contains(p)) return _hits[i].Node;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var node = HitTest(e.GetPosition(this));
            if (node != _hover)
            {
                _hover = node;
                Cursor = node != null && node.IsDir ? Cursors.Hand : Cursors.Arrow;
                HoverChanged?.Invoke(node);
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = null;
            HoverChanged?.Invoke(null);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var node = HitTest(e.GetPosition(this));
            if (node == null) return;
            if (e.ClickCount >= 2) DoubleClicked?.Invoke(node);
            else Clicked?.Invoke(node);
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            var node = HitTest(e.GetPosition(this));
            if (node != null) RightClicked?.Invoke(node, e.GetPosition(this));
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            InvalidateVisual();
        }
    }
}
