using System;
using System.Runtime.InteropServices;

class GameException : Exception
{
    public int Code { get; }
    public GameException(string message, int code) : base(message) { Code = code; }
}

class Resource : IDisposable
{
    readonly string _name;
    public Resource(string name) { _name = name; Console.WriteLine("open " + name); }
    public void Dispose() => Console.WriteLine("close " + _name);
}

static class Program
{
    static int Depth(int n)
    {
        try
        {
            if (n == 0) throw new GameException("bottom", 7);
            return Depth(n - 1) + 1;
        }
        finally
        {
            Console.WriteLine("unwind " + n);
        }
    }

    static int TryReturn()
    {
        int x = 1;
        try { return x; }
        finally { x = 2; Console.WriteLine("finally after return"); }
    }

    static void Filtered(int code)
    {
        try { throw new GameException("f", code); }
        catch (GameException e) when (e.Code > 5) { Console.WriteLine("big code " + e.Code); }
        catch (GameException e) { Console.WriteLine("small code " + e.Code); }
    }

    static void Rethrow()
    {
        try { throw new InvalidOperationException("inner op"); }
        catch (Exception) { Console.WriteLine("caught, rethrowing"); throw; }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        try { Depth(3); }
        catch (GameException e) { Console.WriteLine("caught " + e.Message + " code " + e.Code); }
        Console.WriteLine(TryReturn());
        Filtered(9); Filtered(2);
        try { Rethrow(); } catch (InvalidOperationException e) { Console.WriteLine("outer got " + e.Message); }
        using (var r = new Resource("a"))
        {
            Console.WriteLine("using a");
        }
        try
        {
            using var r2 = new Resource("b");
            using var r3 = new Resource("c");
            throw new ArgumentException("bad arg", "param");
        }
        catch (ArgumentException e) { Console.WriteLine(e.Message + " / " + e.ParamName); }
        for (int i = 0; i < 3; i++)
        {
            try
            {
                if (i == 1) continue;
                Console.WriteLine("body " + i);
            }
            finally { Console.WriteLine("finally " + i); }
        }
        try { int[] a = new int[2]; a[2] = 1; } catch (IndexOutOfRangeException e) { Console.WriteLine(e.Message); }
        try { throw new Exception("outer", new Exception("inner")); } catch (Exception e) { Console.WriteLine(e.InnerException.Message); Console.WriteLine(e.ToString()); }
        try { object o = "s"; int i = (int)o; } catch (InvalidCastException e) { Console.WriteLine(e.Message); }
        try { throw new NotImplementedException(); } catch (Exception e) { Console.WriteLine(e.Message); }
        Exception ex = new ArgumentNullException("thing");
        Console.WriteLine(ex.Message);
        Console.WriteLine(new ArgumentOutOfRangeException("idx").Message);
        try { try { throw new Exception("1"); } finally { Console.WriteLine("inner finally"); } }
        catch (Exception e) { Console.WriteLine("got " + e.Message); }
        Console.WriteLine("done");
    }
}
