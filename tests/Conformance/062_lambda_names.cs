using System;
using System.Linq;
using System.Runtime.InteropServices;

class Worker
{
    int _bias = 3;
    public int Field;
    public Func<int, int> Make() => x => x + _bias;          // captures this only
    public Func<int, int> MakeCapture(int k) { int m = k * 2; return x => x + m + _bias; } // display class with this
    public void Fail() { Action a = () => throw new InvalidOperationException("worker"); a(); }
}

static class Program
{
    static int s_count;

    static void Report(Exception e)
    {
        foreach (var line in e.StackTrace!.Split('\n'))
        {
            var t = line.Trim();
            int i = t.IndexOf(" in ");
            Console.WriteLine(i > 0 ? t.Substring(0, i) : t);
        }
    }

    static void Static() { Func<int, int> f = x => 10 / x; f(0); }
    static void Captured(int y) { int z = y + 1; Func<int> f = () => z / (y - y); f(); }
    static void Nested() { Func<Func<int>> outer = () => { int q = 0; return () => 5 / q; }; outer()(); }
    static void Local() { int r = 0; int Div(int a) => a / r; Div(4); }
    static void LocalStatic() { static int Div(int a, int b) => a / b; Div(4, 0); }
    static void Second() { Func<int> a = () => 1; Func<int> b = () => a() / (s_count - s_count); b(); }
    static void Loop() { for (int i = 0; i < 2; i++) { int j = i; Func<int> f = () => 10 / j; f(); } }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Action[] tests = [Static, () => Captured(2), Nested, Local, LocalStatic, Second, Loop, () => new Worker().Fail()];
        foreach (var t in tests)
        {
            try { t(); } catch (Exception e) { Console.WriteLine(e.GetType().Name); Report(e); }
        }
        var w = new Worker();
        Console.WriteLine(w.Make()(1) + " " + w.MakeCapture(2)(1));
        Expr(4);
    }

    static void Expr(int k)
    {
        int cap = 5;
        System.Linq.Expressions.Expression<Func<int, int>> e = x => x + cap * k;
        Func<int> after = () => 10 / (cap - 5);
        Console.WriteLine(e + " " + e.Compile()(1));
        try { after(); } catch (Exception ex) { Report(ex); }
    }
}
