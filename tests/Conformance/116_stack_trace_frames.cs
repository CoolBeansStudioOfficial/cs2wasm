// System.Diagnostics.StackTrace and StackFrame of the running code (section 6.3): the frames with file and line,
// frames skipped, the text, Environment.StackTrace, and an exception's frames.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Box
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string Where(int skip) => Program.Line(new StackFrame(skip, true));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public StackTrace Trace(int skip) => new StackTrace(skip, true);
}

static class Program
{
    static string Name(string? path) => path == null ? "?" : path.Substring(Math.Max(path.LastIndexOf((char)47), path.LastIndexOf((char)92)) + 1);

    public static string Line(StackFrame? f) => f == null ? "null" : Name(f.GetFileName()) + ":" + f.GetFileLineNumber();

    // the frames' text down to Main (what runs Main is the host's)
    static void Print(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            int i = t.IndexOf(" in "), l = t.LastIndexOf(":line ");
            Console.WriteLine(i > 0 && l > i ? t.Substring(0, i) + t.Substring(l) : t);
            if (t.StartsWith("at Program.Main()")) break;
        }
    }

    static void Frames(StackTrace st)
    {
        for (int i = 0; i < st.FrameCount; i++)
        {
            var f = st.GetFrame(i)!;
            // (what runs Main is the host's)
            if (!Line(f).StartsWith("116_")) break;
            Console.Write(Line(f) + " ");
        }
        Console.WriteLine();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static StackTrace A(int skip) => B(skip);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static StackTrace B(int skip) => new Box().Trace(skip);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Env() => Environment.StackTrace;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Thrower(int depth) { if (depth == 0) throw new InvalidOperationException("deep"); Thrower(depth - 1); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Here() => new StackFrame(0, true).GetFileLineNumber();

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Here());
        Frames(A(0));
        Frames(A(1));
        Frames(A(2));
        Console.WriteLine(A(1000).FrameCount + " " + (A(1000).GetFrame(0) == null) + " " + (A(0).GetFrame(-1) == null));
        Print(A(0).ToString());
        Print(A(1).ToString());
        Print(Env());
        Console.WriteLine(Env().StartsWith("   at System.Environment.get_StackTrace()"));
        var box = new Box();
        Console.WriteLine(box.Where(0) + " " + box.Where(1) + " " + (new StackFrame(100000, true).GetFileName() == null));
        Console.WriteLine(new StackFrame(0, false).GetFileName() == null);
        Console.WriteLine(new StackFrame(0, false).GetFileLineNumber());
        Console.WriteLine(new StackTrace(false).GetFrame(0)!.GetFileLineNumber());
        Console.WriteLine(new StackTrace().FrameCount > 0);
        Console.WriteLine(new StackTrace(true).GetFrames().Length == new StackTrace(true).FrameCount);
        try { Thrower(2); }
        catch (Exception e)
        {
            var st = new StackTrace(e, true);
            Console.WriteLine(st.FrameCount);
            for (int i = 0; i < st.FrameCount; i++) Console.Write(Line(st.GetFrame(i)) + " ");
            Console.WriteLine();
            Print(st.ToString());
            Console.WriteLine(new StackTrace(e, 2, true).FrameCount + " " + Line(new StackTrace(e, 2, true).GetFrame(0)));
            Console.WriteLine(new StackTrace(e, false).GetFrame(0)!.GetFileLineNumber());
        }
        Console.WriteLine(new StackTrace(new Exception("never thrown"), true).FrameCount);
    }
}
