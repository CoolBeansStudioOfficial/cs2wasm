// Pool threads that queue work onto their own queues and then block until it is done: only other workers can run it,
// by stealing it from those queues (as PLINQ's tasks do, started from a pool thread that then waits for its producers).
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

static class Program
{
    static long Work(int n)
    {
        long h = n;
        for (int k = 0; k < 2000; k++) h = h * 31 + k;
        return h & 1;
    }

    static long Parent(int p)
    {
        const int Kids = 16;
        int left = Kids;
        long sum = 0;
        var gate = new object();
        for (int i = 0; i < Kids; i++)
        {
            int k = i;
            // (queued from a pool thread: onto this thread's own queue)
            ThreadPool.UnsafeQueueUserWorkItem<object?>(_ =>
            {
                long r = Work(p * 100 + k);
                lock (gate) { sum += r; if (--left == 0) Monitor.PulseAll(gate); }
            }, null, preferLocal: true);
        }
        // (a wait no task can be run inline in: the children must be stolen)
        lock (gate) { while (left > 0) Monitor.Wait(gate); }
        return sum;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        long total = 0;
        for (int round = 0; round < 3; round++)
        {
            var parents = new Task<long>[Math.Max(Environment.ProcessorCount, 10) + 4];
            for (int p = 0; p < parents.Length; p++) { int q = p; parents[p] = Task.Run(() => Parent(q)); }
            foreach (var t in parents) total += t.Result >= 0 ? 1 : 0;
        }
        Console.WriteLine(total > 0 ? "done" : "none");
    }
}
