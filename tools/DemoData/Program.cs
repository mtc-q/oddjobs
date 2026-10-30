using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Usage: DemoData <outputFolder>
static class Program
{
    static readonly Random Rng = new Random(7);

    [STAThread]
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args.Length > 0 ? args[0] : "demo");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        // Photos with camera, date and GPS metadata.
        string photos = Dir(root, "Photos", "2026 Lisbon trip");
        var start = new DateTime(2026, 5, 2, 9, 14, 0);
        string[] names = { "IMG_4501", "IMG_4502", "IMG_4503", "IMG_4510", "IMG_4511", "IMG_4512", "IMG_4520", "IMG_4521", "IMG_4533", "IMG_4534", "IMG_4540", "IMG_4541" };
        for (int i = 0; i < names.Length; i++)
            WritePhoto(Path.Combine(photos, names[i] + ".JPG"), i, start.AddMinutes(i * 47 + Rng.Next(0, 20)), 38.70 + i * 0.003, -9.14 - i * 0.002);

        // Receipts / scans for Binder.
        string scans = Dir(root, "Scans");
        for (int i = 1; i <= 4; i++) WriteScan(Path.Combine(scans, "receipt-" + i + ".jpg"), i);

        // Duplicates for Twins.
        string downloads = Dir(root, "Downloads");
        string backup = Dir(root, "Desktop", "old phone backup");
        foreach (var n in new[] { "IMG_4501", "IMG_4502", "IMG_4510", "IMG_4520", "IMG_4533" })
            File.Copy(Path.Combine(photos, n + ".JPG"), Path.Combine(backup, n + ".JPG"));
        File.Copy(Path.Combine(photos, "IMG_4511.JPG"), Path.Combine(downloads, "IMG_4511 (1).JPG"));
        File.Copy(Path.Combine(photos, "IMG_4502.JPG"), Path.Combine(downloads, "photo.jpg"));

        // A messy Downloads folder for Tidy.
        var now = DateTime.Now;
        Blob(downloads, "Invoice-2026-0412.pdf", 184_000, now.AddDays(-12));
        Blob(downloads, "lease agreement signed.pdf", 2_400_000, now.AddDays(-40));
        Blob(downloads, "Quarterly report final v3.docx", 912_000, now.AddDays(-5));
        Blob(downloads, "budget 2026.xlsx", 64_000, now.AddDays(-9));
        Blob(downloads, "slides-team-offsite.pptx", 7_800_000, now.AddDays(-20));
        Blob(downloads, "VSCodeUserSetup-x64.exe", 96_000_000, now.AddDays(-60));
        Blob(downloads, "node-v24-x64.msi", 31_000_000, now.AddDays(-33));
        Blob(downloads, "fonts-inter.zip", 4_300_000, now.AddDays(-15));
        Blob(downloads, "project-assets.7z", 22_000_000, now.AddDays(-3));
        Blob(downloads, "podcast-episode-41.mp3", 48_000_000, now.AddDays(-7));
        Blob(downloads, "screen-recording.mp4", 156_000_000, now.AddDays(-2));
        Blob(downloads, "boarding-pass.png", 220_000, now.AddDays(-1));
        Blob(downloads, "notes.txt", 3_000, now.AddDays(-4));
        Blob(downloads, "Ubuntu-26.04-desktop-amd64.iso", 600_000_000, now.AddDays(-80), sparse: true);
        Blob(downloads, "report.crdownload", 1_200_000, now);

        // Two folders that have drifted apart, for Parity.
        string left = Dir(root, "Parity", "Documents");
        string right = Dir(root, "Parity", "USB backup");
        foreach (var (name, size) in new[] { ("taxes\\2025 return.pdf", 400_000), ("taxes\\receipts.zip", 2_000_000), ("cv.docx", 80_000), ("photos\\wedding.zip", 90_000_000), ("letters\\bank.pdf", 120_000), ("notes.md", 4_000) })
        {
            Blob(left, name, size, now.AddDays(-100));
            Blob(right, name, size, now.AddDays(-100));
        }
        Blob(left, "cv.docx", 82_000, now.AddDays(-2));                 // changed
        Blob(left, "taxes\\2026 estimate.xlsx", 50_000, now.AddDays(-6)); // new on the left
        Blob(left, "projects\\garden plan.pdf", 1_500_000, now.AddDays(-8));
        Blob(right, "old\\scan-0001.pdf", 300_000, now.AddDays(-400));   // only on the right

        // Text for Untangle.
        File.WriteAllText(Path.Combine(root, "copied-from-pdf.txt"),
            "The   quick start guide explains how to set up the de-\nvice in under ten minutes. Before you begin, make\nsure the battery is charged and the  “power” cable is\nconnected.\n\nStep one: hold the button for three seconds\nuntil the light turns green.\n", new UTF8Encoding(false));

        Console.WriteLine(root);
        return 0;
    }

    static string Dir(string root, params string[] parts)
    {
        string d = Path.Combine(root, Path.Combine(parts));
        Directory.CreateDirectory(d);
        return d;
    }

    static void Blob(string folder, string name, long size, DateTime modified, bool sparse = false)
    {
        string path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var fs = File.Create(path))
        {
            if (sparse) fs.SetLength(Math.Min(size, 4096));
            else
            {
                var buffer = new byte[64 * 1024];
                long left = size;
                while (left > 0)
                {
                    Rng.NextBytes(buffer);
                    int n = (int)Math.Min(buffer.Length, left);
                    fs.Write(buffer, 0, n);
                    left -= n;
                }
            }
        }
        File.SetLastWriteTime(path, modified);
    }

    static readonly string[][] Palettes =
    {
        new[] { "#F2E6D0", "#E9A25F", "#6B8F71", "#3F5E5A" },
        new[] { "#DDE8EE", "#F4C095", "#8AAAC2", "#425B76" },
        new[] { "#F6E3C3", "#E07A5F", "#81B29A", "#3D405B" },
        new[] { "#E8EEF2", "#FFD166", "#94A89A", "#4A5759" },
    };

    static void WritePhoto(string path, int seed, DateTime taken, double lat, double lon)
    {
        var p = Palettes[seed % Palettes.Length];
        Brush B(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        const int w = 1600, h = 1200;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(B(p[0]), null, new Rect(0, 0, w, h));
            dc.DrawEllipse(B(p[1]), null, new Point(300 + seed * 90 % 1000, 330 + seed * 37 % 200), 120, 120);
            var far = new StreamGeometry();
            using (var g = far.Open())
            {
                g.BeginFigure(new Point(0, 820), true, true);
                g.LineTo(new Point(380 + seed * 20, 520), true, false);
                g.LineTo(new Point(760, 760), true, false);
                g.LineTo(new Point(1150 - seed * 15, 480), true, false);
                g.LineTo(new Point(w, 760), true, false);
                g.LineTo(new Point(w, h), true, false);
                g.LineTo(new Point(0, h), true, false);
            }
            dc.DrawGeometry(B(p[2]), null, far);
            var near = new StreamGeometry();
            using (var g = near.Open())
            {
                g.BeginFigure(new Point(0, 980), true, true);
                g.LineTo(new Point(520, 820 + seed * 6), true, false);
                g.LineTo(new Point(1040, 960), true, false);
                g.LineTo(new Point(w, 860), true, false);
                g.LineTo(new Point(w, h), true, false);
                g.LineTo(new Point(0, h), true, false);
            }
            dc.DrawGeometry(B(p[3]), null, near);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);

        var meta = new BitmapMetadata("jpg");
        meta.SetQuery("/app1/ifd/{ushort=271}", "Apple");
        meta.SetQuery("/app1/ifd/{ushort=272}", "iPhone 16 Pro");
        meta.SetQuery("/app1/ifd/{ushort=305}", "26.4");
        meta.SetQuery("/app1/ifd/exif/{ushort=36867}", taken.ToString("yyyy:MM:dd HH:mm:ss"));
        meta.SetQuery("/app1/ifd/exif/{ushort=36868}", taken.ToString("yyyy:MM:dd HH:mm:ss"));
        meta.SetQuery("/app1/ifd/gps/{ushort=1}", lat >= 0 ? "N" : "S");
        meta.SetQuery("/app1/ifd/gps/{ushort=2}", Dms(Math.Abs(lat)));
        meta.SetQuery("/app1/ifd/gps/{ushort=3}", lon >= 0 ? "E" : "W");
        meta.SetQuery("/app1/ifd/gps/{ushort=4}", Dms(Math.Abs(lon)));
        meta.Author = new System.Collections.ObjectModel.ReadOnlyCollection<string>(new[] { "Sam Rivera" });

        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0), null, meta, null));
        using (var fs = File.Create(path)) encoder.Save(fs);
        File.SetLastWriteTime(path, taken.AddDays(3));
    }

    static ulong[] Dms(double value)
    {
        int deg = (int)value;
        double minutesFull = (value - deg) * 60;
        int min = (int)minutesFull;
        double sec = (minutesFull - min) * 60;
        ulong R(uint n, uint d) => ((ulong)d << 32) | n;
        return new[] { R((uint)deg, 1), R((uint)min, 1), R((uint)Math.Round(sec * 100), 100) };
    }

    static void WriteScan(string path, int seed)
    {
        const int w = 1200, h = 1700;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(246, 244, 239)), null, new Rect(0, 0, w, h));
            var ink = new SolidColorBrush(Color.FromRgb(40, 40, 40));
            var face = new Typeface("Consolas");
            void Text(string s, double x, double y, double size) =>
                dc.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, ink, 1.0), new Point(x, y));
            Text("CAFE MIRADOURO", 360, 120, 52);
            Text("Rua da Rosa 12, Lisboa", 330, 200, 34);
            for (int i = 0; i < 6 + seed; i++)
            {
                Text("Item " + (i + 1).ToString("00"), 140, 340 + i * 80, 38);
                Text((2.5 + i * 1.35 + seed).ToString("0.00") + " EUR", 820, 340 + i * 80, 38);
            }
            Text("TOTAL", 140, 1300, 46);
            Text((24.80 + seed * 7.1).ToString("0.00") + " EUR", 780, 1300, 46);
            Text("Obrigado!", 460, 1500, 40);
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0)));
        using (var fs = File.Create(path)) encoder.Save(fs);
    }
}
