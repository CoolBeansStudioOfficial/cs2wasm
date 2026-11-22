// An instance field read in a loop, where the loop (or what it calls) stores the field meanwhile: the read must see
// every store (Wasm/FieldLoads.cs reads a field once before a loop only when nothing the loop runs can store it).
// The loop's own stores (plain, compound, increment), a store through another reference to the same object, a
// callee's store, a setter and a method small enough to be inlined into the loop, an auto-property, the local
// itself assigned in the loop (also under a condition, and through a copy), nested loops that store between the
// inner one's turns, two classes whose getters are the same code (one body in the module), a field of a field, a
// constructor's loop over its own readonly field, a list's size and array changed by the loop; an object that is
// null (the exception at the same turn, after the same output; no exception from a loop that takes no turn); an
// array's length where the loop assigns the array; and loops that store nothing, whose reads may be made once.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Box
{
    public int V, W, X, Y, Z, U, T, Q;
    public Box? Next;
    public int[] Arr = new int[4];
    public int P { get; set; }
    public Box(int v) { V = v; W = v; X = v; Y = v; Z = v; U = v; T = v; Q = v; P = v; }
    public void SetW(int v) { W = v; }
    public int GetW() => W;
    // (they can throw: the compiler leaves them calls)
    [MethodImpl(MethodImplOptions.NoInlining)] public void Bump() { if (X < -1000) throw new InvalidOperationException("low"); X += 5; }
    [MethodImpl(MethodImplOptions.NoInlining)] public void BumpP() { if (P < -1000) throw new InvalidOperationException("low"); P += 7; }
}

// (two classes with a getter of the same code: the module keeps one body for both)
sealed class Left { public int A; public int Get() => A; [MethodImpl(MethodImplOptions.NoInlining)] public int GetFar() => A; }
sealed class Right
{
    public int B;
    public int Get() => B;
    [MethodImpl(MethodImplOptions.NoInlining)] public int GetFar() => B;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Set(int v) { if (v < -1000) throw new ArgumentOutOfRangeException(nameof(v)); B = v; }
}

sealed class Ro
{
    public readonly int R;
    public int Sum;
    public Ro(int n)
    {
        // the constructor reads its own readonly field in a loop that stores it
        for (int i = 0; i < n; i++) { Sum += R; R = i * 3 + 1; Sum += R; }
        Fill(this, n);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Fill(Ro r, int n) { for (int i = 0; i < n; i++) r.Sum += r.R; }
}

static class P
{
    static int Own(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.V; b.V = s + i; }
        for (int i = 0; i < 4; i++) { s += b.V; b.V++; s += b.V; b.V += 3; s += b.V; }
        for (int i = 0; i < 4; i++) { s += b.V; if (i == 2) b.V = -1; }
        return s;
    }
    // (a and b are the same object)
    static int Alias(Box a, Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += a.Y; b.Y = i * 10; s += a.Y; }
        return s;
    }
    static int Callee(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.X; b.Bump(); s += b.X; }
        for (int i = 0; i < 4; i++) { s += b.X; if (i == 3) b.Bump(); }
        return s;
    }
    static int Inlined(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.W; b.SetW(i + 100); s += b.GetW(); }
        for (int i = 0; i < 4; i++) { s += b.GetW(); if (i == 1) b.SetW(7); }
        return s;
    }
    static int Props(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.P; b.P = i + 20; s += b.P; }
        for (int i = 0; i < 4; i++) { s += b.P; b.BumpP(); s += b.P; }
        for (int i = 0; i < 4; i++) { s += b.P; b.P++; }
        return s;
    }
    static int Reassigned(Box a, Box b)
    {
        int s = 0;
        var o = a;
        for (int i = 0; i < 4; i++) { s += o.Z; if (i == 1) o = b; }
        o = a;
        for (int i = 0; i < 4; i++) { var t = o; s += t.Z; o = (i & 1) == 0 ? b : a; }
        // (a copy made before the read on one path only)
        o = a;
        var c = b;
        for (int i = 0; i < 4; i++) { if (i == 2) c = o; s += c.Z; }
        // (a copy that comes after the read: the read sees the last turn's)
        c = b;
        for (int i = 0; i < 4; i++) { s += c.Z * 3; c = i == 1 ? a : b; }
        // (the walk of a chain)
        for (var n = a; n != null; n = n.Next) s += n.Z;
        return s;
    }
    static int Nested(Box b)
    {
        int s = 0;
        for (int o = 0; o < 3; o++)
        {
            for (int i = 0; i < 3; i++) s += b.U;
            b.U += 10;
        }
        for (int o = 0; o < 3; o++)
            for (int i = 0; i < 3; i++) { s += b.U; if (i == 1) b.U++; }
        for (int o = 0; o < 3; o++)
        {
            for (int i = 0; i < 3; i++) s += b.U;
            b.Bump();
            for (int i = 0; i < 2; i++) { s += b.X; }
        }
        return s;
    }
    static int Merged(Left l, Right r)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) s += l.Get() + l.GetFar();
        for (int i = 0; i < 4; i++) { s += r.Get(); r.Set(i + 50); s += r.Get(); }
        for (int i = 0; i < 4; i++) { s += r.GetFar(); r.Set(i + 60); s += r.GetFar(); }
        for (int i = 0; i < 4; i++) { s += r.Get(); r.B = i + 70; s += r.Get(); }
        return s;
    }
    static int Chain(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.Next!.T; b.Next = new Box(i + 30); s += b.Next.T; }
        var keep = b.Next!;
        for (int i = 0; i < 4; i++) { s += b.Next!.T; keep.T += 2; }
        for (int i = 0; i < 4; i++) { s += b.Next!.Arr.Length; if (i == 1) b.Next.Arr = new int[9]; }
        return s;
    }
    static int Lists()
    {
        int s = 0;
        var list = new List<int> { 1, 2, 3 };
        // the list grows in the loop: its size and its array change
        for (int i = 0; i < list.Count; i++) { if (list[i] == 3 || list[i] == 99) list.Add(list[i] == 3 ? 99 : 7); s += list[i]; }
        for (int i = 0; i < 40; i++) { list.Add(i); s += list[0] + list.Count + list.Capacity; }
        // the survivors moved down, the rest cut off after the loop
        int w = 0;
        for (int i = 0; i < list.Count; i++) { int v = list[i]; if ((v & 1) == 0) { list[w++] = v; s += v; } }
        list.RemoveRange(w, list.Count - w);
        for (int i = 0; i < list.Count; i++) { s += list[i]; if (i == 3) list.RemoveAt(0); }
        // a list changed while it is walked: the exception at the turn after
        string r = "";
        try { foreach (var v in list) { r += v + ","; if (v == 12) list.Add(5); } } catch (InvalidOperationException) { r += "changed"; }
        Console.WriteLine("  " + r);
        try { for (int i = 0; i < 100; i++) { s += list[i]; if (i == 2) list.Clear(); } } catch (ArgumentOutOfRangeException) { s += 1000; }
        return s + list.Count;
    }
    static string Nulls(Box? n, int turns)
    {
        string r = "";
        try { for (int i = 0; i < turns; i++) { r += "t" + i; Console.Write(i); if (i == 2) r += n!.V; } }
        catch (NullReferenceException) { r += " null"; }
        try { for (int i = 0; i < turns; i++) { Console.Write((char)('a' + i)); r += n!.V; } }
        catch (NullReferenceException) { r += " first"; }
        // (no turn: no read, no exception)
        for (int i = 0; i < 0; i++) r += n!.V;
        // (the condition reads it: the exception before the first turn)
        try { for (int i = 0; i < n!.V; i++) r += "never"; } catch (NullReferenceException) { r += " cond"; }
        // (null only from some turn on)
        var o = new Box(1);
        try { for (int i = 0; i < turns; i++) { r += o!.V; if (i == 1) o = n; } } catch (NullReferenceException) { r += " later"; }
        Console.WriteLine();
        return r;
    }
    static string Lengths(int[] a, int[]? none)
    {
        string r = "";
        int s = 0;
        for (int i = 0; i < 6; i++) { s += a.Length; if (i == 2) a = new int[7]; }
        r += s;
        try { for (int i = 0; i <= a.Length; i++) { Console.Write(a[i] + i); } } catch (IndexOutOfRangeException) { r += " range"; }
        try { for (int i = 0; i < 3; i++) { Console.Write("n" + i); if (i == 1) s += none!.Length; } } catch (NullReferenceException) { r += " null"; }
        for (int i = 0; i < 0; i++) s += none!.Length;
        var jag = new int[][] { new int[2], new int[5], new int[1] };
        for (int i = 0; i < jag.Length; i++) for (int k = 0; k < jag[i].Length; k++) s += jag.Length + jag[i].Length;
        Array.Resize(ref a, 11);
        for (int i = 0; i < 3; i++) { s += a.Length; if (i == 0) Array.Resize(ref a, 3); }
        Console.WriteLine();
        return r + " " + s;
    }
    // loops that store nothing the loop reads: every read may be made once
    static int Quiet(Box b, Left l, Right r, List<Box> boxes)
    {
        int s = 0;
        for (int i = 0; i < 5; i++) s += b.V + b.W + b.P + b.Arr.Length + b.Next!.T;
        for (int i = 0; i < 5; i++) { s += b.V; b.W = i; b.Arr[i & 3] = i; }
        for (int i = 0; i < 5; i++) { s += b.V; r.Set(i); s += l.Get(); }
        for (int i = 0; i < boxes.Count; i++) { var x = boxes[i]; x.V += b.Q; s += x.V + boxes.Count; }
        int w = 0;
        for (int i = 0; i < boxes.Count; i++) { var x = boxes[i]; if ((x.V & 1) == 0) boxes[w++] = x; }
        boxes.RemoveRange(w, boxes.Count - w);
        return s + boxes.Count + b.W + b.Arr[1];
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var b = new Box(3) { Next = new Box(4) { Next = new Box(5) } };
        Console.WriteLine("own " + Own(b));
        Console.WriteLine("alias " + Alias(b, b));
        Console.WriteLine("callee " + Callee(b));
        Console.WriteLine("inlined " + Inlined(b));
        Console.WriteLine("props " + Props(b));
        Console.WriteLine("reassigned " + Reassigned(b, new Box(40)));
        Console.WriteLine("nested " + Nested(b));
        Console.WriteLine("merged " + Merged(new Left { A = 2 }, new Right { B = 9 }));
        Console.WriteLine("chain " + Chain(b));
        var ro = new Ro(4);
        Console.WriteLine("readonly " + ro.Sum + " " + ro.R);
        Console.WriteLine("lists " + Lists());
        Console.WriteLine("nulls " + Nulls(null, 4));
        Console.WriteLine("lengths " + Lengths(new int[3], null));
        Console.WriteLine("quiet " + Quiet(b, new Left { A = 1 }, new Right(), new List<Box> { new Box(1), new Box(2), new Box(3), new Box(4) }));
    }
}
