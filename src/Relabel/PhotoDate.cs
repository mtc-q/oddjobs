using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace OddJobs.Relabel
{
    /// <summary>Reads the "date taken" a camera or phone wrote into a photo, without decoding the pixels.</summary>
    public static class PhotoDate
    {
        static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".tif", ".tiff", ".heic", ".heif", ".png", ".dng", ".webp",
        };

        static readonly string[] Queries =
        {
            "/app1/ifd/exif/{ushort=36867}",  // DateTimeOriginal (JPEG)
            "/app1/ifd/exif/{ushort=36868}",  // DateTimeDigitized (JPEG)
            "/ifd/exif/{ushort=36867}",       // DateTimeOriginal (TIFF, HEIC)
            "/ifd/exif/{ushort=36868}",
        };

        public static bool IsPhoto(string path) => Extensions.Contains(Path.GetExtension(path));

        public static DateTime? TryRead(string path)
        {
            if (!IsPhoto(path)) return null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
                {
                    var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    if (decoder.Frames.Count == 0 || !(decoder.Frames[0].Metadata is BitmapMetadata meta)) return null;

                    foreach (var query in Queries)
                    {
                        try
                        {
                            if (meta.GetQuery(query) is string s && TryParseExif(s, out var d)) return d;
                        }
                        catch (Exception) { /* query not valid for this container */ }
                    }
                    try
                    {
                        if (!string.IsNullOrEmpty(meta.DateTaken) &&
                            DateTime.TryParse(meta.DateTaken, CultureInfo.CurrentCulture, DateTimeStyles.None, out var taken))
                            return taken;
                    }
                    catch (NotSupportedException) { }
                }
            }
            catch (Exception) { /* unreadable, unsupported or not really an image */ }
            return null;
        }

        static bool TryParseExif(string s, out DateTime value) =>
            DateTime.TryParseExact(s.Trim().TrimEnd('\0'), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
