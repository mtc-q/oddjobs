using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OddJobs.UI;

namespace OddJobs.Binder
{
    public enum PageSize { FitImage, A4, Letter }
    public enum PageMargin { None, Small, Normal }

    public sealed class BindOptions
    {
        public PageSize Size = PageSize.A4;
        public PageMargin Margin = PageMargin.Small;
        public bool Compact;
    }

    /// <summary>Turns image files into PDF pages: upright, sized and centred on the page.</summary>
    public static class PageMaker
    {
        public static readonly string[] Extensions = { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp" };

        const int CompactMaxEdge = 2000;

        public static PdfPage Make(string path, int extraRotation, BindOptions options)
        {
            var image = LoadImage(path, extraRotation, options.Compact);

            // Size the page.
            double pageW, pageH;
            if (options.Size == PageSize.FitImage)
            {
                double longest = Math.Max(image.Width, image.Height);
                double scale = 842.0 / longest;                       // the long side matches A4's long side
                pageW = image.Width * scale;
                pageH = image.Height * scale;
            }
            else
            {
                double a = options.Size == PageSize.A4 ? 595.28 : 612, b = options.Size == PageSize.A4 ? 841.89 : 792;
                bool landscape = image.Width > image.Height;
                pageW = landscape ? b : a;
                pageH = landscape ? a : b;
            }

            double margin = options.Size == PageSize.FitImage ? 0 : options.Margin == PageMargin.Normal ? 36 : options.Margin == PageMargin.Small ? 18 : 0;
            double boxW = pageW - 2 * margin, boxH = pageH - 2 * margin;
            double fit = Math.Min(boxW / image.Width, boxH / image.Height);
            double drawW = image.Width * fit, drawH = image.Height * fit;

            return new PdfPage
            {
                Width = pageW,
                Height = pageH,
                Image = image,
                DrawWidth = drawW,
                DrawHeight = drawH,
                X = (pageW - drawW) / 2,
                Y = (pageH - drawH) / 2,
            };
        }

        static PdfImage LoadImage(string path, int extraRotation, bool compact)
        {
            byte[] bytes = File.ReadAllBytes(path);

            // Best case: an upright RGB or greyscale JPEG goes into the PDF untouched, with no quality loss.
            if (!compact && extraRotation % 360 == 0 && JpegInfo.TryRead(bytes, out var info) &&
                (info.Components == 3 || info.Components == 1) && info.Orientation == 1)
            {
                return new PdfImage { Data = JpegInfo.StripMetadata(bytes), IsJpeg = true, Width = info.Width, Height = info.Height, Components = info.Components };
            }

            BitmapSource src;
            bool photo;
            using (var ms = new MemoryStream(bytes))
            {
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                photo = decoder is JpegBitmapDecoder || IsPhotoExtension(path);
                src = Thumbnails.Orient(frame, Thumbnails.ReadOrientation(frame.Metadata as BitmapMetadata));
                if (extraRotation % 360 != 0)
                {
                    src = new TransformedBitmap(src, new RotateTransform(extraRotation));
                }
                if (compact)
                {
                    int longest = Math.Max(src.PixelWidth, src.PixelHeight);
                    if (longest > CompactMaxEdge)
                    {
                        double s = (double)CompactMaxEdge / longest;
                        src = new TransformedBitmap(src, new ScaleTransform(s, s));
                    }
                }
                if (Thumbnails.HasAlpha(src.Format)) src = Thumbnails.FlattenOnWhite(src);
                src = new FormatConvertedBitmap(src, PixelFormats.Rgb24, null, 0);
                src.Freeze();
            }

            int w = src.PixelWidth, h = src.PixelHeight;
            if (photo || compact)
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = compact ? 80 : 92 };
                encoder.Frames.Add(BitmapFrame.Create(src));
                using (var outStream = new MemoryStream())
                {
                    encoder.Save(outStream);
                    return new PdfImage { Data = outStream.ToArray(), IsJpeg = true, Width = w, Height = h };
                }
            }

            // Screenshots and drawings stay pixel-perfect: raw RGB, losslessly compressed.
            var raw = new byte[w * h * 3];
            src.CopyPixels(raw, w * 3, 0);
            return new PdfImage { Data = PdfWriter.Zlib(raw), IsJpeg = false, Width = w, Height = h };
        }

        static bool IsPhotoExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".heic" || ext == ".heif" || ext == ".webp" || ext == ".jpg" || ext == ".jpeg";
        }

    }

    /// <summary>Reads size, colour channels and EXIF orientation straight from JPEG headers.</summary>
    public struct JpegInfo
    {
        public int Width, Height, Components, Orientation;

        public static bool TryRead(byte[] d, out JpegInfo info)
        {
            info = new JpegInfo { Orientation = 1 };
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
            int i = 2;
            bool gotFrame = false;
            while (i + 4 <= d.Length)
            {
                if (d[i] != 0xFF) return false;
                byte marker = d[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD9 || marker == 0xDA) break;
                int len = (d[i + 2] << 8) | d[i + 3];
                if (len < 2 || i + 2 + len > d.Length) return false;

                if (marker == 0xE1 && len > 16 && d[i + 4] == 'E' && d[i + 5] == 'x' && d[i + 6] == 'i' && d[i + 7] == 'f')
                    info.Orientation = ExifOrientation(d, i + 10, len - 8);
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof && len >= 8)
                {
                    // Arithmetic-coded JPEGs (C9-CB, CD-CF) are rare and poorly supported by viewers; re-encode those.
                    if (marker >= 0xC9) return false;
                    info.Height = (d[i + 5] << 8) | d[i + 6];
                    info.Width = (d[i + 7] << 8) | d[i + 8];
                    info.Components = d[i + 9];
                    gotFrame = true;
                }
                i += 2 + len;
            }
            return gotFrame && info.Width > 0 && info.Height > 0;
        }

        /// <summary>
        /// Copies a JPEG without its EXIF, XMP, IPTC and comment blocks (so no GPS location ends up inside
        /// the PDF). The compressed image data is copied byte for byte, so quality is untouched.
        /// APP0 (JFIF) and APP14 (Adobe colour transform) are kept because decoders need them.
        /// </summary>
        public static byte[] StripMetadata(byte[] d)
        {
            using (var ms = new MemoryStream(d.Length))
            {
                ms.WriteByte(0xFF);
                ms.WriteByte(0xD8);
                int i = 2;
                while (i + 4 <= d.Length && d[i] == 0xFF)
                {
                    byte marker = d[i + 1];
                    if (marker == 0xFF) { i++; continue; }
                    if (marker == 0xDA) break;   // start of scan: the rest is image data
                    int len = (d[i + 2] << 8) | d[i + 3];
                    if (len < 2 || i + 2 + len > d.Length) break;
                    bool drop = (marker >= 0xE1 && marker <= 0xEF && marker != 0xEE) || marker == 0xFE;
                    if (!drop) ms.Write(d, i, 2 + len);
                    i += 2 + len;
                }
                ms.Write(d, i, d.Length - i);
                return ms.ToArray();
            }
        }

        static int ExifOrientation(byte[] d, int tiff, int length)
        {
            try
            {
                bool little = d[tiff] == 'I';
                int U16(int at) => little ? d[at] | (d[at + 1] << 8) : (d[at] << 8) | d[at + 1];
                int U32(int at) => little ? d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24) : (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];
                int ifd = tiff + U32(tiff + 4);
                int count = U16(ifd);
                for (int e = 0; e < count; e++)
                {
                    int entry = ifd + 2 + e * 12;
                    if (entry + 12 > tiff + length) break;
                    if (U16(entry) == 0x0112) return Math.Max(1, Math.Min(8, U16(entry + 8)));
                }
            }
            catch (IndexOutOfRangeException) { }
            return 1;
        }
    }
}
