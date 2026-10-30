using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OddJobs.UI
{
    /// <summary>
    /// Shared application bootstrap: theme, crash handling, and the main window.
    /// Every app's Program.Main is one line that calls <see cref="Run"/>.
    /// </summary>
    public sealed class OddApp : Application
    {
        public static AppInfo Info { get; private set; }

        readonly Func<string[], Window> _createMainWindow;

        OddApp(Func<string[], Window> createMainWindow)
        {
            _createMainWindow = createMainWindow;
        }

        public static int Run(AppInfo info, string[] args, Func<string[], Window> createMainWindow)
        {
            Info = info;
            var app = new OddApp(createMainWindow);
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/" + typeof(OddApp).Assembly.GetName().Name + ";component/Shared/UI/Theme.xaml", UriKind.Relative),
            });
            app.DispatcherUnhandledException += app.OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += (s, e) => e.SetObserved();

            var light = Theme.Parse(info.Color);
            Theme.Init(light, Lighten(light, 0.22));
            return app.Run(app.CreateMain(args));
        }

        Window CreateMain(string[] args)
        {
            var window = _createMainWindow(args);
            MainWindow = window;
            return window;
        }

        void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Keep the app alive and tell the user what happened, rather than vanishing.
            e.Handled = true;
            try
            {
                Dialog.Error(MainWindow, "Something went wrong",
                    "The last action did not finish. Your files were not changed by the part that failed.",
                    e.Exception.GetType().Name + ": " + e.Exception.Message);
            }
            catch
            {
                MessageBox.Show(e.Exception.Message, Info?.Name ?? "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        static Color Lighten(Color c, double amount)
        {
            byte L(byte v) => (byte)Math.Round(v + (255 - v) * amount);
            return Color.FromRgb(L(c.R), L(c.G), L(c.B));
        }
    }
}
