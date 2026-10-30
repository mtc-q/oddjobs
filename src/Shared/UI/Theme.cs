using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace OddJobs.UI
{
    /// <summary>
    /// Black or white, nothing in between. Follows the Windows app theme live,
    /// and paints the native title bar to match.
    /// </summary>
    public static class Theme
    {
        public static bool IsDark { get; private set; }
        public static Color Accent { get; private set; }
        public static event Action Changed;

        static bool _applied;
        static Color _accentLight, _accentDark;

        static readonly Dictionary<string, string> Light = new Dictionary<string, string>
        {
            ["Bg"] = "#FFFFFF",
            ["Surface"] = "#FAFAFA",
            ["Surface2"] = "#F2F2F2",
            ["Surface3"] = "#E8E8E8",
            ["Border"] = "#E5E5E5",
            ["BorderStrong"] = "#CCCCCC",
            ["InputBorder"] = "#D4D4D4",
            ["Text"] = "#0A0A0A",
            ["TextMuted"] = "#5C5C5C",
            ["TextFaint"] = "#949494",
            ["Primary"] = "#0A0A0A",
            ["PrimaryHover"] = "#333333",
            ["PrimaryText"] = "#FFFFFF",
            ["Danger"] = "#D92D20",
            ["DangerHover"] = "#B42318",
            ["DangerText"] = "#FFFFFF",
            ["DangerSoft"] = "#FDECEA",
            ["Success"] = "#14883E",
            ["SuccessSoft"] = "#E7F6EC",
            ["Warning"] = "#B54708",
            ["WarningSoft"] = "#FEF4E6",
            ["Overlay"] = "#66000000",
        };

        static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
        {
            ["Bg"] = "#000000",
            ["Surface"] = "#0D0D0D",
            ["Surface2"] = "#1A1A1A",
            ["Surface3"] = "#262626",
            ["Border"] = "#242424",
            ["BorderStrong"] = "#3D3D3D",
            ["InputBorder"] = "#333333",
            ["Text"] = "#EDEDED",
            ["TextMuted"] = "#A3A3A3",
            ["TextFaint"] = "#6B6B6B",
            ["Primary"] = "#EDEDED",
            ["PrimaryHover"] = "#C7C7C7",
            ["PrimaryText"] = "#000000",
            ["Danger"] = "#FF6B61",
            ["DangerHover"] = "#FF8F87",
            ["DangerText"] = "#000000",
            ["DangerSoft"] = "#2A0F0D",
            ["Success"] = "#4ADE80",
            ["SuccessSoft"] = "#0B2414",
            ["Warning"] = "#FBBF24",
            ["WarningSoft"] = "#2A1E05",
            ["Overlay"] = "#99000000",
        };

        public static void Init(Color accentLight, Color accentDark)
        {
            _accentLight = accentLight;
            _accentDark = accentDark;
            Apply();
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            Application.Current.Exit += (s, e) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }

        static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(new Action(Apply));
        }

        static bool SystemPrefersDark()
        {
#if SCREENSHOT
            var forced = Environment.GetEnvironmentVariable("ODDJOBS_THEME");
            if (forced == "dark") return true;
            if (forced == "light") return false;
#endif
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        public static void Apply()
        {
            bool dark = SystemPrefersDark();
            if (_applied && dark == IsDark) return;
            _applied = true;
            IsDark = dark;
            Accent = dark ? _accentDark : _accentLight;

            var res = Application.Current.Resources;
            foreach (var pair in dark ? Dark : Light)
                SetBrush(res, pair.Key, Parse(pair.Value));

            SetBrush(res, "Accent", Accent);
            SetBrush(res, "AccentSoft", Color.FromArgb(dark ? (byte)0x40 : (byte)0x24, Accent.R, Accent.G, Accent.B));
            SetBrush(res, "AccentFaint", Color.FromArgb(dark ? (byte)0x22 : (byte)0x12, Accent.R, Accent.G, Accent.B));
            SetBrush(res, "SelectionText", Color.FromArgb(0x66, Accent.R, Accent.G, Accent.B));
            double luminance = (0.2126 * Accent.R + 0.7152 * Accent.G + 0.0722 * Accent.B) / 255.0;
            SetBrush(res, "OnAccent", luminance > 0.55 ? Colors.Black : Colors.White);
            res["AccentColor"] = Accent;

            foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
            Changed?.Invoke();
        }

        static void SetBrush(ResourceDictionary res, string key, Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            res[key] = b;
        }

        public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

        public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

        // --- Native title bar ------------------------------------------------------

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWA_CAPTION_COLOR = 35;
        const int DWMWA_TEXT_COLOR = 36;

        public static void ApplyTitleBar(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int dark = IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);

                // Windows 11 lets us paint the caption exactly the window colour. Windows 10 ignores these.
                var bg = Parse((IsDark ? Dark : Light)["Bg"]);
                var fg = Parse((IsDark ? Dark : Light)["Text"]);
                var border = Parse((IsDark ? Dark : Light)["Border"]);
                int caption = ToColorRef(bg), text = ToColorRef(fg), edge = ToColorRef(border);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, 4);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, 4);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref edge, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
    }
}
