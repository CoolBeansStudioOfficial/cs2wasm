// An ordered PLINQ query over many items, then one over a few, as the compiler parses its library's files
// (Library.BuildOne): the self-hosted compiler waited forever in the second on 12 threads or more.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

static class Program
{
    static string Work(string s)
    {
        var sb = new StringBuilder();
        int h = 0;
        for (int k = 0; k < 200; k++) { h = h * 31 + s[k % s.Length]; if (k % 50 == 0) sb.Append((char)('a' + (h & 15))); }
        return sb.ToString();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long total = 0;
        for (int round = 0; round < 6; round++)
        {
            var files = Enumerable.Range(0, round % 2 == 0 ? 3000 : 7).Select(i => "file" + i + "_" + round).ToList();
            var outs = files.AsParallel().AsOrdered().Select(Work).ToList();
            total += outs.Count + outs[outs.Count - 1].Length;
            var few = files.Take(3).AsParallel().AsOrdered().Select(f => Work(f + "x")).ToList();
            total += few.Count;
        }
        Console.WriteLine(total);
    }
}
