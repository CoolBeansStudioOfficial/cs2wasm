using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

static class Program
{
    static int s_counter;
    static long s_atomic;
    static readonly object s_gate = new object();
    [ThreadStatic] static int t_local;
    static readonly Queue<int> s_queue = new Queue<int>();

    sealed class Box { public string Text; public Box(string t) { Text = t; } }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // locked and interlocked counters from four threads
        var threads = new List<Thread>();
        for (int i = 0; i < 4; i++)
        {
            var t = new Thread(() =>
            {
                for (int k = 0; k < 2000; k++)
                {
                    lock (s_gate) s_counter++;
                    Interlocked.Increment(ref s_atomic);
                }
            });
            threads.Add(t);
            t.Start();
        }
        foreach (var t in threads) t.Join();
        Console.WriteLine(s_counter + " " + Interlocked.Read(ref s_atomic));

        // thread-local state
        t_local = 7;
        int seen = -1;
        var tl = new Thread(() => { seen = t_local; t_local = 99; });
        tl.Start(); tl.Join();
        Console.WriteLine(seen + " " + t_local);

        // objects shared across threads: counts stay right
        var shared = new Box("shared");
        var results = new string[3];
        var ws = new Thread[3];
        for (int i = 0; i < 3; i++)
        {
            int n = i;
            ws[i] = new Thread(() =>
            {
                for (int k = 0; k < 500; k++) { var local = shared; results[n] = local.Text + n; }
            });
            ws[i].Start();
        }
        foreach (var w in ws) w.Join();
        Console.WriteLine(string.Join(",", results) + " " + shared.Text);

        // producer / consumer with Monitor.Wait and Pulse
        int total = 0;
        var consumer = new Thread(() =>
        {
            for (int got = 0; got < 50; got++)
            {
                int v;
                lock (s_queue)
                {
                    while (s_queue.Count == 0) Monitor.Wait(s_queue);
                    v = s_queue.Dequeue();
                }
                total += v;
            }
        });
        consumer.Start();
        for (int i = 1; i <= 50; i++) lock (s_queue) { s_queue.Enqueue(i); Monitor.Pulse(s_queue); }
        consumer.Join();
        Console.WriteLine(total);

        // parameterized start, ids, IsAlive
        object got2 = null;
        var p = new Thread(o => got2 = o);
        Console.WriteLine(p.IsAlive + " " + (p.ManagedThreadId > 1) + " " + (Thread.CurrentThread.ManagedThreadId != p.ManagedThreadId));
        p.Start("param"); p.Join();
        Console.WriteLine(got2 + " " + p.IsAlive);
        try { p.Start(); } catch (ThreadStateException) { Console.WriteLine("restart refused"); }
        Console.WriteLine(Interlocked.CompareExchange(ref s_counter, 1, 8000) + " " + s_counter);
    }
}
