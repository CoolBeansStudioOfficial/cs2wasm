// GC.CollectionCount after GC.Collect: a collection counts for its own generation and every younger one (GC.Collect(n)
// raises CollectionCount(g) for every g <= n, and a plain GC.Collect() for all of them). il2wasm has no collector, but
// the count keeps .NET's meaning (SPEC 4.4). .NET may also collect on its own (a background GC), so every check is a
// delta of at least 1 or an ordering, never an exact count.
using System;

static class Program
{
    static bool Rose(int before, int after) => after - before >= 1;

    static void Main()
    {
        int a0 = GC.CollectionCount(0), a1 = GC.CollectionCount(1), a2 = GC.CollectionCount(2);
        Console.WriteLine("before: ordered " + (a0 >= a1 && a1 >= a2));

        GC.Collect();
        int b0 = GC.CollectionCount(0), b1 = GC.CollectionCount(1), b2 = GC.CollectionCount(2);
        Console.WriteLine("after Collect(): g0 " + Rose(a0, b0) + ", g1 " + Rose(a1, b1) + ", g2 " + Rose(a2, b2) + ", ordered " + (b0 >= b1 && b1 >= b2));

        GC.Collect(0);
        int c0 = GC.CollectionCount(0), c1 = GC.CollectionCount(1), c2 = GC.CollectionCount(2);
        Console.WriteLine("after Collect(0): g0 " + Rose(b0, c0) + ", ordered " + (c0 >= c1 && c1 >= c2));

        GC.Collect(1);
        int d0 = GC.CollectionCount(0), d1 = GC.CollectionCount(1), d2 = GC.CollectionCount(2);
        Console.WriteLine("after Collect(1): g0 " + Rose(c0, d0) + ", g1 " + Rose(c1, d1) + ", g2 unchanged-or-more " + (d2 >= c2) + ", ordered " + (d0 >= d1 && d1 >= d2));

        long index = GC.GetGCMemoryInfo().Index;
        GC.Collect();
        Console.WriteLine("GetGCMemoryInfo().Index rose: " + (GC.GetGCMemoryInfo().Index - index >= 1));

        int e2 = GC.CollectionCount(2);
        GC.GetTotalMemory(true);
        Console.WriteLine("after GetTotalMemory(true): g2 " + Rose(e2, GC.CollectionCount(2)));

        int f0 = GC.CollectionCount(0);
        GC.WaitForPendingFinalizers();
        Console.WriteLine("WaitForPendingFinalizers keeps g0 " + (GC.CollectionCount(0) >= f0));

        try { GC.CollectionCount(-1); Console.WriteLine("negative: no exception"); }
        catch (ArgumentOutOfRangeException) { Console.WriteLine("negative: ArgumentOutOfRangeException"); }
    }
}
