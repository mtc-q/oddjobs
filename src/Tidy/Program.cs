using System;
using OddJobs.UI;

namespace OddJobs.Tidy
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Tidy, args, a => new MainWindow(a));
    }
}
