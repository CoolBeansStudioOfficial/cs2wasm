// The catch variable of a clause with an exception filter (Lower/Filters.cs, Ir/FilterSlots.cs). The filter stores the
// exception into the variable, which is a frame slot the handler reads; in .NET's first pass the filter runs as a
// function of its own. Its entry must release nothing of its try (the handler releases what a taken exception held,
// the function's cleanup what a refused one held), and a method's argument is not the filter's parameter. Before the
// fix, a try in a method with a reference argument whose filter read the exception gave the handler null, and a
// generic Check<TException> (filter on GetType, handler on Message) threw NullReferenceException.
using System;

static class Program
{
    static void Thrower(int k)
    {
        if (k == 0) throw new ArgumentException("boom");
        if (k == 1) throw new InvalidOperationException("x");
        throw new Exception("gen" + k);
    }

    static int Check<TException>(Action action, string expected, int bit) where TException : Exception
    {
        try { action(); }
        catch (Exception exception) when (exception.GetType() == typeof(TException))
        {
            return exception.Message == expected ? bit : 0;
        }
        return 0;
    }

    static void Run(Action action)
    {
        try { action(); }
        catch (Exception e) when (e.Message.Length > 0) { Console.WriteLine("run " + (e == null ? "null" : e.Message)); }
    }

    static void Nested(Action action)
    {
        try
        {
            try { action(); }
            catch (Exception e) when (e is ArgumentException) { Console.WriteLine("nested " + e.Message); throw; }
        }
        catch (Exception f) { Console.WriteLine("outer " + f.Message); }
    }

    static void Refused(Action action, object tag)
    {
        try
        {
            try { action(); }
            catch (Exception e) when (e.Message == "never") { Console.WriteLine("not reached"); }
        }
        catch (Exception g) { Console.WriteLine("refused " + (tag == null ? "null" : "tag") + " " + g.Message); }
    }

    static void Main()
    {
        try { Thrower(0); }
        catch (Exception e) when (e is ArgumentException) { Console.WriteLine(e.Message); }

        try { Thrower(1); }
        catch (Exception e) when (true) { Console.WriteLine(e == null ? "null" : e.Message); }

        try { Thrower(0); }
        catch (Exception e) when (e.Message.Length > 2) { Console.WriteLine(e == null ? "null" : e.Message); }

        Console.WriteLine("check " + Check<ArgumentException>(() => Thrower(0), "boom", 1 << 0)
            + " " + Check<InvalidOperationException>(() => Thrower(1), "x", 1 << 1)
            + " " + Check<InvalidOperationException>(() => Thrower(1), "y", 1 << 2));

        Run(() => Thrower(0));
        Run(() => Thrower(1));
        Nested(() => Thrower(0));
        Refused(() => Thrower(1), new object());
        Refused(() => Thrower(1), null);
    }
}
