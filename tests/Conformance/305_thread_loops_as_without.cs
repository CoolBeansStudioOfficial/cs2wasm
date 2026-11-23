// Threads (section 4.5): a loop of a program with threads, over objects no other thread can reach, is compiled as
// without threads: the counts' common case written out in place (RcFastPaths.cs, on the plain counts) and the
// fields of such objects read once by the loop (FieldLoads.cs, Sharing.FieldMayBeShared). Objects another thread
// can reach keep their atomic counts as calls and every read of their fields.
// - Each worker and the main thread fill a list of their own, pass over it, store its elements back (each store a
//   plain count of the old element and the new one, written out), and drop it.
// - Every thread stores the one Gem a static holds into an array of its own, again and again: two threads count
//   it at once. CS2WASM_EXP_FAST_ATOMIC=1 writes those atomic counts out as plain ones: counts are lost, the Gem
//   goes while referenced, and its value (or the program) is no longer what it was.
// - The main thread waits in a loop (a full fence each time round) for a flag another thread sets, under a lock,
//   in an object both reach.
//   CS2WASM_FIELD_HOIST_WITHOUT=threads reads the flag once before the loop: the loop never ends.
// Whatever the interleaving, the output is the same (tools/thread-escape-breaks.sh).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Bead { public int V; public float X; public string Tag = ""; }
sealed class Gem { public int V; public string Name = ""; }
sealed class Flag { public bool Done; public int N; }

static class Program
{
    static Gem s_gem = new Gem { V = 12345, Name = "gem" };
    static readonly object s_gate = new();
    static Flag s_flag = new();

    static long Private(int t)
    {
        var tags = "a bb ccc dddd".Split(' ');
        var list = new List<Bead>();
        long sum = 0;
        for (int round = 0; round < 20; round++)
        {
            for (int i = 0; i < 2000; i++) list.Add(new Bead { V = i + t, Tag = tags[i & 3] });
            for (int k = 0; k < 4; k++)
            {
                int w = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var b = list[i];
                    b.X += 0.5f;
                    if ((b.V + k) % 7 != 0) { list[w++] = b; sum += b.Tag.Length + b.V; }
                }
                list.RemoveRange(w, list.Count - w);
            }
            list.RemoveRange(0, list.Count / 2);
        }
        return sum + list.Count;
    }

    static long Shared()
    {
        var mine = new Gem[8];
        long sum = 0;
        for (int i = 0; i < 400000; i++)
        {
            mine[i & 7] = s_gem;
            sum += mine[(i + 3) & 7]?.V ?? 0;
        }
        for (int i = 0; i < mine.Length; i++) mine[i] = null!;
        return sum;
    }

    static void Run()
    {
        var sums = new long[3];
        var threads = new Thread[3];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => sums[k] = Private(k + 1) + Shared());
            threads[t].Start();
        }
        long own = Private(0) + Shared();
        foreach (var th in threads) th.Join();
        long total = own;
        foreach (var s in sums) total += s;
        // (churn the heap: a Gem let go too soon is another object's memory by now)
        var junk = new List<Gem>();
        for (int i = 0; i < 1000; i++) junk.Add(new Gem { V = -i, Name = "j" + i });
        Console.WriteLine(total + " " + s_gem.V + " " + s_gem.Name + " " + junk.Count);

        var setter = new Thread(() => { Thread.Sleep(20); lock (s_gate) { s_flag.N = 7; s_flag.Done = true; } });
        setter.Start();
        var f = s_flag;
        long spins = 0;
        while (!f.Done) { Interlocked.MemoryBarrier(); spins++; }
        setter.Join();
        Console.WriteLine("flag " + f.N + " " + (spins >= 0));
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        Console.WriteLine("done");
    }
}
