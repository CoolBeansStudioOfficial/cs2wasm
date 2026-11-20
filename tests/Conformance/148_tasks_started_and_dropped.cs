// Tasks a pool thread starts and keeps no reference to, as PLINQ starts its producers (StartNew from a root task,
// attached to it, preferring fairness: onto the pool's global queue, which alone holds them until they run): every
// one runs. (The self-hosted compiler's parse on 12 threads waited forever for producers that never ran.)
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

sealed class Group
{
    public Work?[] Works = new Work?[32];
}

sealed class Work
{
    public static int Done;
    public int Index;
    // (each work refers to its group and the group to it: a cycle, as PLINQ's tasks and their query's state)
    public Group Group;
    public Work(int i, Group g) { Index = i; Group = g; g.Works[i] = this; }
}

static class Program
{
    static readonly Action<object?> s_run = o =>
    {
        var w = (Work)o!;
        long h = w.Index;
        for (int k = 0; k < (w.Index % 7) * 20000; k++) h = h * 31 + k;
        if (h == 42) Console.WriteLine("?");
        Interlocked.Increment(ref Work.Done);
    };

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int rounds = 60, missing = 0;
        for (int round = 0; round < rounds; round++)
        {
            Work.Done = 0;
            int n = 12 + round % 13;
            var group = new Group();
            var root = new Task(() =>
            {
                for (int i = 0; i < n; i++)
                    Task.Factory.StartNew(s_run, new Work(i, group), CancellationToken.None,
                        TaskCreationOptions.AttachedToParent | TaskCreationOptions.PreferFairness, TaskScheduler.Default);
            });
            root.Start(TaskScheduler.Default);
            root.Wait();
            // (attached children: the root completes with them)
            missing += n - Volatile.Read(ref Work.Done);
            GC.KeepAlive(group);
        }
        Console.WriteLine("missing " + missing);
    }
}
