// Exceptions: throw and catch by type, finally on every path, nested regions, rethrow, filters, the runtime's own
// exceptions, and one that escapes Main.
using System;

class MyError : Exception
{
    public readonly int Code;
    public MyError(int code) : base("my error " + code) { Code = code; }
}

static class Program
{
    static int Depth;

    static void Thrower(int n)
    {
        if (n == 0) throw new MyError(7);
        Depth++;
        try { Thrower(n - 1); }
        finally { Depth--; }
    }

    static string Classify(int k)
    {
        try
        {
            if (k == 0) throw new InvalidOperationException("zero");
            if (k == 1) throw new MyError(1);
            if (k == 2) { int[] a = new int[1]; return a[k].ToString(); }
            if (k == 3) { object o = "s"; return ((Exception)o).Message; }
            if (k == 4) { int z = k - 4; return (10 / z).ToString(); }
            return "none";
        }
        catch (MyError e) when (e.Code == 1) { return "filtered " + e.Message; }
        catch (InvalidOperationException e) { return "invalid " + e.Message; }
        catch (IndexOutOfRangeException) { return "index"; }
        catch (InvalidCastException) { return "cast"; }
        catch (Exception e) { return "other " + e.Message; }
    }

    static int Finally(int k)
    {
        int r = 0;
        try
        {
            try
            {
                r = 1;
                if (k > 0) throw new MyError(k);
                r = 2;
            }
            finally { r += 10; Console.WriteLine("inner finally " + r); }
        }
        catch (MyError e) { r += e.Code * 100; }
        finally { Console.WriteLine("outer finally " + r); }
        return r;
    }

    static int Loop()
    {
        int n = 0;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (i % 2 == 0) continue;
                if (i == 3) break;
                n += i;
            }
            finally { n += 100; }
        }
        return n;
    }

    static void Rethrow()
    {
        try
        {
            try { throw new MyError(3); }
            catch (MyError) { Console.WriteLine("caught, rethrowing"); throw; }
        }
        catch (Exception e) { Console.WriteLine("outer: " + e.Message); }
    }

    static void Main()
    {
        try { Thrower(5); }
        catch (MyError e) { Console.WriteLine(e.Message + " depth " + Depth); }
        for (int k = 0; k < 6; k++) Console.WriteLine(Classify(k));
        Console.WriteLine(Finally(0));
        Console.WriteLine(Finally(2));
        Console.WriteLine(Loop());
        Rethrow();
        string s = null;
        try { Console.WriteLine(s.Length); }
        catch (NullReferenceException) { Console.WriteLine("null"); }
        try { checked { int big = int.MaxValue; big += Depth + 1; Console.WriteLine(big); } }
        catch (OverflowException e) { Console.WriteLine(e.Message); }
        throw new InvalidOperationException("the end");
    }
}
