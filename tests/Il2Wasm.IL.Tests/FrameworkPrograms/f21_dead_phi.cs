// A conditional whose value nothing uses once a constant branch is gone (a phi of the two arms' objects, dead): its
// count goes at once. The naive plan kept it, an object leaked on every pass and still alive at the end (NetWasm's
// SortedSet<T>.Enumerator.MoveNext has the shape).
using System;
using System.Runtime.CompilerServices;

sealed class Node { public int V; }

static class Program
{
    static WeakReference? s_w;
    static readonly bool s_never = false;
    [MethodImpl(MethodImplOptions.NoInlining)] static Node Make(int v) { var n = new Node { V = v }; if (v == 7) s_w = new WeakReference(n); return n; }

    static void Main()
    {
        int n = 0;
        for (int i = 0; i < 20; i++) { var other = (i & 1) == 0 ? Make(i) : Make(i * 2 + 1); n++; if (s_never) n += other.V; }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(n + " " + s_w!.IsAlive);
    }
}
