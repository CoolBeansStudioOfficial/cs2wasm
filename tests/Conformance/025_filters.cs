using System;
using System.Runtime.InteropServices;

static class Program
{
    sealed class CodeException : Exception
    {
        public int Code;
        public CodeException(int code) : base("code " + code) { Code = code; }
    }

    static bool Log(string s, bool r) { Console.WriteLine("filter " + s + " -> " + r); return r; }

    static void Inner(int code)
    {
        try { throw new CodeException(code); }
        finally { Console.WriteLine("inner finally"); }
    }

    static void Middle(int code)
    {
        try { Inner(code); }
        catch (CodeException e) when (Log("middle " + e.Code, e.Code == 2)) { Console.WriteLine("middle caught"); }
        finally { Console.WriteLine("middle finally"); }
    }

    static int Counted()
    {
        int seen = 0;
        for (int i = 0; i < 3; i++)
        {
            try { if (i != 1) throw new CodeException(i); }
            catch (CodeException e) when (e.Code == i && ++seen > 0) { Console.WriteLine("loop " + i); }
        }
        return seen;
    }

    static bool Throws() => throw new InvalidOperationException("in filter");

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // the filters run before any finally (two-pass), outermost catch takes it
        try { Middle(1); }
        catch (CodeException e) when (Log("outer " + e.Code, true)) { Console.WriteLine("outer caught " + e.Message); }
        Console.WriteLine("--");
        // middle's filter accepts: only middle's filter runs
        try { Middle(2); }
        catch (CodeException e) when (Log("outer " + e.Code, true)) { Console.WriteLine("unexpected"); }
        Console.WriteLine("--");
        Console.WriteLine(Counted());
        // an exception inside a filter counts as false
        try
        {
            try { throw new CodeException(5); }
            catch (CodeException) when (Throws()) { Console.WriteLine("no"); }
        }
        catch (CodeException e) { Console.WriteLine("fell through " + e.Code); }
        // rethrow searches again
        try
        {
            try { throw new CodeException(7); }
            catch (CodeException e) when (e.Code == 7) { Console.WriteLine("first"); throw; }
            finally { Console.WriteLine("finally after rethrow"); }
        }
        catch (Exception e) when (Log("rethrown " + e.Message, true)) { Console.WriteLine("second"); }
        // a plain catch between stops the search
        try
        {
            try { Inner(9); } catch (CodeException) { Console.WriteLine("plain caught"); }
        }
        catch (CodeException) when (Log("never", true)) { }
    }
}
