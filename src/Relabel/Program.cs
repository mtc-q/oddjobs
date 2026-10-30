using System;
using OddJobs.UI;

namespace OddJobs.Relabel
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args) => OddApp.Run(Brand.Relabel, args, a => new MainWindow(a));
    }
}
