// ExceptionDispatchInfo.SetCurrentStackTrace: the trace of where it is called, as .NET writes it (cs2wasm wrote
// nothing: .NET's own text reads each frame's MethodBase, and frames had none).
using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Exception Make(string m)
    {
        var e = new InvalidOperationException(m);
        ExceptionDispatchInfo.SetCurrentStackTrace(e);
        return e;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var e = Make("made");
        var lines = e.StackTrace!.Split('\n');
        foreach (var l in lines)
        {
            var t = l.TrimEnd('\r');
            // (the oracle runs inside the test runner, whose frames are below Main)
            if (t.TrimStart().StartsWith("at ") && !t.TrimStart().StartsWith("at P.")) continue;
            // (file paths differ between the two builds: the method and the line number are what is compared)
            int i = t.IndexOf(" in ");
            int ln = t.LastIndexOf(":line ");
            Console.WriteLine(i > 0 && ln > i ? t[..i] + " line " + t[(ln + 6)..] : t);
        }
        try { throw e; }
        catch (InvalidOperationException c) { Console.WriteLine(c.Message + " " + (c.StackTrace!.Contains("Make") ? "keeps where it was made" : "lost it")); }
    }
}
