using System;
using System.Globalization;

namespace OddJobs.IO
{
    public static class Format
    {
        static readonly string[] Units = { "bytes", "KB", "MB", "GB", "TB", "PB" };

        /// <summary>1536 becomes "1.5 KB". Uses 1024-based units, like File Explorer.</summary>
        public static string Size(long bytes)
        {
            if (bytes < 0) return "-" + Size(-bytes);
            if (bytes < 1024) return bytes == 1 ? "1 byte" : bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            string number = value >= 100 ? value.ToString("0", CultureInfo.CurrentCulture)
                : value >= 10 ? value.ToString("0.#", CultureInfo.CurrentCulture)
                : value.ToString("0.##", CultureInfo.CurrentCulture);
            return number + " " + Units[unit];
        }

        public static string Count(long n, string singular, string plural = null) =>
            n.ToString("N0", CultureInfo.CurrentCulture) + " " + (n == 1 ? singular : plural ?? singular + "s");

        public static string Number(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

        public static string Duration(TimeSpan t)
        {
            if (t.TotalSeconds < 1) return "under a second";
            if (t.TotalSeconds < 60) return ((int)Math.Round(t.TotalSeconds)) + " s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + " min " + t.Seconds + " s";
            return (int)t.TotalHours + " h " + t.Minutes + " min";
        }

        public static string Rate(double bytesPerSecond) => Size((long)bytesPerSecond) + "/s";

        public static string Date(DateTime local) => local.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

        public static string Percent(double fraction) =>
            fraction >= 0.1 ? (fraction * 100).ToString("0", CultureInfo.CurrentCulture) + "%"
            : fraction >= 0.001 ? (fraction * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%"
            : "<0.1%";
    }
}
