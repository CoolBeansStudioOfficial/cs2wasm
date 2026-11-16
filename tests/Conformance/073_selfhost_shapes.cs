// Shapes the self-hosted compiler needs (SPEC section 10, proof 4): a generic virtual method overridden in a class whose
// base's type arguments coincide (ForAllOperator<T> : UnaryQueryOperator<T, T>), PLINQ (AsParallel, AsOrdered,
// Select, Where, Sum, ToList), environment variables (the host's: PATH is there under .NET and the module alike,
// set and read back), a resource writer's output read back by a resource reader, `default` of a struct in a loop (a
// fresh zero value each time, never the last iteration's), an ordered PLINQ query enumerated as it is merged, and list
// patterns over ImmutableArray<T> in generic code (T a struct, a class, an int), an iterator of a struct (its `this` a
// copy in the iterator, read after the original is gone), and a switch on a tuple of references with type patterns.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.InteropServices;

struct Pr { public int A; public string? B; public Pr(int a, string b) { A = a; B = b; } }

readonly struct OM<T>
{
    readonly T? _one; readonly System.Collections.Immutable.ImmutableArray<T> _many;
    public OM(System.Collections.Immutable.ImmutableArray<T> many) { if (many is [var item]) { _one = item; _many = default; } else { _one = default; _many = many; } }
    public string Show() => _many.IsDefault ? "one " + _one : "many " + _many.Length;
    public static string Lp(System.Collections.Immutable.ImmutableArray<T> a) => a is [var x] ? "1:" + x : a is [var f, .., var l] ? "n:" + f + "-" + l : "0";
}

struct BV
{
    readonly int[] _bits; readonly int _n;
    public BV(int n, params int[] set) { _bits = new int[n]; _n = n; foreach (var s in set) _bits[s] = 1; }
    public IEnumerable<int> TrueBits() { for (int i = 0; i < _n; i++) if (_bits[i] != 0) yield return i; }
}
sealed class Holder { public BV Bad { get; } public Holder(BV b) => Bad = b; }

abstract class Test { }
sealed class TypeTest(string t) : Test { public string T = t; }
sealed class ValueTest(int v) : Test { public int V = v; }
sealed class NullTest : Test { }

abstract class B<TI, TO> { internal abstract string W<TK>(TK k, TO o); public string Go<TK>(TK k, TO o) => W(k, o); }
sealed class D<T> : B<T, T> { internal override string W<TK>(TK k, T o) => "D " + typeof(TK).Name + " " + k + " " + o; }
sealed class E<T, U> : B<T, U> { internal override string W<TK>(TK k, U o) => "E " + typeof(TK).Name + " " + k + " " + o; }

static class Program
{
    static IEnumerable<int> Bits(Holder h) => h.Bad.TrueBits();

    static string Same(Test a, Test b)
    {
        var label = new System.Text.StringBuilder("same:");
        switch (a, b)
        {
            case (TypeTest x, TypeTest y): label.Append(x.T == y.T); break;
            case (ValueTest x, ValueTest y): label.Append((x.V + 0, y.V + 0) == (y.V, x.V)); break;
            case (NullTest, NullTest): label.Append("null"); break;
            default: label.Append("different kinds"); break;
        }
        return label.ToString();
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        B<int, int> b = new D<int>();
        Console.WriteLine(b.Go(1, 2) + " | " + b.Go("s", 3));
        B<string, int> e = new E<string, int>();
        Console.WriteLine(e.Go(5L, 6));

        var xs = Enumerable.Range(0, 50).Select(i => "s" + i).ToList();
        var r = xs.AsParallel().AsOrdered().Select(s => s.Length).ToList();
        Console.WriteLine(r.Count + " " + r.Sum() + " " + string.Join(",", r.Take(12)));
        Console.WriteLine(Enumerable.Range(1, 100).AsParallel().Where(i => i % 3 == 0).Sum());

        Console.WriteLine((Environment.GetEnvironmentVariable("PATH") != null) + " " + (Environment.GetEnvironmentVariable("CS2WASM_073_UNSET") == null));
        Environment.SetEnvironmentVariable("CS2WASM_073", "set");
        Console.WriteLine(Environment.GetEnvironmentVariable("CS2WASM_073") + " " + Environment.GetEnvironmentVariables().Contains("CS2WASM_073"));

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            Pr pr = default;
            sb.Append(pr.B ?? "-");
            pr = new Pr(i, "x" + i);
            var other = new Pr(i * 2, "y" + i);
            sb.Append(pr.B).Append(other.A).Append(' ');
        }
        Console.WriteLine(sb.ToString());

        long total = 0;
        foreach (var s in xs.AsParallel().AsOrdered().WithDegreeOfParallelism(16).Select(x => x)) total += s.Length;
        Console.WriteLine(total);

        Console.WriteLine(new OM<Pr>([new Pr(1, "a")]).Show() + " " + new OM<Pr>([new Pr(1, "a"), new Pr(2, "b")]).Show()
            + " " + new OM<string>(["z"]).Show() + " " + new OM<int>([5]).Show());
        Console.WriteLine(OM<string>.Lp(["c"]) + " " + OM<string>.Lp(["c", "d", "e"]) + " " + OM<int>.Lp([]));

        var bits1 = Bits(new Holder(new BV(8, 1, 5)));
        var bits2 = Bits(new Holder(new BV(6, 0, 2, 4)));
        var bvl = new BV(4, 3);
        Console.WriteLine(string.Join(",", bits1) + " | " + string.Join(",", bits2) + " | " + string.Join(",", bvl.TrueBits()));
        Console.WriteLine(Same(new TypeTest("a"), new TypeTest("a")) + " " + Same(new ValueTest(2), new ValueTest(3)) + " " + Same(new ValueTest(4), new ValueTest(4))
            + " " + Same(new NullTest(), new NullTest()) + " " + Same(new NullTest(), new TypeTest("x")));

        var ms = new MemoryStream();
        using (var w = new ResourceWriter(ms)) { w.AddResource("b", "two"); w.AddResource("a", "one"); }
        var rd = new ResourceReader(new MemoryStream(ms.ToArray()));
        foreach (var x in rd.Cast<DictionaryEntry>().OrderBy(x => (string)x.Key)) Console.WriteLine(x.Key + "=" + x.Value);
    }
}
