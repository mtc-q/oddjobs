using System;
using OddJobs.UI;

namespace OddJobs.Verity
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Verity, args, a => new MainWindow(a));
    }
}
