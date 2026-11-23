// Threads (section 4.5): the one write of a static readonly field, its initializer's, fixes the object it stores, and
// an array with no element so stored is immortal (Rt.ArenaPinFixed): List<T>'s s_emptyArray, which every list starts
// with, is no longer counted by every thread at every list it makes and lets go. Workers each make lists of Cells of
// their own (a cycle-capable class: a cell has a list of cells and a parent) by the thousand, all starting with that
// array, and an array with no element of the program's own (Cell.None); the main thread does the same. Whatever the
// interleaving, the sums are the same. A static that is not readonly keeps its initializer's object pinned, not
// fixed: when the program lets go of it, it goes, and its Probe's finalizer runs (GC.WaitForPendingFinalizers in
// .NET). CS2WASM_EXP_FIXED_PINS_ANY=1 (a test of the tests) fixes that one too: it never goes, and the count of
// finalized probes differs (tools/thread-escape-breaks.sh).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Probe
{
    public static int s_gone;
    ~Probe() { Interlocked.Increment(ref s_gone); }
}

sealed class Cell
{
    public static readonly Cell[] None = new Cell[0];
    public int V;
    public Cell? Parent;
    public List<Cell> Kids = new();
    public Cell[] More = None;
    public Probe? P;
}

static class Program
{
    static Cell? s_first = new Cell { V = 7, P = new Probe() };

    static long Work(int t)
    {
        long sum = 0;
        for (int round = 0; round < 200; round++)
        {
            var root = new Cell { V = t };
            for (int i = 0; i < 50; i++)
            {
                var c = new Cell { V = i, Parent = root };
                root.Kids.Add(c);
                if ((i & 3) == 0) c.More = new Cell[] { root };
                sum += c.Kids.Count + c.More.Length + Cell.None.Length;
            }
            root.Kids.RemoveRange(10, 20);
            foreach (var k in root.Kids) sum += k.V + (k.Parent == root ? 1 : 0);
        }
        return sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LetGoFirst() { s_first = null; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int FirstV() => s_first!.V;

    static void Run()
    {
        var sums = new long[4];
        var threads = new Thread[4];
        for (int t = 0; t < threads.Length; t++)
        {
            int k = t;
            threads[t] = new Thread(() => sums[k] = Work(k + 1));
            threads[t].Start();
        }
        long total = Work(0);
        foreach (var th in threads) th.Join();
        foreach (var s in sums) total += s;
        Console.WriteLine(total + " " + Cell.None.Length + " " + FirstV());
        LetGoFirst();
        for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Console.WriteLine("probes finalized: " + Probe.s_gone);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Run();
        Console.WriteLine("done");
    }
}
