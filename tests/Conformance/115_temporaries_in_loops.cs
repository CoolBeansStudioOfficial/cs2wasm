// Temporaries of a loop's statements (section 4.1): a statement's temporary that holds a reference (the place an
// `out` argument of a deconstruction writes to) and a later statement's temporary of the same size that holds none
// (a small struct a method is called on): each round finds the first one clear.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Sym { public string Name = ""; }
readonly struct Small { public readonly int V; public Small(int v) { V = v; } public int Twice() => V * 2; public Small Next() => new Small(V + 1); }
readonly struct Pair
{
    readonly Sym _s; readonly Small _c;
    public Pair(Sym s, Small c) { _s = s; _c = c; }
    public void Deconstruct(out Sym s, out Small c) { s = _s; c = _c; }
}

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Small Make(int v) => new Small(v * 0x01010101 + 0x7f);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Pair Get(int i) => new Pair(new Sym { Name = "s" + i }, new Small(i));

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(int n)
    {
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            var (s, c) = Get(i);
            total += s.Name.Length + c.Twice();
            total += Make(i).Twice() & 0xff;
            total += Make(i + 1).Next().Next().Twice() & 0xff;
            Sym t; Small d;
            (t, d) = Get(i + 100);
            total += t.Name.Length + d.Next().V;
            total += new Small(-1 - i).Next().Twice() & 0xf;
        }
        return total;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Run(1));
        Console.WriteLine(Run(50));
        int sum = 0;
        foreach (var k in new[] { 3, 4, 5 })
        {
            Get(k).Deconstruct(out Sym a, out Small b);
            sum += a.Name.Length + Make(k).Next().V % 7 + b.V;
            Get(k + 1).Deconstruct(out var a2, out _);
            sum += a2.Name.Length + new Small(k).Next().Next().V;
        }
        Console.WriteLine(sum);
    }
}
