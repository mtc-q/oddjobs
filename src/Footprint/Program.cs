using System;
using OddJobs.UI;

namespace OddJobs.Footprint
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Footprint, args, a => new MainWindow(a));
    }
}
