using System;
using OddJobs.UI;

namespace OddJobs.Lockbox
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Lockbox, args, a => new MainWindow(a));
    }
}
