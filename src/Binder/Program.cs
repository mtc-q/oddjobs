using System;
using OddJobs.UI;

namespace OddJobs.Binder
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Binder, args, a => new MainWindow(a));
    }
}
