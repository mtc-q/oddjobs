using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OddJobs.UI
{
    /// <summary>Small, upright previews of photos. Safe to call from a background thread.</summary>
    public static class Thumbnails
    {
        /// <summary>Returns a frozen bitmap no wider or taller than <paramref name="max"/> pixels, or null.</summary>
        public static BitmapSource Load(string path, int max)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0) return null;
                    var frame = decoder.Frames[0];
                    int orientation = ReadOrientation(frame.Metadata as BitmapMetadata);
                    int w = frame.PixelWidth, h = frame.PixelHeight;
                    if (w <= 0 || h <= 0) return null;

                    fs.Position = 0;
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    image.StreamSource = fs;
                    if (w >= h) image.DecodePixelWidth = Math.Min(max, w);
                    else image.DecodePixelHeight = Math.Min(max, h);
                    image.EndInit();
                    image.Freeze();
                    return Orient(image, orientation);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Copies the pixels into a plain, frozen bitmap so nothing keeps the source file or decoder alive.
        /// Pure pixel copying, so it is safe on any thread.
        /// </summary>
        public static BitmapSource Materialize(BitmapSource source, PixelFormat format)
        {
            var converted = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);
            int stride = (converted.PixelWidth * format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * converted.PixelHeight];
            converted.CopyPixels(pixels, stride, 0);
            var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, format, null, pixels, stride);
            result.Freeze();
            return result;
        }

        /// <summary>
        /// Puts see-through pixels on white (JPEG and PDF images have no transparency).
        /// Done with plain arithmetic rather than WPF drawing, so it works on background threads.
        /// </summary>
        public static BitmapSource FlattenOnWhite(BitmapSource source)
        {
            var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var pixels = new byte[w * h * 4];
            bgra.CopyPixels(pixels, w * 4, 0);
            var rgb = new byte[w * h * 3];
            for (int i = 0, o = 0; i < pixels.Length; i += 4, o += 3)
            {
                int a = pixels[i + 3], inv = 255 - a;
                rgb[o] = (byte)((pixels[i] * a + 255 * inv + 127) / 255);
                rgb[o + 1] = (byte)((pixels[i + 1] * a + 255 * inv + 127) / 255);
                rgb[o + 2] = (byte)((pixels[i + 2] * a + 255 * inv + 127) / 255);
            }
            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
            result.Freeze();
            return result;
        }

        /// <summary>True for pixel formats that can hold transparency.</summary>
        public static bool HasAlpha(PixelFormat f) =>
            f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 ||
            f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float ||
            f == PixelFormats.Indexed1 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed8;

        /// <summary>EXIF orientation 1–8 (1 = upright). Phones store rotation here instead of rotating pixels.</summary>
        public static int ReadOrientation(BitmapMetadata meta)
        {
            if (meta == null) return 1;
            foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}", "System.Photo.Orientation" })
            {
                try
                {
                    var v = meta.GetQuery(q);
                    if (v is ushort u && u >= 1 && u <= 8) return u;
                    if (v is uint ui && ui >= 1 && ui <= 8) return (int)ui;
                }
                catch (Exception) { }
            }
            return 1;
        }

        /// <summary>Applies an EXIF orientation so the image is upright.</summary>
        public static BitmapSource Orient(BitmapSource source, int orientation)
        {
            if (orientation <= 1 || orientation > 8) return source;
            Transform t;
            switch (orientation)
            {
                case 2: t = new ScaleTransform(-1, 1); break;
                case 3: t = new RotateTransform(180); break;
                case 4: t = new ScaleTransform(1, -1); break;
                case 5: t = new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } }; break;
                case 6: t = new RotateTransform(90); break;
                case 7: t = new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } }; break;
                default: t = new RotateTransform(270); break;
            }
            var result = new TransformedBitmap(source, t);
            result.Freeze();
            return result;
        }
    }
}
