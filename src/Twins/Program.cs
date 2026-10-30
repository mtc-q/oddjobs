using System;
using OddJobs.UI;

namespace OddJobs.Twins
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Twins, args, a => new MainWindow(a));
    }
}
