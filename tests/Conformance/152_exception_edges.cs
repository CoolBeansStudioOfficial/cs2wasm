// Exceptions at their edges (section 6.1, 6.3): ToString with inner exceptions, AggregateException, a static
// constructor that throws, ExceptionDispatchInfo's trace, `throw;` against `throw e;`, a throw from a finally,
// Data, Source, HResult, default messages, a filter's side effects, and reflection on exception types.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

sealed class Quiet : Exception { }

sealed class Coded : Exception
{
    public Coded(string m, Exception? inner = null) : base(m, inner) { HResult = 0x1234; }
}

static class Broken
{
    public static readonly int Value = Fail();
    static int Fail() => throw new InvalidOperationException("cctor");
}

static class Program
{
    // a trace's lines without paths, down to Main
    static void Trace(string? text)
    {
        foreach (var raw in (text ?? "<null>").Split('\n'))
        {
            var t = raw.TrimEnd('\r');
            int i = t.IndexOf(" in "), l = t.LastIndexOf(":line ");
            Console.WriteLine(i > 0 && l > i ? t.Substring(0, i) + t.Substring(l) : t);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Thrower(string m) => throw new Coded(m);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rethrow()
    {
        try { Thrower("again"); }
        catch (Exception) { throw; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void RethrowNew()
    {
        try { Thrower("reset"); }
        catch (Exception e) { throw e; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void FromFinally()
    {
        try { Thrower("lost"); }
        finally { throw new Coded("from finally"); }
    }

    static int s_filters;
    static bool Count(Exception e) { s_filters++; return e.Message == "b"; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        try { try { Thrower("inner"); } catch (Exception e) { throw new Coded("outer", e); } }
        catch (Exception e) { Trace(e.ToString()); Console.WriteLine(e.InnerException!.Message + " " + e.HResult.ToString("x") + " " + e.Source); }

        Console.WriteLine(new Quiet().Message);
        Console.WriteLine(new Exception().Message + "|" + new ArgumentException().Message + "|" + new ArgumentNullException("p").Message);
        Console.WriteLine(new ArgumentOutOfRangeException("x", 5, "too big").Message);
        Console.WriteLine(new Exception("m").ToString());
        Console.WriteLine(new Exception("m").StackTrace == null);

        var agg = new AggregateException(new Coded("a"), new InvalidOperationException("b"));
        Console.WriteLine(agg.Message);
        Console.WriteLine(agg.Flatten().InnerExceptions.Count + " " + agg.GetBaseException().GetType().Name);
        try { agg.Handle(x => x is Coded); }
        catch (AggregateException left) { Console.WriteLine(left.InnerExceptions.Count + " " + left.InnerException!.Message); }

        try { Console.WriteLine(Broken.Value); }
        catch (TypeInitializationException e) { Console.WriteLine(e.Message + " | " + e.TypeName + " | " + e.InnerException!.Message); }
        try { Console.WriteLine(Broken.Value); }
        catch (TypeInitializationException e) { Console.WriteLine("again: " + e.InnerException!.Message); }

        try { Rethrow(); } catch (Exception e) { Trace(e.StackTrace); }
        try { RethrowNew(); } catch (Exception e) { Trace(e.StackTrace); }
        try { FromFinally(); } catch (Exception e) { Console.WriteLine(e.Message); Trace(e.StackTrace); }

        ExceptionDispatchInfo? edi = null;
        try { Thrower("captured"); } catch (Exception e) { edi = ExceptionDispatchInfo.Capture(e); }
        try { edi!.Throw(); } catch (Exception e) { Trace(e.StackTrace); }

        var ex = new Coded("data");
        ex.Data["k"] = 42;
        Console.WriteLine(ex.Data["k"] + " " + ex.Data.Count + " " + ex.Data.Contains("k"));

        foreach (var m in new[] { "a", "b" })
        {
            try { throw new Exception(m); }
            catch (Exception e) when (Count(e)) { Console.WriteLine("filtered " + e.Message); }
            catch (Exception e) { Console.WriteLine("plain " + e.Message); }
        }
        Console.WriteLine(s_filters);

        Console.WriteLine(typeof(Coded).BaseType + " " + typeof(ArgumentNullException).BaseType + " " + typeof(Coded).IsSealed);
        Console.WriteLine(new Coded("x").GetType().FullName + " " + typeof(KeyNotFoundException).Namespace);
        try { _ = new Dictionary<string, int>()["missing"]; }
        catch (KeyNotFoundException e) { Console.WriteLine(e.Message); }
        try { object o = new List<int>(); _ = (string)o; }
        catch (InvalidCastException e) { Console.WriteLine(e.Message); }
        try { checked { int big = int.MaxValue; big++; } }
        catch (OverflowException e) { Console.WriteLine(e.Message); }
        try { _ = "abc".Substring(5); }
        catch (ArgumentOutOfRangeException e) { Console.WriteLine(e.ParamName + ": " + e.Message); }
    }
}
