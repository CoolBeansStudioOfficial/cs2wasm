// Delegate.DynamicInvoke: the arguments taken as their parameters take them (null is a value type's default, a primitive
// widens, otherwise ArgumentException, not wrapped), the result boxed, the count checked, exceptions wrapped as .NET does.
using System;

public struct Pt { public int X; public long Y; public Pt(int x, long y) { X = x; Y = y; } }

public static class Program
{
    public static int Twice(int x) => 2 * x;
    public static Pt MakePt(int x) => new Pt(x, x * 10L);
    public static long Sum(Pt p, byte b) => p.X + p.Y + b;

    static void Show(object? o) => Console.WriteLine(o == null ? "null" : o.GetType().Name + " " + o);

    static void Try(Func<object?> f)
    {
        try { Show(f()); }
        catch (Exception e)
        {
            Console.WriteLine(e.GetType().Name + ": " + (e.InnerException == null ? e.Message : e.InnerException.GetType().Name + ": " + e.InnerException.Message));
        }
    }

    static void Main()
    {
        Func<int, int> twice = Twice;
        Try(() => twice.DynamicInvoke(21));
        Try(() => twice.DynamicInvoke("x"));
        Try(() => twice.DynamicInvoke((long)1));
        Try(() => twice.DynamicInvoke((short)1));
        Try(() => twice.DynamicInvoke('A'));
        Try(() => twice.DynamicInvoke(null));
        Try(() => twice.DynamicInvoke());

        Func<double, double> half = x => x / 2;
        Try(() => half.DynamicInvoke(3));
        Try(() => half.DynamicInvoke(3.5f));

        Func<Pt> mk = () => new Pt(3, 4);
        Try(() => mk.DynamicInvoke());
        Func<Pt, byte, long> sum = Sum;
        Try(() => sum.DynamicInvoke(new Pt(1, 2), (byte)3));
        Try(() => sum.DynamicInvoke(null, (byte)3));
        Try(() => sum.DynamicInvoke(new Pt(1, 2), 3));
        Try(() => sum.DynamicInvoke(new Pt(1, 2), (sbyte)3));

        Func<long> lng = () => 1L << 40;
        Try(() => lng.DynamicInvoke());
        Action<string> act = s => Console.WriteLine("act " + s);
        Try(() => { act.DynamicInvoke("hi"); return null; });
        Func<string, string> named = s => "name:" + s;
        Try(() => named.DynamicInvoke(null));
        Try(() => named.DynamicInvoke("s"));
        Try(() => named.DynamicInvoke(5));

        Func<int> thrower = () => throw new InvalidOperationException("boom");
        Try(() => thrower.DynamicInvoke());
        Func<int> one = () => 1, two = () => 2;
        Func<int> both = one + two;
        Try(() => both.DynamicInvoke());

        Func<int, Pt> dm = MakePt;
        Try(() => dm.DynamicInvoke(4));

        // a delegate type of its own, with a struct, a small integer, a reference and a double parameter
        Mix mix = (p, b, t, d) => p.X + p.Y + b + t.Length + (long)d;
        Try(() => mix.DynamicInvoke(new Pt(1, 2), (sbyte)3, "abcd", 5.0));
        Try(() => mix.DynamicInvoke(new Pt(1, 2), 3, "x", 5.0));
        Try(() => mix.DynamicInvoke(null, (sbyte)1, "xy", 2.5));
        Try(() => mix.DynamicInvoke(new Pt(1, 2), (sbyte)3, null, 5.0));
        Try(() => mix.DynamicInvoke(new Pt(1, 2), (sbyte)3, 9, 5.0));
        Try(() => mix.DynamicInvoke(new Pt(1, 2), (sbyte)3, "abcd", 5));
    }

    public delegate long Mix(Pt p, sbyte b, string t, double d);
}
