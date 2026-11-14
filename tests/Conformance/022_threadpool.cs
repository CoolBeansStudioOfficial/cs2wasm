using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static async Task<int> SumAsync(int n)
    {
        int a = await Task.Run(() => Enumerable.Range(1, n).Sum());
        int b = await Task.Run(async () => { await Task.Delay(5); return n; });
        return a + b;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // Task.Run results and exceptions
        var t = Task.Run(() => 6 * 7);
        Console.WriteLine(t.Result);
        var bad = Task.Run(() => { throw new InvalidOperationException("boom"); });
        try { bad.Wait(); } catch (AggregateException e) { Console.WriteLine(e.InnerException.GetType().Name + " " + e.InnerException.Message); }
        Console.WriteLine(bad.IsFaulted);

        // many tasks, WhenAll
        var tasks = Enumerable.Range(0, 32).Select(i => Task.Run(() => i * i)).ToArray();
        Console.WriteLine(Task.WhenAll(tasks).Result.Sum());

        // async over the pool
        Console.WriteLine(SumAsync(100).Result);

        // Parallel.For with Interlocked and a lock
        long total = 0;
        var seen = new HashSet<int>();
        Parallel.For(0, 1000, i =>
        {
            Interlocked.Add(ref total, i);
            lock (seen) seen.Add(i);
        });
        Console.WriteLine(total + " " + seen.Count);
        var squares = new int[100];
        Parallel.ForEach(Enumerable.Range(0, 100), i => squares[i] = i * i);
        Console.WriteLine(squares.Sum());
        try { Parallel.For(0, 10, i => { if (i == 3) throw new ArgumentException("three"); }); }
        catch (AggregateException e) { Console.WriteLine(e.InnerException.Message); }
        int a = 0, b = 0;
        Parallel.Invoke(() => a = 1, () => b = 2);
        Console.WriteLine(a + b);

        // ThreadLocal, SemaphoreSlim, CountdownEvent
        var tl = new ThreadLocal<int>(() => 5);
        tl.Value = 9;
        int other = Task.Run(() => tl.Value).Result;
        Console.WriteLine(tl.Value + " " + other);
        var sem = new SemaphoreSlim(2);
        int inside = 0, maxInside = 0;
        var gate = new object();
        var work = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            sem.Wait();
            lock (gate) { inside++; if (inside > maxInside) maxInside = inside; }
            Thread.Sleep(2);
            lock (gate) inside--;
            sem.Release();
        })).ToArray();
        Task.WaitAll(work);
        Console.WriteLine((maxInside <= 2) + " " + sem.CurrentCount);
        var cd = new CountdownEvent(4);
        for (int i = 0; i < 4; i++) ThreadPool.QueueUserWorkItem(_ => cd.Signal());
        cd.Wait();
        Console.WriteLine(cd.IsSet);
        Console.WriteLine(Task.Run(() => Thread.CurrentThread.IsThreadPoolThread).Result);
    }
}
