// A span or a `ref` into a collection's storage, passed on in every way the language has, then read and written
// after the collection grew or was cleared: it still reads the old storage, as under .NET (the storage is kept
// for as long as the span can be used). Lists of ints, strings and of a class that can be in a cycle; a
// dictionary's value by `ref`; arrays resized; a stack and a queue through their enumerators' storage.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Node { public Node? Next; public int Id; public List<Node> Kids = new(); public Node(int id) { Id = id; } }

ref struct Holder<T>
{
    public Span<T> S; public ref T First;
    public Holder(Span<T> s) { S = s; First = ref s[0]; }
    public Span<T> Rest() => S.Slice(1);
}

readonly ref struct Pair { public readonly ReadOnlySpan<int> A; public readonly Span<string> B; public Pair(ReadOnlySpan<int> a, Span<string> b) { A = a; B = b; } }

delegate Span<T> Spanner<T>(List<T> l);
delegate ref T Reffer<T>(List<T> l, int i);

static class Program
{
    static int s_fill = 100;
    static void Grow<T>(List<T> l, Func<int, T> make) { for (int i = 0; i < 40; i++) l.Add(make(s_fill++)); }
    static string Text<T>(ReadOnlySpan<T> s, Func<T, string> f) { var parts = new string[s.Length]; for (int i = 0; i < s.Length; i++) parts[i] = f(s[i]); return string.Join(",", parts); }
    static string I(ReadOnlySpan<int> s) => Text(s, x => x.ToString());
    static string N(ReadOnlySpan<Node> s) => Text(s, x => x == null ? "null" : x.Id.ToString());
    static string S(ReadOnlySpan<string> s) => Text(s, x => x ?? "null");

    static List<int> Ints() => new List<int> { 1, 2, 3 };
    static List<string> Strs() => new List<string> { "a" + s_fill, "b" + s_fill, "c" + s_fill };
    static List<Node> Nodes() { var l = new List<Node>(); for (int i = 1; i <= 3; i++) { var n = new Node(i); n.Kids.Add(n); if (l.Count > 0) n.Next = l[0]; l.Add(n); } l[0].Next = l[2]; return l; }

    [MethodImpl(MethodImplOptions.NoInlining)] static Span<T> Get<T>(List<T> l) => CollectionsMarshal.AsSpan(l);
    [MethodImpl(MethodImplOptions.NoInlining)] static Span<T> Through<T>(Span<T> s) => s;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Out<T>(List<T> l, out Span<T> s) { s = CollectionsMarshal.AsSpan(l); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void ByRef<T>(List<T> l, ref Span<T> s) { s = CollectionsMarshal.AsSpan(l).Slice(1); }
    [MethodImpl(MethodImplOptions.NoInlining)] static ref T At<T>(List<T> l, int i) => ref CollectionsMarshal.AsSpan(l)[i];
    [MethodImpl(MethodImplOptions.NoInlining)] static ref T Pick<T>(ref T a, ref T b, bool first) => ref first ? ref a : ref b;
    [MethodImpl(MethodImplOptions.NoInlining)] static Holder<T> Hold<T>(List<T> l) => new Holder<T>(CollectionsMarshal.AsSpan(l));
    [MethodImpl(MethodImplOptions.NoInlining)] static ReadOnlySpan<T> In<T>(in Span<T> s) => s;

    static void Ways<T>(string name, Func<List<T>> make, Func<int, T> fill, Func<T, string> f)
    {
        string T2(ReadOnlySpan<T> s) => Text(s, f);
        { var l = make(); var s = CollectionsMarshal.AsSpan(l); Grow(l, fill); Console.WriteLine($"{name} local: {T2(s)} / {l.Count}"); s[0] = fill(-1); Console.WriteLine($"  written: {T2(s)} list {f(l[0])}"); }
        { var l = make(); var s = Get(l); l.Clear(); l.TrimExcess(); Grow(l, fill); Console.WriteLine($"{name} returned, cleared: {T2(s)}"); }
        { var l = make(); var s = Through(Through(Get(l)).Slice(1)); Grow(l, fill); Console.WriteLine($"{name} through: {T2(s)}"); }
        { var l = make(); Out(l, out var s); Grow(l, fill); Console.WriteLine($"{name} out: {T2(s)}"); }
        { var l = make(); Span<T> s = default; ByRef(l, ref s); Grow(l, fill); Console.WriteLine($"{name} ref: {T2(s)}"); }
        { var l = make(); ref T r = ref At(l, 1); Grow(l, fill); Console.WriteLine($"{name} ref return: {f(r)}"); r = fill(-2); Console.WriteLine($"  written: {f(r)} list {f(l[1])}"); }
        { var l = make(); var m = make(); ref T r = ref Pick(ref At(l, 0), ref At(m, 2), l.Count > 5); Grow(l, fill); Grow(m, fill); Console.WriteLine($"{name} picked: {f(r)}"); }
        { var l = make(); var h = Hold(l); Grow(l, fill); Console.WriteLine($"{name} ref struct: {T2(h.S)} first {f(h.First)} rest {T2(h.Rest())}"); }
        { var l = make(); var s = Get(l); var r = In(in s); Grow(l, fill); Console.WriteLine($"{name} in: {T2(r)}"); }
        { var l = make(); Spanner<T> d = Get; var s = d(l); Grow(l, fill); Console.WriteLine($"{name} delegate: {T2(s)}"); }
        { var l = make(); Reffer<T> d = At; ref T r = ref d(l, 2); Grow(l, fill); Console.WriteLine($"{name} ref delegate: {f(r)}"); }
        { var l = make(); Func<List<T>, int> len = x => { var s = CollectionsMarshal.AsSpan(x); Grow(x, fill); return s.Length; }; Console.WriteLine($"{name} lambda: {len(l)}"); }
        { var l = make(); Span<T> s = default; if (l.Count == 3) { s = CollectionsMarshal.AsSpan(l); } Grow(l, fill); Console.WriteLine($"{name} inner scope: {T2(s)}"); }
        { var l = make(); Span<T> s = default; for (int i = 0; i < 3; i++) { s = CollectionsMarshal.AsSpan(l); Grow(l, fill); } Console.WriteLine($"{name} loop: {s.Length} {f(s[0])}"); }
        { var l = make(); var s = CollectionsMarshal.AsSpan(l); var e = s.GetEnumerator(); Grow(l, fill); int n = 0; while (e.MoveNext()) n++; Console.WriteLine($"{name} enumerator: {n}"); }
        { var l = make(); var s = CollectionsMarshal.AsSpan(l); l = make(); Grow(l, fill); Console.WriteLine($"{name} list replaced: {T2(s)}"); }
        { var l = make(); var s = CollectionsMarshal.AsSpan(l); l.Insert(0, fill(-3)); l.RemoveAt(1); l.Reverse(); Console.WriteLine($"{name} moved in place: {T2(s)}"); }
        { var a = make().ToArray(); var s = a.AsSpan(); var m = a.AsMemory(1); Array.Resize(ref a, 10); a[0] = fill(-4); Console.WriteLine($"{name} array resized: {T2(s)} memory {T2(m.Span)}"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Dictionaries()
    {
        var d = new Dictionary<string, int> { ["a"] = 1 };
        ref int r = ref CollectionsMarshal.GetValueRefOrAddDefault(d, "b", out bool had);
        r = 5;
        for (int i = 0; i < 100; i++) d["k" + i] = i;
        r += 10;
        Console.WriteLine($"dictionary ref: {r} {had} {d["b"]} {d.Count}");
        var dn = new Dictionary<int, Node>();
        ref Node? rn = ref CollectionsMarshal.GetValueRefOrAddDefault(dn, 1, out _);
        rn = new Node(7); rn.Kids.Add(rn);
        for (int i = 2; i < 60; i++) dn[i] = new Node(i);
        Console.WriteLine($"dictionary of nodes: {rn.Id} {dn[1].Id} {ReferenceEquals(rn, dn[1])}");
        ref Node existing = ref CollectionsMarshal.GetValueRefOrNullRef(dn, 5);
        dn.Clear(); dn.TrimExcess();
        Console.WriteLine($"cleared: {existing == null} {Unsafe.IsNullRef(ref CollectionsMarshal.GetValueRefOrNullRef(dn, 5))}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Mixed()
    {
        var li = Ints(); var ls = Strs();
        var p = new Pair(CollectionsMarshal.AsSpan(li), CollectionsMarshal.AsSpan(ls));
        Grow(li, x => x); Grow(ls, x => "s" + x);
        Console.WriteLine($"pair: {I(p.A)} {p.B.Length} {p.B[0][0]}");
        var text = "hello world"; var sp = text.AsSpan(6); text = text + "!"; Console.WriteLine("string: " + sp.ToString());
        var sb = new System.Text.StringBuilder("abc"); foreach (var chunk in sb.GetChunks()) { var cs = chunk.Span; sb.Append('x', 100); Console.WriteLine("builder chunk: " + cs.Length + " " + cs[0]); break; }
        var stack = new Stack<Node>(); stack.Push(new Node(1)); stack.Push(new Node(2));
        foreach (var n in stack.ToArray().AsSpan()) { stack.Push(new Node(n.Id + 10)); }
        Console.WriteLine("stack: " + stack.Count);
        Span<Node> local = new Node[2]; local[0] = new Node(1); local[1] = new Node(2); local[0].Next = local[1]; local[1].Next = local[0];
        Console.WriteLine("array in a span: " + N(local) + " " + local[0].Next!.Next!.Id);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Ways("ints", Ints, x => x, x => x.ToString());
        Ways("strings", Strs, x => "s" + x, x => x ?? "null");
        Ways("nodes", Nodes, x => { var n = new Node(x); n.Kids.Add(n); return n; }, x => x == null ? "null" : x.Id + (x.Next == null ? "" : ">" + x.Next.Id));
        Dictionaries();
        Mixed();
    }
}
