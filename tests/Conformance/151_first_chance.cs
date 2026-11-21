// AppDomain.FirstChanceException (section 6.1): raised at each throw and rethrow, before filters and catch clauses
// run, with the exception and the domain; handlers added and removed; an exception a handler throws is its own.
using System;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

static class Program
{
    static int s_count;

    static void OnFirst(object? sender, FirstChanceExceptionEventArgs e)
    {
        s_count++;
        Console.WriteLine("first chance: " + e.Exception.GetType().Name + " " + e.Exception.Message + " " + (sender == AppDomain.CurrentDomain));
    }

    static bool Filter(string where) { Console.WriteLine("filter " + where); return true; }

    static void Thrower() => throw new InvalidOperationException("deep");

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirst;
        try
        {
            try { throw new ArgumentException("one"); }
            catch (ArgumentException e) when (Filter("one")) { Console.WriteLine("caught " + e.Message); }

            try
            {
                try { Thrower(); }
                catch (InvalidOperationException) { Console.WriteLine("rethrowing"); throw; }
            }
            catch (Exception e) { Console.WriteLine("outer " + e.Message); }

            try
            {
                try { throw new FormatException("f"); }
                catch (FormatException e) { throw new AggregateException("wrapped", e); }
            }
            catch (AggregateException e) { Console.WriteLine("aggregate " + e.InnerExceptions.Count); }

            try { int.Parse("x"); } catch (FormatException) { Console.WriteLine("parse failed"); }
            try { object o = "s"; _ = (int)o; } catch (InvalidCastException) { Console.WriteLine("cast failed"); }
            try { int[] a = new int[1]; a[2] = 1; } catch (IndexOutOfRangeException) { Console.WriteLine("index failed"); }
            try { string? s = null; _ = s!.Length; } catch (NullReferenceException) { Console.WriteLine("null failed"); }
            try { int z = 0; _ = 1 / z; } catch (DivideByZeroException) { Console.WriteLine("divide failed"); }
            try { ExceptionDispatchInfo.Capture(new TimeoutException("t")).Throw(); } catch (TimeoutException) { Console.WriteLine("edi"); }
            try { try { throw new Exception("in try"); } finally { Console.WriteLine("finally"); } } catch { Console.WriteLine("after finally"); }

            AppDomain.CurrentDomain.FirstChanceException -= OnFirst;
            try { throw new Exception("not seen"); } catch { Console.WriteLine("no handler"); }
            AppDomain.CurrentDomain.FirstChanceException += OnFirst;
            AppDomain.CurrentDomain.FirstChanceException += OnFirst;
            try { throw new Exception("twice"); } catch { Console.WriteLine("two handlers"); }
            AppDomain.CurrentDomain.FirstChanceException -= OnFirst;
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnFirst; }
        Console.WriteLine(s_count);
    }
}
