using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

struct WithRef { public int X; public string S; }
struct WithDouble { public double D; public int I; }
struct Padded { public byte B; public int I; }
struct Inner { public int V; public override bool Equals(object? o) => o is Inner i && Math.Abs(i.V - V) <= 1; public override int GetHashCode() => 0; }
struct Outer { public Inner In; public int K; }
struct Plain { public int A, B; }
struct NoEquatable { public int N; }
record struct RS(NoEquatable Field, string Name);
sealed record RC(WithRef W, double D);

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        string h1 = "h" + 1;
        Console.WriteLine(new WithRef { X = 1, S = h1 }.Equals(new WithRef { X = 1, S = "h1" }));
        Console.WriteLine(new WithRef { X = 1, S = null! }.Equals(new WithRef { X = 1, S = null! }) + " " + new WithRef { X = 1, S = null! }.Equals(new WithRef { X = 1, S = "" }));
        Console.WriteLine(new WithDouble { D = double.NaN }.Equals(new WithDouble { D = double.NaN }) + " " + new WithDouble { D = 0.0 }.Equals(new WithDouble { D = -0.0 }));
        Console.WriteLine(new Padded { B = 1, I = 2 }.Equals(new Padded { B = 1, I = 2 }) + " " + new Padded { B = 1, I = 2 }.Equals(new Padded { B = 2, I = 2 }));
        Console.WriteLine(new Outer { In = new Inner { V = 1 }, K = 3 }.Equals(new Outer { In = new Inner { V = 2 }, K = 3 }));
        Console.WriteLine(new Plain { A = 1, B = 2 }.Equals(new Plain { A = 1, B = 2 }) + " " + new Plain { A = 1 }.Equals((object)new NoEquatable { N = 1 }) + " " + new Plain().Equals(null));
        object boxed = new WithRef { X = 5, S = "x" };
        Console.WriteLine(boxed.Equals(new WithRef { X = 5, S = "x" }) + " " + Equals(boxed, new WithRef { X = 6, S = "x" }));
        var set = new HashSet<WithRef> { new WithRef { X = 1, S = "a" } };
        Console.WriteLine(set.Contains(new WithRef { X = 1, S = "a" }) + " " + EqualityComparer<WithDouble>.Default.Equals(new WithDouble { D = 1.5 }, new WithDouble { D = 1.5 }));
        Console.WriteLine(new RS(new NoEquatable { N = 1 }, "n") == new RS(new NoEquatable { N = 1 }, "n"));
        Console.WriteLine(new RC(new WithRef { X = 1, S = h1 }, 2) == new RC(new WithRef { X = 1, S = "h1" }, 2));
        var a1 = new { P = new NoEquatable { N = 3 }, Q = "q" };
        var a2 = new { P = new NoEquatable { N = 3 }, Q = "q" };
        Console.WriteLine(a1.Equals(a2) + " " + (new NoEquatable { N = 1 }, "t").Equals((new NoEquatable { N = 1 }, "t")));
        Console.WriteLine(new WithRef { X = 1 }.ToString() + " " + new Plain().GetType().Name);
    }
}
