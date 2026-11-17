// Array.Clear, List.Clear, Span.Clear and RemoveRange let go of what the elements referred to (section 4: an object
// is destroyed when its last reference goes, and a cleared element is a reference gone).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class P
{
    public static int Live;
    public P() { Live++; }
    ~P() { Live--; }
}

sealed class N
{
    public static int Live;
    public N? Self;
    public N() { Live++; Self = this; }
    ~N() { Live--; }
}

struct Pair { public P? A; public int X; public P? B; }

static class Program
{
    static readonly List<P> s_p = new();
    static readonly List<N> s_n = new();
    static readonly Dictionary<int, P> s_d = new();

    static void Round()
    {
        for (int i = 0; i < 5; i++) s_p.Add(new P());
        s_p.Clear();
        var a = new P[5];
        for (int i = 0; i < 5; i++) a[i] = new P();
        Array.Clear(a);
        for (int i = 0; i < 5; i++) a[i] = new P();
        Array.Clear(a, 1, 3);
        a.AsSpan().Clear();
        var pairs = new Pair[4];
        for (int i = 0; i < 4; i++) pairs[i] = new Pair { A = new P(), X = i, B = new P() };
        Array.Clear(pairs, 0, 2);
        pairs.AsSpan(2).Clear();
        var grid = new P[2, 3];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) grid[i, j] = new P();
        Array.Clear(grid);
        if (grid.GetLength(0) != 2 || grid.GetLength(1) != 3 || grid[1, 2] != null) Console.WriteLine("wrong grid");
        for (int i = 0; i < 5; i++) s_d[i] = new P();
        s_d.Clear();
        for (int i = 0; i < 5; i++) s_n.Add(new N());
        s_n.RemoveRange(1, 3);
        s_n.Clear();
        var b = new N[5];
        for (int i = 0; i < 5; i++) b[i] = new N();
        Array.Clear(b);
        for (int i = 0; i < 5; i++) b[i] = new N();
        b.AsSpan().Clear();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        for (int round = 0; round < 300; round++) Round();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        // (.NET may not have collected the last round yet)
        Console.WriteLine(P.Live <= 34 ? "gone" : "left: " + P.Live);
        Console.WriteLine(N.Live <= 15 ? "gone" : "left: " + N.Live);
    }
}
