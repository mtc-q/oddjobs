using System;
using OddJobs.UI;

namespace OddJobs.Scrub
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Scrub, args, a => new MainWindow(a));
    }
}
