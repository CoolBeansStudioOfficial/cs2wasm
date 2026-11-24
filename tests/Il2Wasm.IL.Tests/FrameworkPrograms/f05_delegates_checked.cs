// Delegates of a static virtual on a type argument (constrained. ldftn) and of another delegate's Invoke; the
// overflow-checked multiply's edges and checked conversions of doubles at the bounds.
using System;

interface IMake<TSelf> where TSelf : IMake<TSelf> { static abstract TSelf Make(int n); static virtual int Seven() => 7; }
sealed class Thing : IMake<Thing> { public int N; public static Thing Make(int n) => new Thing { N = n * 2 }; public static int Seven() => 77; }
struct Val : IMake<Val> { public int N; public static Val Make(int n) => new Val { N = n + 1 }; }

static class Program
{
    static Func<int, T> Maker<T>() where T : IMake<T> => T.Make;
    static Func<int> Sevens<T>() where T : IMake<T> => T.Seven;

    static string Try(Func<long> f) { try { return f().ToString(); } catch (OverflowException) { return "ovf"; } }

    static void Main()
    {
        Console.WriteLine(Maker<Thing>()(5).N + " " + Maker<Val>()(5).N + " " + Sevens<Thing>()() + " " + Sevens<Val>()());
        Func<int, int> sq = x => x * x;
        Func<int, int> again = sq.Invoke;
        Func<int, int> twice = new Func<int, int>(again.Invoke) + sq;
        Console.WriteLine(again(7) + " " + twice(3));
        long[] xs = [-1, 1, 0, 2, -2, long.MinValue, long.MaxValue, 3037000499, 3037000500, -3037000500];
        foreach (var a in xs)
        {
            var line = "";
            foreach (var b in xs) line += Try(() => checked(a * b)) + " ";
            Console.WriteLine(line);
        }
        ulong ua = ulong.MaxValue;
        Console.WriteLine(Try(() => (long)checked(ua * 1UL)) == "ovf" ? "ovf" : (checked(ua * 1UL)).ToString());
        double[] ds = [9.2233720368547758E18, 9.2233720368547748E18, -9.2233720368547758E18, -9.2233720368547779E18, 2147483647.9, 2147483648.0, -2147483648.9, -2147483649.0, 1.8446744073709552E19, double.NaN, -0.9];
        foreach (var d in ds)
            Console.WriteLine(Try(() => checked((long)d)) + " " + Try(() => checked((int)d)) + " " + Try(() => (long)checked((ulong)d)) + " " + Try(() => checked((uint)d)));
    }
}
