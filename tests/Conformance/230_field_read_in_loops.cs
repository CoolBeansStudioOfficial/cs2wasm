// An instance field read in a loop, where the loop (or what it calls) stores the field meanwhile: the read must see
// every store (Wasm/FieldLoads.cs reads a field once before a loop only when nothing the loop runs can store it).
// The loop's own stores (plain, compound, increment), a store through another reference to the same object, a
// callee's store, a setter and a method small enough to be inlined into the loop, an auto-property, the local
// itself assigned in the loop (also under a condition, and through a copy), nested loops that store between the
// inner one's turns, two classes whose getters are the same code (one body in the module), a field of a field, a
// constructor's loop over its own readonly field, a list's size and array changed by the loop; an object that is
// null (the exception at the same turn, after the same output; no exception from a loop that takes no turn); an
// array's length where the loop assigns the array; loops whose test counts, stores or calls (the pass runs a loop's
// test once more before the loop, to read behind it what the turns read: only a test that can be run twice), loops
// that take no turn and loops left at once; a field of a field of a null (what a read from null gets is no
// reference: nothing is read through it before the loop); objects constructed in a loop that reads a readonly field of an
// older one; two reads in a straight line with a store between them (by the code itself, a callee, another
// reference, on one path only, a handler, a loop); and loops that store nothing, whose reads may be made once.
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

// (objects made inside a loop that reads a readonly field of one made before it: the constructors store that
// field, of the new objects; an init-only property set by an object initializer in the loop)
sealed class Made
{
    public readonly int K;
    public readonly Made? Prev;
    public int Count;
    public int Init { get; init; }
    public Made(int k, Made? prev)
    {
        K = k; Prev = prev;
        if (prev != null) for (int i = 0; i < 3; i++) { Count += prev.K + K; if (i == 1) K++; }
    }
}

// (an object no other refers to: a variable of it is assigned by a plain copy)
sealed class Flat { public int Z; }

// (references at many offsets: the memory after address 0 holds the program's data, and a loop that takes no turn,
// or throws at its first read, reads nothing through what a null's field "holds")
sealed class Link
{
    public Link? L0, L1, L2, L3, L4, L5, L6, L7, L8, L9, L10, L11;
    public int[]? Arr;
    public int T;
}

static class P
{
    // (one loop a call: the first that throws would hide the others)
    static string NullChains(Link? n, int turns, int which)
    {
        int s = 0;
        try
        {
            if (which == 0) for (int i = 0; i < turns; i++) s += n!.L0!.T + n.L0.L0!.T;
            if (which == 1) for (int i = 0; i < turns; i++) s += n!.L1!.T;
            if (which == 2) for (int i = 0; i < turns; i++) s += n!.L2!.T + n.L2.Arr!.Length;
            if (which == 3) for (int i = 0; i < turns; i++) s += n!.L3!.T;
            if (which == 4) for (int i = 0; i < turns; i++) s += n!.L4!.L5!.L6!.T;
            if (which == 5) for (int i = 0; i < turns; i++) s += n!.L5!.T;
            if (which == 6) for (int i = 0; i < turns; i++) s += n!.L6!.Arr!.Length;
            if (which == 7) for (int i = 0; i < turns; i++) s += n!.L7!.T;
            if (which == 8) for (int i = 0; i < turns; i++) s += n!.L8!.T + n.L8.L9!.T;
            if (which == 9) for (int i = 0; i < turns; i++) s += n!.L9!.T;
            if (which == 10) for (int i = 0; i < turns; i++) s += n!.L10!.L11!.T;
            if (which == 11) for (int i = 0; i < turns; i++) s += n!.L11!.T + n.Arr!.Length;
            // (a loop with no test at its top: what it reads on every entry is read before it)
            if (which == 12) { int i = 0; do { s += n!.L2!.T + n.L3!.L4!.T; } while (++i < turns); }
            if (which == 13) { int i = 0; do { s += n!.L7!.L8!.L9!.T; } while (++i < turns); }
        }
        catch (NullReferenceException) { return "n"; }
        return s.ToString();
    }
    // a local the loop assigns, read through after a copy of another that the loop does not assign: the copy on one
    // path only, on both arms from two others, inside an inner loop that may take no turn
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Copies(Flat a, Flat b, int n)
    {
        int s = 0;
        var c = b;
        for (int i = 0; i < n; i++) { if (i == 2) c = a; s += c.Z; }
        c = b;
        for (int i = 0; i < n; i++) { if ((i & 1) == 0) c = a; else c = b; s += c.Z * 3; }
        c = b;
        for (int i = 0; i < n; i++) { for (int k = 0; k < i; k++) c = a; s += c.Z * 7; c = b; }
        // (the copy before every read: one read before the loop will do)
        for (int i = 0; i < n; i++) { c = a; s += c.Z * 11; c = b; }
        return s;
    }
    static int s_calls;
    static bool More(Box b) { s_calls++; return b.Q++ < 3; }
    // the loop's test with an effect of its own: a count, a store, a call; each loop reads fields after its test
    static string Tested(Box b, List<Box> boxes, Box? none)
    {
        int s = 0;
        b.Q = 0; b.W = 0; b.V = 7;
        for (int i = 0; i++ < boxes.Count;) s += boxes[i - 1].V + b.V + i;
        int k = 0;
        while (k++ < b.Arr.Length) s += b.V * k;
        s += k;
        while ((b.W = b.W + 1) < 5) s += b.V + boxes.Count;
        s += b.W;
        while (b.W++ < 9) s += b.V + boxes[0].V;
        s += b.W;
        while (More(b)) s += b.V + boxes.Count;
        s += b.Q + s_calls;
        int n = 3;
        while (n-- > 0) s += b.V + boxes[n].V;
        s += n;
        // no turn: the test alone, once
        k = 10;
        while (k++ < 5) s += b.V;
        for (int i = 0; i < boxes.Count - 100; i++) s += boxes[i].V + b.V;
        // left at once, before the reads; the reads behind a test that passes are of a null
        string r = "";
        for (int i = 0; i < boxes.Count; i++) { if (i == 0) break; s += boxes[i].V + none!.V; }
        try { for (int i = 0; i < boxes.Count; i++) { r += "a" + i; s += none!.V + none.Next!.V; } } catch (NullReferenceException) { r += " null"; }
        try { for (int i = 0; i < none!.V; i++) { r += "b"; s += b.V + boxes[i].V; } } catch (NullReferenceException) { r += " test"; }
        List<Box>? noList = null;
        try { for (int i = 0; i < noList!.Count; i++) { r += "c"; s += noList[i].V + b.V; } } catch (NullReferenceException) { r += " list"; }
        return s + " " + k + r;
    }
    static int Constructed(Made first)
    {
        int s = 0;
        var last = first;
        for (int i = 0; i < 4; i++) { s += first.K; last = new Made(i + 10, last); s += first.K + last.K + last.Count + last.Prev!.K; }
        for (int i = 0; i < 4; i++) { s += first.Init; var n = new Made(i, null) { Init = i + 5 }; s += n.Init + first.Init + n.K; }
        var walk = last;
        for (int i = 0; i < 3; i++) { s += walk.K * (i + 1); walk = walk.Prev!; }
        return s;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Thrower(Box b) { b.T = 500; throw new InvalidOperationException("t"); }
    // two reads of a field with something between them that stores it, in a straight line: the second read must
    // see the store (the pass keeps the first read's value for the second only when nothing between can store)
    static int Seconds(Box b, Box same, Box other)
    {
        int s = b.V; b.V = s + 1; s += b.V;
        s += b.X; b.Bump(); s += b.X;
        s += b.Y; same.Y = 77; s += b.Y;
        s += b.W; b.SetW(5); s += b.W + b.GetW();
        s += b.P; b.P = 9; s += b.P; b.BumpP(); s += b.P;
        if (s > 0) { s += b.Z; b.Z = 12; }
        s += b.Z;
        if (s > 0) b.U++; else s += b.U;
        s += b.U;
        var o = b; s += o.Q; o = other; s += o.Q; o = same; s += o.Q;
        s += b.Next!.T; b.Next = new Box(600); s += b.Next.T;
        s += b.Arr.Length; b.Arr = new int[11]; s += b.Arr.Length;
        try { s += b.T; Thrower(b); s += b.T; } catch (InvalidOperationException) { s += b.T; }
        for (int i = 0; i < 3; i++) { s += b.T; s += b.T; b.T++; }
        s += b.T; for (int i = 0; i < 2; i++) b.T += 3; s += b.T;
        s += b.T; for (int i = 0; i < 2; i++) same.Bump(); s += b.T + b.X;
        // (nothing between: one read)
        s += b.Q; s += b.Q + b.Q; if (s > 0) s += b.Q; other.Bump(); s += b.Q + other.X;
        return s;
    }
    static string SecondNull(Box b, Box? n)
    {
        string r = "";
        try { r += b.V; Console.Write("s1"); r += n!.V; r += n.V; } catch (NullReferenceException) { r += " null"; }
        var o = b;
        try { r += o.V; o = n!; Console.Write("s2"); r += o.V; } catch (NullReferenceException) { r += " later"; }
        Console.WriteLine();
        return r;
    }
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
        Console.WriteLine("constructed " + Constructed(new Made(3, null) { Init = 2 }));
        Console.WriteLine("lists " + Lists());
        Console.WriteLine("nulls " + Nulls(null, 4));
        Console.WriteLine("lengths " + Lengths(new int[3], null));
        Console.WriteLine("copies " + Copies(new Flat { Z = 1 }, new Flat { Z = 100 }, 4));
        Console.WriteLine("tested " + Tested(new Box(2), new List<Box> { new Box(1), new Box(2), new Box(3), new Box(4) }, null));
        string chains = "";
        var links = new Link { L0 = new Link { T = 3 }, L3 = new Link { T = 4 }, L8 = new Link { T = 5, L9 = new Link { T = 6 } } };
        for (int w = 0; w < 14; w++) chains += " " + NullChains(null, 0, w) + NullChains(null, 2, w) + NullChains(new Link(), 0, w) + NullChains(new Link(), 2, w) + NullChains(links, 2, w);
        Console.WriteLine("null chains" + chains);
        Console.WriteLine("seconds " + Seconds(b, b, new Box(8)) + " " + SecondNull(b, null));
        Console.WriteLine("quiet " + Quiet(b, new Left { A = 1 }, new Right(), new List<Box> { new Box(1), new Box(2), new Box(3), new Box(4) }));
    }
}
