using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static readonly AsyncLocal<string> s_user = new AsyncLocal<string>();
    static readonly AsyncLocal<int> s_depth = new AsyncLocal<int>();

    static async Task Inner(string tag)
    {
        Console.WriteLine(tag + " sees " + s_user.Value + " " + s_depth.Value);
        s_user.Value = "changed-by-" + tag;
        s_depth.Value++;
        await Task.Yield();
        Console.WriteLine(tag + " after await " + s_user.Value + " " + s_depth.Value);
    }

    static async Task Outer()
    {
        s_user.Value = "alice";
        s_depth.Value = 1;
        await Inner("inner");
        Console.WriteLine("outer keeps " + s_user.Value + " " + s_depth.Value);
        var t = Task.Run(() => s_user.Value + "@pool");
        Console.WriteLine(await t);
        string seen = null;
        var th = new Thread(() => seen = s_user.Value + "@thread");
        th.Start(); th.Join();
        Console.WriteLine(seen);
    }

    static class Slow
    {
        public static readonly int Value;
        public static int Runs;
        static Slow()
        {
            Interlocked.Increment(ref Runs);
            Thread.Sleep(20);
            Value = 42;
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Outer().Wait();
        Console.WriteLine("main " + (s_user.Value ?? "null") + " " + s_depth.Value);
        var results = new int[4];
        var ts = new List<Thread>();
        for (int i = 0; i < 4; i++) { int k = i; var t = new Thread(() => results[k] = Slow.Value); ts.Add(t); t.Start(); }
        foreach (var t in ts) t.Join();
        Console.WriteLine(string.Join(",", results) + " runs " + Slow.Runs);
    }
}
