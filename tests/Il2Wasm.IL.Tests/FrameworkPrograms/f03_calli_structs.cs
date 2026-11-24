// Function pointers (calli) with struct arguments and struct results, with and without references in them;
// floating-point remainder.
using System;

struct V2 { public double X, Y; public override string ToString() => "(" + X + "," + Y + ")"; }
struct Named { public string Name; public int N; }

static unsafe class Program
{
    static V2 Add(V2 a, V2 b) => new V2 { X = a.X + b.X, Y = a.Y + b.Y };
    static Named Rename(Named n, string s) { n.Name = s + n.Name; n.N++; return n; }
    static int Len(Named n) => n.Name.Length + n.N;
    static void Main()
    {
        delegate*<V2, V2, V2> add = &Add;
        var r = add(new V2 { X = 1, Y = 2 }, new V2 { X = 0.5, Y = -4 });
        Console.WriteLine(r);
        delegate*<Named, string, Named> ren = &Rename;
        var n = new Named { Name = "bob", N = 1 };
        var m = ren(n, "x-");
        Console.WriteLine(n.Name + n.N + " " + m.Name + m.N);
        delegate*<Named, int> len = &Len;
        Console.WriteLine(len(m));
        for (int i = 0; i < 3; i++) m = ren(m, "y");
        Console.WriteLine(m.Name + " " + m.N);
        double a = 7.5, b = 2;
        float fa = -7.25f, fb = 2f;
        Console.WriteLine(a % b + " " + (fa % fb) + " " + (-0.0 % 1.0) + " " + (5.0 % 0.0) + " " + (1e300 % 3.0) + " " + (fa % 0.1f));
    }
}
