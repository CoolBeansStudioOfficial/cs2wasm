// Exception filters in .NET's two passes (SPEC 3): a filter runs before the finally blocks of the frames it would
// unwind, and sees their state as it was at the throw; filters are asked innermost first, a catch without a filter
// in between stops the search; a filter that throws is false; a rethrow is filtered again; a filter reads and writes
// the method's locals and arguments; a static constructor's exception is not seen by the filters around it.
using System;

static class Program
{
    static int State;
    static string Log = "";
    static void Note(string s) { Log += s + ";"; }

    static bool Watch(string who, bool answer) { Note($"filter {who} sees {State}"); return answer; }

    static void Inner()
    {
        try { State = 1; throw new InvalidOperationException("inner"); }
        finally { Note("inner finally"); State = 2; }
    }

    static void Middle()
    {
        try { Inner(); }
        catch (ArgumentException) { Note("wrong catch"); }
        finally { Note("middle finally"); State = 3; }
    }

    static void TwoPass()
    {
        State = 0; Log = "";
        try { Middle(); }
        catch (InvalidOperationException e) when (Watch("outer", true)) { Note("caught " + e.Message + " state " + State); }
        Console.WriteLine(Log);
    }

    static void Declined()
    {
        State = 0; Log = "";
        try
        {
            try { Middle(); }
            catch (Exception) when (Watch("first", false)) { Note("not here"); }
        }
        catch (Exception e) when (Watch("second", true)) { Note("second caught " + e.Message); }
        Console.WriteLine(Log);
    }

    static void StoppedByCatch()
    {
        State = 0; Log = "";
        try
        {
            try { Inner(); }
            catch (InvalidOperationException) { Note("plain catch"); }
        }
        catch (Exception) when (Watch("never", true)) { Note("not here"); }
        Console.WriteLine(Log);
    }

    static bool Throws() => throw new FormatException("in filter");

    static void ThrowingFilter()
    {
        Log = "";
        try
        {
            try { throw new InvalidOperationException("x"); }
            catch (Exception) when (Throws()) { Note("not here"); }
        }
        catch (Exception e) { Note("outer got " + e.GetType().Name); }
        Console.WriteLine(Log);
    }

    static void Locals(int arg)
    {
        Log = "";
        int local = 10;
        string text = "start";
        try
        {
            local = 20; text = "changed"; arg += 1;
            throw new InvalidOperationException("locals");
        }
        catch (Exception) when (Watch($"locals {local} {text} {arg}", (local = 30) == 30)) { Note($"after {local} {arg}"); }
        Console.WriteLine(Log);
    }

    static void Rethrown()
    {
        State = 0; Log = "";
        try
        {
            try
            {
                try { throw new InvalidOperationException("again"); }
                catch (Exception) { Note("rethrowing"); State = 5; throw; }
                finally { State = 6; }
            }
            finally { Note("finally " + State); State = 7; }
        }
        catch (Exception e) when (Watch("rethrow", true)) { Note("caught " + e.Message + " " + State); }
        Console.WriteLine(Log);
    }

    class Bad { public static int V = Fail(); static int Fail() => throw new InvalidOperationException("cctor"); }

    static void Initializer()
    {
        Log = "";
        try { Console.WriteLine(Bad.V); }
        catch (Exception e) when (Watch(e.GetType().Name, true)) { Note("got " + e.InnerException?.Message); }
        Console.WriteLine(Log);
    }

    static void Main()
    {
        TwoPass();
        Declined();
        StoppedByCatch();
        ThrowingFilter();
        Locals(1);
        Rethrown();
        Initializer();
    }
}
