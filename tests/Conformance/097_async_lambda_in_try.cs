// An async lambda or local function written inside a try block of its enclosing method: its own exceptions are
// its task's (the enclosing try and finally are not its handlers).
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
static class Program
{
    static string S1()
    {
        try
        {
            var t = Task.Run(async () => { await Task.Delay(20); throw new FormatException(); return 1; });
            try { return t.Wait(2000) ? "s1 done" : "s1 HANG " + t.Status; } catch (AggregateException e) { return "s1 " + e.InnerException!.GetType().Name; }
        }
        finally { Console.Write("(fin)"); }
    }
    static string S2()
    {
        var t = Task.Run(async () => { await Task.Delay(20); throw new FormatException(); return 1; });
        try { return t.Wait(2000) ? "s2 done" : "s2 HANG " + t.Status; } catch (AggregateException e) { return "s2 " + e.InnerException!.GetType().Name; }
    }
    static string S3()
    {
        try
        {
            Func<Task<int>> f = async () => { await Task.Delay(20); throw new FormatException(); };
            var t = f();
            try { return t.Wait(2000) ? "s3 done" : "s3 HANG " + t.Status; } catch (AggregateException e) { return "s3 " + e.InnerException!.GetType().Name; }
        }
        catch (InvalidOperationException) { return "outer"; }
    }
    static string S4()
    {
        try
        {
            Func<Task<int>> f = async () => { await Task.Yield(); try { throw new FormatException(); } catch (FormatException) { return 7; } };
            var t = f();
            return t.Wait(2000) ? "s4 " + t.Result : "s4 HANG " + t.Status;
        }
        finally { Console.Write("(fin)"); }
    }
    static string S5()
    {
        try
        {
            Func<int> f = () => { try { throw new FormatException(); } catch (FormatException) { return 7; } };
            Func<int> g = () => throw new FormatException();
            try { g(); } catch (FormatException) { return "s5 " + f(); }
            return "no";
        }
        finally { Console.Write("(fin)"); }
    }
    static string S6()
    {
        try
        {
            async Task<int> L() { await Task.Delay(20); throw new FormatException(); }
            var t = L();
            try { return t.Wait(2000) ? "s6 done" : "s6 HANG " + t.Status; } catch (AggregateException e) { return "s6 " + e.InnerException!.GetType().Name; }
        }
        finally { Console.Write("(fin)"); }
    }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(S2()); Console.WriteLine(S5()); Console.WriteLine(S4()); Console.WriteLine(S6()); Console.WriteLine(S3()); Console.WriteLine(S1());
    }
}
