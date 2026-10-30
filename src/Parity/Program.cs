using System;
using OddJobs.UI;

namespace OddJobs.Parity
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Parity, args, a => new MainWindow(a));
    }
}
