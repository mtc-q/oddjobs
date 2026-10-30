using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OddJobs.UI;

// Usage: IconGen <repoRoot>
// Writes src/Shared/Icons/<App>.ico plus docs/assets/icons/<app>.svg and .png,
// all from the glyphs in src/Shared/UI/Brand.cs.
static class Program
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 256 };

    [STAThread]
    static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        string icoDir = Path.Combine(root, "src", "Shared", "Icons");
        string svgDir = Path.Combine(root, "docs", "assets", "icons");
        Directory.CreateDirectory(icoDir);
        Directory.CreateDirectory(svgDir);

        foreach (var app in Brand.All)
        {
            var color = (Color)ColorConverter.ConvertFromString(app.Color);
            var geometry = Geometry.Parse(app.Glyph);
            geometry.Freeze();

            var pngs = new List<byte[]>();
            foreach (int size in Sizes) pngs.Add(RenderPng(geometry, color, size));
            string slug = app.Name.ToLowerInvariant();
            File.WriteAllBytes(Path.Combine(icoDir, app.Name + ".ico"), BuildIco(Sizes, pngs));
            File.WriteAllBytes(Path.Combine(svgDir, slug + ".png"), pngs[Array.IndexOf(Sizes, 256)]);
            File.WriteAllText(Path.Combine(svgDir, slug + ".svg"), BuildSvg(app.Color, app.Glyph), new UTF8Encoding(false));
            Console.WriteLine("icon  " + app.Name);
        }
        return 0;
    }

    static byte[] RenderPng(Geometry glyph, Color color, int size)
    {
        // Small icons get a larger glyph and a minimum stroke so they stay legible in the taskbar.
        double glyphFraction = size <= 24 ? 0.72 : size <= 48 ? 0.66 : 0.62;
        double scale = size * glyphFraction / 24.0;
        double strokePx = Math.Max(2 * scale, size <= 20 ? 1.35 : 1.5);
        double offset = (size - 24 * scale) / 2.0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double radius = size * 0.22;
            var fill = new SolidColorBrush(color);
            dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, size, size), radius, radius);
            dc.PushTransform(new TranslateTransform(offset, offset));
            dc.PushTransform(new ScaleTransform(scale, scale));
            var pen = new Pen(Brushes.White, strokePx / scale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, glyph);
            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var ms = new MemoryStream())
        {
            encoder.Save(ms);
            return ms.ToArray();
        }
    }

    static byte[] BuildIco(int[] sizes, List<byte[]> pngs)
    {
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write((ushort)0);            // reserved
            w.Write((ushort)1);            // type: icon
            w.Write((ushort)sizes.Length); // count
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                w.Write(dim);              // width
                w.Write(dim);              // height
                w.Write((byte)0);          // palette
                w.Write((byte)0);          // reserved
                w.Write((ushort)1);        // planes
                w.Write((ushort)32);       // bpp
                w.Write(pngs[i].Length);
                w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
            return ms.ToArray();
        }
    }

    static string BuildSvg(string color, string path)
    {
        const double size = 48;
        double scale = size * 0.62 / 24.0;
        double offset = (size - 24 * scale) / 2.0;
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        return
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 48 48\" width=\"48\" height=\"48\" aria-hidden=\"true\">" +
            $"<rect width=\"48\" height=\"48\" rx=\"{F(size * 0.22)}\" fill=\"{color}\"/>" +
            $"<g transform=\"translate({F(offset)} {F(offset)}) scale({F(scale)})\" fill=\"none\" stroke=\"#fff\" stroke-width=\"2\" stroke-linecap=\"round\" stroke-linejoin=\"round\">" +
            $"<path d=\"{path}\"/></g></svg>\n";
    }
}
