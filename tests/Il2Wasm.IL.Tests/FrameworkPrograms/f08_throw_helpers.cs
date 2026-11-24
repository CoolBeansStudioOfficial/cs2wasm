// The argument throw helpers (CallerArgumentExpression names), their messages; array covariance store checks and
// ldelema's exact-type check (readonly. exempt).
using System;

static class Program
{
    static void Show(Action a)
    {
        try { a(); Console.WriteLine("none"); }
        catch (ArgumentException e) { Console.WriteLine(e.GetType().Name + " " + (e.ParamName ?? "null") + " | " + e.Message); }
        catch (Exception e) { Console.WriteLine(e.GetType().Name + " " + e.Message); }
    }

    static void Main()
    {
        string missing = null, white = " ";
        int zero = 0, one = 1;
        Show(() => ArgumentNullException.ThrowIfNull(missing));
        Show(() => ArgumentException.ThrowIfNullOrWhiteSpace(white));
        Show(() => ArgumentOutOfRangeException.ThrowIfZero(zero));
        Show(() => ArgumentOutOfRangeException.ThrowIfGreaterThan(one, 0));
        Show(() => ArgumentOutOfRangeException.ThrowIfNegative(zero - one));
        Show(() => ArgumentNullException.ThrowIfNull(missing, "other"));
        object[] objs = new string[2];
        Show(() => objs[0] = "fine");
        Show(() => objs[1] = new object());
        Show(() => objs[1] = null);
        object[] exact = new object[1];
        Show(() => exact[0] = 5);
        Exception[] ex = new ArgumentException[1];
        Show(() => ex[0] = new ArgumentNullException("p"));
        Show(() => ex[0] = new InvalidOperationException());
        Show(() => { ref object r = ref objs[0]; r = "x"; });
        Show(() => { ref object r = ref exact[0]; r = "x"; });
        Show(() => Console.WriteLine(Read(objs)));
    }
    static string Read<T>(T[] a) { ref readonly T r = ref a[0]; return r?.ToString() ?? "null"; }
}
