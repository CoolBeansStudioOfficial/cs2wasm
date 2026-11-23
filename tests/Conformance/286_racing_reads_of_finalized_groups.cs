// Threads (section 4.5): test 285 with a cycle-capable class: eight threads replace, with no lock, the elements of
// an array a static holds, each a pair of objects that refer to each other (a group), and read them; the pair's class
// has a finalizer, which now and then stores its object where the program can reach it again (another array a static
// holds, which the threads also read and clear). A data race, which C# allows: every object is finalized exactly once,
// a brought-back one not again when it goes for good, and nothing a place holds is freed. A group's member runs its
// finalizer under a bias on its count (RuntimeGen.EmitDestroy), while other threads count it and give their counts
// back by exchanges; the bias was added and taken off by a read and a write, which lost such a count now and then:
// the check after the finalizers then took the group for garbage while a thread held one of its members, which it
// later let go of in memory that was another object's by then, and a read spun on a place that held a freed object
// (hangs: with CS2WASM_NO_FIN_GROUP_ATOMIC=1, the read and the write, 14 of 80 loaded runs; 0 of 200 with the fix).
using System;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Pair
{
    public static int Made, Finalized;
    public static readonly Pair?[] s_back = new Pair?[4];
    public int V;
    public Pair? Other;
    public Pair(int v) { V = v; Interlocked.Increment(ref Made); }
    ~Pair()
    {
        int w = 0;
        for (int i = 0; i < 32; i++) w += (i ^ V) & 1;
        if (w < 0) Console.WriteLine("never");
        Interlocked.Increment(ref Finalized);
        // (one in eight brought back: a place another thread reads and replaces)
        if ((V & 7) == 3) s_back[V & 3] = this;
    }
}

static class Program
{
    static readonly Pair?[] s_pairs = new Pair?[4];

    static long Work(int t)
    {
        long sum = 0;
        for (int i = 0; i < 20000; i++)
        {
            var p = s_pairs[(i + t) & 3];
            if (p != null) sum += p.V & 1;
            var b = Pair.s_back[(i + 2 * t) & 3];
            if (b != null) sum += (b.Other?.V ?? 0) & 1;
            if ((i & 15) == t) Pair.s_back[(i + t) & 3] = null;
            var a = new Pair(i * 4 + t);
            var c = new Pair(i * 4 + t + 1);
            a.Other = c; c.Other = a;
            s_pairs[(i * 5 + t) & 3] = a;
        }
        return sum >= 0 ? 1 : 0;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        for (int round = 0; round < 12; round++)
        {
            var results = new long[8];
            var threads = new Thread[8];
            for (int t = 0; t < threads.Length; t++)
            {
                int k = t;
                threads[t] = new Thread(() => results[k] = Work(k));
                threads[t].Start();
            }
            foreach (var th in threads) th.Join();
            Console.WriteLine("round " + round + ": " + (results[0] + results[1] + results[2] + results[3] + results[4] + results[5] + results[6] + results[7]));
        }
        for (int i = 0; i < s_pairs.Length; i++) s_pairs[i] = null;
        for (int i = 0; i < Pair.s_back.Length; i++) Pair.s_back[i] = null;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        for (int i = 0; i < Pair.s_back.Length; i++) Pair.s_back[i] = null;
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine("made " + Pair.Made + ", finalized " + Pair.Finalized);
    }
}
