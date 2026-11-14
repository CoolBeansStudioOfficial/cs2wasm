using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

static class Program
{
    sealed class CodeException : Exception { public int Code; public CodeException(int c) : base("code " + c) { Code = c; } }

    static async Task Fail(int code) { await Task.Yield(); throw new CodeException(code); }

    static async Task<string> Handle(int code)
    {
        try { await Fail(code); return "none"; }
        catch (CodeException e) when (e.Code == 1) { return "one"; }
        catch (CodeException e) when (e.Code == 2 && e.Message.Length > 0) { return "two"; }
        catch (CodeException e) { return "other " + e.Code; }
    }

    static async Task<string> Outer(int code)
    {
        try
        {
            try { await Fail(code); }
            catch (CodeException e) when (e.Code > 10) { return "inner " + e.Code; }
            return "no";
        }
        catch (CodeException e) when (e.Code < 10) { await Task.Yield(); return "outer " + e.Code; }
    }

    static IEnumerable<string> Iter(int code)
    {
        yield return "start";
        string r;
        try { if (code > 0) throw new CodeException(code); r = "ok"; }
        catch (CodeException e) when (e.Code % 2 == 0) { r = "even " + e.Code; }
        catch (CodeException e) { r = "odd " + e.Code; }
        yield return r;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Handle(1).Result + " " + Handle(2).Result + " " + Handle(3).Result);
        Console.WriteLine(Outer(5).Result + " " + Outer(20).Result);
        Console.WriteLine(string.Join(",", Iter(0)) + " " + string.Join(",", Iter(4)) + " " + string.Join(",", Iter(7)));
        try { Outer(10).Wait(); } catch (AggregateException e) { Console.WriteLine("escaped " + e.InnerException.Message); }
    }
}
