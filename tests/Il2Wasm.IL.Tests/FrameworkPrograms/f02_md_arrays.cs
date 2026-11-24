// Multi-dimensional arrays: made, read, written, by address, of numbers, references and structs; their lengths,
// bounds checks, Clone, Array's methods over them and the negative-length error.
using System;

struct P { public int X; public string S; public override string ToString() => X + ":" + S; }

static class Program
{
    static void Main()
    {
        var a = new int[3, 4];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 4; j++) a[i, j] = i * 10 + j;
        Console.WriteLine(a[2, 3] + " " + a.Length + " " + a.Rank + " " + a.GetLength(0) + " " + a.GetLength(1) + " " + a.GetUpperBound(1));
        ref int r = ref a[1, 2];
        r += 100;
        Console.WriteLine(a[1, 2]);
        int sum = 0;
        foreach (int v in a) sum += v;
        Console.WriteLine(sum);
        var s = new string[2, 2, 2];
        s[1, 0, 1] = "x"; s[0, 1, 0] = "y";
        Console.WriteLine((s[1, 0, 1] ?? "null") + (s[0, 0, 0] ?? "null") + s[0, 1, 0] + " " + s.Length);
        var c = (string[,,])s.Clone();
        s[1, 0, 1] = "z";
        Console.WriteLine(c[1, 0, 1] + s[1, 0, 1]);
        var ps = new P[2, 3];
        ps[1, 2] = new P { X = 5, S = "five" };
        ps[0, 1].X = 7;
        Console.WriteLine(ps[1, 2] + " " + ps[0, 1] + " " + ps[0, 0]);
        var d = new double[2, 2] { { 1.5, 2.5 }, { 3.5, 4.5 } };
        Console.WriteLine(d[1, 0] + d[0, 1]);
        var b = new byte[2, 3];
        b[1, 2] = 255; b[1, 2]++;
        Console.WriteLine(b[1, 2]);
        try { Console.WriteLine(a[3, 0]); } catch (IndexOutOfRangeException e) { Console.WriteLine("IOOR " + e.Message); }
        try { Console.WriteLine(a[0, -1]); } catch (IndexOutOfRangeException) { Console.WriteLine("IOOR neg"); }
        int n = -1;
        try { var bad = new int[2, n]; Console.WriteLine(bad.Length); } catch (OverflowException e) { Console.WriteLine("Overflow " + e.Message); }
        Array.Clear(a);
        Console.WriteLine(a[2, 3]);
        var e0 = new long[0, 5];
        Console.WriteLine(e0.Length + " " + e0.GetLength(1));
        object o = a;
        Console.WriteLine((o is int[,]) + " " + (o is int[]) + " " + (o is Array) + " " + o.GetType().Name);
        var jag = new int[2][,];
        jag[1] = new int[1, 1];
        jag[1][0, 0] = 9;
        Console.WriteLine(jag[1][0, 0]);
    }
}
