namespace OddJobs.UI
{
    public sealed class AppInfo
    {
        public AppInfo(string name, string tagline, string color, string glyph)
        {
            Name = name;
            Tagline = tagline;
            Color = color;
            Glyph = glyph;
        }

        public string Name { get; }
        public string Tagline { get; }
        /// <summary>Tile colour, as #RRGGBB.</summary>
        public string Color { get; }
        /// <summary>Stroke path on a 24x24 grid. Valid as both WPF geometry and SVG path data.</summary>
        public string Glyph { get; }
    }

    /// <summary>
    /// The one place app names, colours and glyphs are defined. The icon generator
    /// compiles this same file, so in-app headers, .ico files and the website stay in sync.
    /// </summary>
    public static class Brand
    {
        public const string Suite = "Odd Jobs";
        public const string Version = "1.0.0";

        public static readonly AppInfo Relabel = new AppInfo("Relabel", "Rename hundreds of files in one go", "#E8590C",
            "M3.5 3.5 H11 L20.5 13 L13 20.5 L3.5 11 Z M9.5 8 A1.5 1.5 0 1 1 6.5 8 A1.5 1.5 0 1 1 9.5 8 Z");

        public static readonly AppInfo Twins = new AppInfo("Twins", "Find duplicate files and reclaim the space", "#3B5BDB",
            "M7.5 3.5 H19.5 A1 1 0 0 1 20.5 4.5 V16.5 M4.5 7.5 H15.5 A1 1 0 0 1 16.5 8.5 V19.5 A1 1 0 0 1 15.5 20.5 H4.5 A1 1 0 0 1 3.5 19.5 V8.5 A1 1 0 0 1 4.5 7.5 Z M7.5 12 H12.5 M7.5 16 H12.5");

        public static readonly AppInfo Footprint = new AppInfo("Footprint", "See what is filling up your disk", "#2F9E44",
            "M4 3.5 H20 A0.5 0.5 0 0 1 20.5 4 V20 A0.5 0.5 0 0 1 20 20.5 H4 A0.5 0.5 0 0 1 3.5 20 V4 A0.5 0.5 0 0 1 4 3.5 Z M12 3.5 V20.5 M12 11 H20.5 M3.5 15 H12 M16.25 11 V20.5");

        public static readonly AppInfo Scrub = new AppInfo("Scrub", "Strip location and hidden data from photos", "#0C8599",
            "M12 20.5 C9 17.5 5.5 13.5 5.5 9.5 A6.5 6.5 0 0 1 18.5 9.5 C18.5 13.5 15 17.5 12 20.5 Z M14 9.5 A2 2 0 1 1 10 9.5 A2 2 0 1 1 14 9.5 Z M3.5 3.5 L20.5 20.5");

        public static readonly AppInfo Tidy = new AppInfo("Tidy", "Sort a messy Downloads folder, with undo", "#AE3EC9",
            "M3.5 13.5 H8 L9.5 16 H14.5 L16 13.5 H20.5 V20.5 H3.5 Z M6.5 4 H17.5 M8 7.25 H16 M9.5 10.5 H14.5");

        public static readonly AppInfo Untangle = new AppInfo("Untangle", "Clean up messy text in one pass", "#C2255C",
            "M3.5 7 C5.5 3.5 7.5 10.5 9.5 7 C11 4.5 12.5 6 13.5 7 H20.5 M3.5 12.5 H20.5 M3.5 18 H14.5");

        public static readonly AppInfo Verity = new AppInfo("Verity", "Check that a file is exactly what it claims", "#7048E8",
            "M12 3.5 L19.5 6.25 V11.5 C19.5 15.75 16.25 19 12 20.5 C7.75 19 4.5 15.75 4.5 11.5 V6.25 Z M8.75 12 L11 14.25 L15.5 9.5");

        public static readonly AppInfo Binder = new AppInfo("Binder", "Turn photos and scans into one PDF", "#E03131",
            "M7 3.5 H18.5 A1 1 0 0 1 19.5 4.5 V19.5 A1 1 0 0 1 18.5 20.5 H7 A1 1 0 0 1 6 19.5 V4.5 A1 1 0 0 1 7 3.5 Z M3.5 7.5 H8 M3.5 12 H8 M3.5 16.5 H8 M11 8 H16 M11 12 H16 M11 16 H14");

        public static readonly AppInfo Lockbox = new AppInfo("Lockbox", "Lock files with a password", "#B7791F",
            "M6 10.5 H18 A1 1 0 0 1 19 11.5 V19.5 A1 1 0 0 1 18 20.5 H6 A1 1 0 0 1 5 19.5 V11.5 A1 1 0 0 1 6 10.5 Z M8 10.5 V7.5 A4 4 0 0 1 16 7.5 V10.5 M12 14.5 V16.5");

        public static readonly AppInfo Parity = new AppInfo("Parity", "Compare two folders and copy what's missing", "#5C940D",
            "M3.5 4.5 H7.5 V19.5 H3.5 Z M16.5 4.5 H20.5 V19.5 H16.5 Z M10 9.5 H14 M12.5 8 L14 9.5 L12.5 11 M14 14.5 H10 M11.5 13 L10 14.5 L11.5 16");

        public static readonly AppInfo[] All = { Relabel, Twins, Footprint, Scrub, Tidy, Untangle, Verity, Binder, Lockbox, Parity };
    }
}
