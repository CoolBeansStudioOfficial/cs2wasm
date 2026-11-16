using System;
using System.Diagnostics;

static class AotMain
{
    static void Main()
    {
        Bench.RunAll();
        // (the peak memory of the process, for the comparison with the module's memory)
        Console.WriteLine("peak_bytes=" + Process.GetCurrentProcess().PeakWorkingSet64);
    }
}
