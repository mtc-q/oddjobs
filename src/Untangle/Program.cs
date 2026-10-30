using System;
using OddJobs.UI;

namespace OddJobs.Untangle
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Untangle, args, a => new MainWindow(a));
    }
}
