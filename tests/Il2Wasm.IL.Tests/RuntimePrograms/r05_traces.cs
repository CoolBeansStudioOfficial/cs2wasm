// Stack traces (SPEC 6.3): an exception's StackTrace has the methods from the throw to the catching frame, with the
// file and line of each from the PDB (here only the line is printed: the file names of the two builds differ), the
// parameters' types and names, generic methods and nested types; a rethrow keeps the frames; a NullReferenceException
// and an IndexOutOfRangeException raised by the runtime name the method they happened in (their top frame only: .NET's
// optimized code gives an inexact line for the frame below).
using System;
using System.Runtime.CompilerServices;

static class Program
{
    static string Lines(string trace)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var raw in (trace ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int at = line.IndexOf(" in ");
            int ln = line.LastIndexOf(":line ");
            if (at >= 0 && ln > at) line = line.Substring(0, at) + " :" + line.Substring(ln + 1);
            sb.Append(line.Trim()).Append('\n');
        }
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Deepest(int depth, string why) => throw new InvalidOperationException(why + " at " + depth);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Middle(int depth, ref int counter)
    {
        counter++;
        Deepest(depth + 1, "middle");
    }

    class Outer
    {
        public class Inner
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public int Run<T>(T value, int[] items) { Middle(items.Length, ref items[0]); return 0; }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int NullLength(string s) => s.Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Index(int[] a, int i) => a[i];

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rethrows()
    {
        try { Deepest(9, "rethrown"); }
        catch (InvalidOperationException) { throw; }
    }

    static void Main()
    {
        try { new Outer.Inner().Run("x", new int[3]); }
        catch (Exception e) { Console.Write(Lines(e.StackTrace)); }
        try { NullLength(null); }
        catch (Exception e) { Console.WriteLine(e.GetType().Name + "\n" + Lines(e.StackTrace).Split('\n')[0]); }
        try { Index(new int[2], 5); }
        catch (Exception e) { Console.WriteLine(e.GetType().Name + "\n" + Lines(e.StackTrace).Split('\n')[0]); }
        try { Rethrows(); }
        catch (Exception e) { Console.Write(Lines(e.StackTrace)); }
    }
}
