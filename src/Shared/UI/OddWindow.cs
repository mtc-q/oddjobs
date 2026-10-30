using System;
using System.Windows;
using System.Windows.Media;
#if SCREENSHOT
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
#endif

namespace OddJobs.UI
{
    /// <summary>Base window: theme colours, crisp text, and a title bar that matches the theme.</summary>
    public class OddWindow : Window
    {
        public OddWindow()
        {
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(ForegroundProperty, "Text");
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SourceInitialized += (s, e) => Theme.ApplyTitleBar(this);
        }

#if SCREENSHOT
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            string path = Environment.GetEnvironmentVariable("ODDJOBS_SHOT");
            if (string.IsNullOrEmpty(path) || this != Application.Current.MainWindow) return;
            int delay = int.TryParse(Environment.GetEnvironmentVariable("ODDJOBS_SHOT_DELAY"), out var d) ? d : 2500;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (s, a) =>
            {
                timer.Stop();
                SaveScreenshot(path);
                Application.Current.Shutdown();
            };
            timer.Start();
        }

        public static void ApplyShotSize(Window w)
        {
            var size = Environment.GetEnvironmentVariable("ODDJOBS_SHOT_SIZE");
            if (string.IsNullOrEmpty(size)) return;
            var parts = size.Split('x');
            w.Width = double.Parse(parts[0]);
            w.Height = double.Parse(parts[1]);
        }

        void SaveScreenshot(string path)
        {
            var root = (FrameworkElement)Content;
            const double scale = 2;
            int w = (int)(root.ActualWidth * scale), h = (int)(root.ActualHeight * scale);
            var bitmap = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bitmap.Render(bg);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var fs = File.Create(path)) encoder.Save(fs);
        }
#endif
    }
}
