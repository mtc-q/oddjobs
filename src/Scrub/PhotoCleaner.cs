using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OddJobs.IO;
using OddJobs.UI;

namespace OddJobs.Scrub
{
    /// <summary>What a photo reveals about where, when and with what it was taken.</summary>
    public sealed class MetadataReport
    {
        public double? Latitude;
        public double? Longitude;
        public bool HasLocation;
        public string Camera;
        public string Taken;
        public string Software;
        public string Author;
        public string Copyright;
        public string Comment;
        public int Width;
        public int Height;
        public List<string> Blocks = new List<string>();
        public string Error;

        public bool HasAnything => HasLocation || Camera != null || Taken != null || Software != null || Author != null ||
                                   Copyright != null || Comment != null || Blocks.Count > 0;

        public string LocationText =>
            Latitude.HasValue && Longitude.HasValue
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.0000}° {1}, {2:0.0000}° {3}",
                    Math.Abs(Latitude.Value), Latitude.Value >= 0 ? "N" : "S", Math.Abs(Longitude.Value), Longitude.Value >= 0 ? "E" : "W")
                : HasLocation ? "Present" : null;
    }

    public sealed class CleanOptions
    {
        public int MaxEdge;            // 0 = keep the original size
        public bool AlwaysJpeg;
        public int Quality = 90;
        public bool KeepFileDates = true;
    }

    public static class PhotoCleaner
    {
        public static readonly string[] Extensions = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".heic", ".heif", ".webp", ".bmp", ".gif" };

        public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        // ---------- Reading -------------------------------------------------------------

        public static MetadataReport Inspect(string path)
        {
            var report = new MetadataReport();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (IsJpeg(fs)) report.Blocks.AddRange(JpegBlocks(fs).Select(DescribeJpegBlock).Where(b => b != null).Distinct());
                    else if (IsPng(fs)) report.Blocks.AddRange(PngChunks(fs).Select(DescribePngChunk).Where(b => b != null).Distinct());
                    fs.Position = 0;

                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    var frame = decoder.Frames[0];
                    report.Width = frame.PixelWidth;
                    report.Height = frame.PixelHeight;
                    if (frame.Metadata is BitmapMetadata meta)
                    {
                        ReadGps(meta, report);
                        report.Camera = Safe(() => Join(meta.CameraManufacturer, meta.CameraModel));
                        report.Taken = Safe(() => meta.DateTaken);
                        report.Software = Safe(() => meta.ApplicationName);
                        report.Author = Safe(() => meta.Author != null ? string.Join(", ", meta.Author) : null);
                        report.Copyright = Safe(() => meta.Copyright);
                        report.Comment = Safe(() => meta.Comment);
                        if (report.Blocks.Count == 0)
                            report.Blocks.AddRange(Safe(() => meta.Select(q => DescribeQuery(q)).Where(b => b != null).Distinct().ToList()) ?? new List<string>());
                    }
                }
            }
            catch (Exception ex)
            {
                report.Error = FriendlyError(path, ex);
            }
            return report;
        }

        static string Join(string a, string b)
        {
            a = a?.Trim();
            b = b?.Trim();
            if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? null : b;
            if (string.IsNullOrEmpty(b)) return a;
            return b.StartsWith(a, StringComparison.OrdinalIgnoreCase) ? b : a + " " + b;
        }

        static T Safe<T>(Func<T> read) where T : class
        {
            try
            {
                var v = read();
                return v is string s && string.IsNullOrWhiteSpace(s) ? null : v;
            }
            catch (Exception) { return null; }
        }

        static void ReadGps(BitmapMetadata meta, MetadataReport report)
        {
            foreach (var prefix in new[] { "/app1/ifd/gps", "/ifd/gps" })
            {
                try
                {
                    var lat = meta.GetQuery(prefix + "/{ushort=2}") as ulong[];
                    var lon = meta.GetQuery(prefix + "/{ushort=4}") as ulong[];
                    if (lat == null || lon == null) continue;
                    string latRef = meta.GetQuery(prefix + "/{ushort=1}") as string;
                    string lonRef = meta.GetQuery(prefix + "/{ushort=3}") as string;
                    double la = FromDms(lat), lo = FromDms(lon);
                    if (latRef != null && latRef.StartsWith("S", StringComparison.OrdinalIgnoreCase)) la = -la;
                    if (lonRef != null && lonRef.StartsWith("W", StringComparison.OrdinalIgnoreCase)) lo = -lo;
                    report.Latitude = la;
                    report.Longitude = lo;
                    report.HasLocation = true;
                    return;
                }
                catch (Exception) { }
            }
            // XMP can carry its own copy of the location.
            foreach (var q in new[] { "/xmp/exif:GPSLatitude", "/app1/ifd/xmp/exif:GPSLatitude" })
            {
                try { if (meta.GetQuery(q) != null) { report.HasLocation = true; return; } }
                catch (Exception) { }
            }
        }

        /// <summary>WIC packs EXIF rationals as numerator (low 32 bits) over denominator (high 32 bits).</summary>
        static double FromDms(ulong[] parts)
        {
            double R(ulong v)
            {
                uint num = (uint)(v & 0xFFFFFFFF), den = (uint)(v >> 32);
                return den == 0 ? 0 : (double)num / den;
            }
            double deg = parts.Length > 0 ? R(parts[0]) : 0, min = parts.Length > 1 ? R(parts[1]) : 0, sec = parts.Length > 2 ? R(parts[2]) : 0;
            return deg + min / 60 + sec / 3600;
        }

        static string DescribeQuery(string q)
        {
            switch (q.ToLowerInvariant())
            {
                case "/app1": case "/ifd": return "EXIF";
                case "/xmp": return "XMP";
                case "/app13": return "IPTC / Photoshop";
                case "/com": return "Comment";
                case "/app2": return "Colour profile";
                case "/iptc": return "IPTC / Photoshop";
                default: return q.StartsWith("/app", StringComparison.OrdinalIgnoreCase) && q != "/app0" ? "Maker data" : null;
            }
        }

        // ---------- Cleaning ------------------------------------------------------------

        /// <summary>
        /// Writes a copy that contains only pixels: the image is decoded, turned upright, converted to sRGB,
        /// optionally resized, and encoded fresh with no metadata at all. Returns the verified output path.
        /// </summary>
        public static string Clean(string input, string outputFolder, CleanOptions options, bool replaceOriginal, out string warning)
        {
            warning = null;
            BitmapSource pixels;
            bool hasAlpha;
            using (var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                int orientation = Thumbnails.ReadOrientation(frame.Metadata as BitmapMetadata);
                hasAlpha = Thumbnails.HasAlpha(frame.Format);

                BitmapSource src = frame;
                // Keep colours right after the colour profile is dropped by converting to sRGB first.
                if (frame.ColorContexts != null && frame.ColorContexts.Count > 0)
                {
                    try
                    {
                        var format = hasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32;
                        src = new ColorConvertedBitmap(frame, frame.ColorContexts[0], new ColorContext(format), format);
                    }
                    catch (Exception)
                    {
                        warning = "Colour profile could not be applied; colours may shift slightly.";
                        src = frame;
                    }
                }

                src = Thumbnails.Orient(src, orientation);

                if (options.MaxEdge > 0)
                {
                    int longest = Math.Max(src.PixelWidth, src.PixelHeight);
                    if (longest > options.MaxEdge)
                    {
                        double scale = (double)options.MaxEdge / longest;
                        src = new TransformedBitmap(src, new ScaleTransform(scale, scale));
                    }
                }

                bool toJpeg = OutputFormat(input, options) == ".jpg";
                pixels = hasAlpha && toJpeg ? Thumbnails.FlattenOnWhite(src) : src;
                pixels = Thumbnails.Materialize(pixels, !toJpeg && hasAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24);   // copy the pixels while the source stream is still open
            }

            string ext = OutputFormat(input, options);
            BitmapEncoder encoder = ext == ".png"
                ? (BitmapEncoder)new PngBitmapEncoder { Interlace = PngInterlaceOption.Off }
                : new JpegBitmapEncoder { QualityLevel = Math.Max(30, Math.Min(100, options.Quality)) };
            encoder.Frames.Add(BitmapFrame.Create(pixels, null, null, null));   // no thumbnail, no metadata, no profile

            string stem = Path.GetFileNameWithoutExtension(input);
            string originalExt = Path.GetExtension(input);
            string fileName = stem + (KeepsExtension(originalExt, ext) ? originalExt : ext);

            string target;
            if (replaceOriginal)
            {
                string folder = Path.GetDirectoryName(input);
                target = Path.Combine(folder, fileName);
                if (!string.Equals(target, input, StringComparison.OrdinalIgnoreCase))
                    target = PathUtil.UniquePath(folder, fileName);
            }
            else
            {
                Directory.CreateDirectory(outputFolder);
                target = PathUtil.UniquePath(outputFolder, fileName);
            }

            string temp = Path.Combine(Path.GetDirectoryName(target), "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".scrub.tmp");
            try
            {
                using (var outStream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    encoder.Save(outStream);

                string leftover = Verify(temp);
                if (leftover != null) throw new InvalidDataException("The cleaned file still contained " + leftover + ", so it was not saved.");

                if (replaceOriginal)
                {
                    var originalTimes = (File.GetCreationTimeUtc(input), File.GetLastWriteTimeUtc(input));
                    var failed = RecycleBin.Send(new[] { input }, IntPtr.Zero);
                    if (failed.Count > 0) throw new IOException("The original could not be moved to the Recycle Bin, so it was left untouched.");
                    File.Move(temp, target);
                    if (options.KeepFileDates) SetTimes(target, originalTimes.Item1, originalTimes.Item2);
                }
                else
                {
                    File.Move(temp, target);
                    if (options.KeepFileDates) SetTimes(target, File.GetCreationTimeUtc(input), File.GetLastWriteTimeUtc(input));
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
            return target;
        }

        static void SetTimes(string path, DateTime created, DateTime written)
        {
            try
            {
                File.SetCreationTimeUtc(path, created);
                File.SetLastWriteTimeUtc(path, written);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string OutputFormat(string input, CleanOptions options)
        {
            if (options.AlwaysJpeg) return ".jpg";
            string ext = Path.GetExtension(input).ToLowerInvariant();
            return ext == ".png" || ext == ".gif" || ext == ".bmp" ? ".png" : ".jpg";
        }

        static bool KeepsExtension(string original, string output)
        {
            string o = original.ToLowerInvariant();
            return output == ".jpg" ? (o == ".jpg" || o == ".jpeg" || o == ".jpe" || o == ".jfif") : o == ".png";
        }

        public static string FriendlyError(string path, Exception ex)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if ((ext == ".heic" || ext == ".heif") && (ex is NotSupportedException || ex is FileFormatException || ex is System.Runtime.InteropServices.COMException))
                return "Windows can't read HEIC photos yet. Install “HEIF Image Extensions” from the Microsoft Store, then try again.";
            if (ext == ".webp" && (ex is NotSupportedException || ex is FileFormatException))
                return "Windows can't read WebP images yet. Install “Webp Image Extensions” from the Microsoft Store.";
            if (ex is FileFormatException || ex is NotSupportedException) return "This file isn't a picture Windows can read, or it is damaged.";
            if (ex is UnauthorizedAccessException) return "No permission to read or write here.";
            return ex.Message;
        }

        // ---------- Verification at the byte level ------------------------------------------

        /// <summary>Returns null if the file holds pixels only, otherwise a description of what is left.</summary>
        public static string Verify(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (IsJpeg(fs))
                {
                    var bad = JpegBlocks(fs).Where(m => (m >= 0xE1 && m <= 0xEF) || m == 0xFE).ToList();
                    return bad.Count == 0 ? null : string.Join(", ", bad.Select(DescribeJpegBlock).Distinct());
                }
                if (IsPng(fs))
                {
                    var allowed = new HashSet<string> { "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "pHYs", "sBIT", "bKGD" };
                    var bad = PngChunks(fs).Where(c => !allowed.Contains(c)).ToList();
                    return bad.Count == 0 ? null : string.Join(", ", bad.Distinct());
                }
                return "an unknown format";
            }
        }

        static bool IsJpeg(Stream s)
        {
            s.Position = 0;
            return s.ReadByte() == 0xFF && s.ReadByte() == 0xD8;
        }

        static bool IsPng(Stream s)
        {
            s.Position = 0;
            var sig = new byte[8];
            return s.Read(sig, 0, 8) == 8 && sig[0] == 0x89 && sig[1] == (byte)'P' && sig[2] == (byte)'N' && sig[3] == (byte)'G';
        }

        /// <summary>Marker bytes of every segment before the image data starts.</summary>
        static List<int> JpegBlocks(Stream s)
        {
            var markers = new List<int>();
            s.Position = 2;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) break;
                if (b != 0xFF) break;
                int marker = s.ReadByte();
                while (marker == 0xFF) marker = s.ReadByte();
                if (marker < 0 || marker == 0xDA || marker == 0xD9) break;     // start of scan / end of image
                if (marker >= 0xD0 && marker <= 0xD7) continue;                 // restart markers have no length
                int hi = s.ReadByte(), lo = s.ReadByte();
                if (hi < 0 || lo < 0) break;
                int length = (hi << 8) | lo;
                markers.Add(marker);
                if (length < 2) break;
                s.Seek(length - 2, SeekOrigin.Current);
            }
            return markers;
        }

        static string DescribeJpegBlock(int marker)
        {
            if (marker == 0xE1) return "EXIF / XMP";
            if (marker == 0xE2) return "Colour profile";
            if (marker == 0xED) return "IPTC / Photoshop";
            if (marker == 0xFE) return "Comment";
            if (marker >= 0xE3 && marker <= 0xEF) return "Maker data";
            return null;
        }

        static List<string> PngChunks(Stream s)
        {
            var chunks = new List<string>();
            s.Position = 8;
            var header = new byte[8];
            while (s.Read(header, 0, 8) == 8)
            {
                int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                string type = Encoding.ASCII.GetString(header, 4, 4);
                chunks.Add(type);
                if (type == "IEND" || length < 0) break;
                s.Seek(length + 4L, SeekOrigin.Current);
            }
            return chunks;
        }

        static string DescribePngChunk(string type)
        {
            switch (type)
            {
                case "eXIf": return "EXIF";
                case "tEXt": case "zTXt": case "iTXt": return "Text notes";
                case "iCCP": return "Colour profile";
                case "tIME": return "Timestamp";
                default: return null;
            }
        }
    }
}
