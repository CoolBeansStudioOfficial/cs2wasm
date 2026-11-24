// The IR inliner (docs/MEMORY-IR.md 3.7, Ir/Inline.cs; on with IL2WASM_MEM_PLAN=1): what inlining must keep.
// - What an inlined callee does is still seen by the next round's facts: lowering merges an inlined callee's
//   summary (Lower/Phases.cs). Without that, the field Set writes (Set is inlined at its only site) looks unwritten,
//   so Peek's value folds to 0 and Main's branch on it is decided wrongly; and Note2's argument from the inlined
//   Twice is missing, so Note2 folds its parameter to the other site's 4.
// - callvirt on null throws at the call, before the callee runs, even when the callee never dereferences this
//   (IL2WASM_INLINE_NULL_BREAK=1 inlines those anyway: no exception).
// - A callee that may throw keeps its frame in the trace (IL2WASM_INLINE_TRACE_BREAK=1 inlines it: the top frame
//   becomes Main). It is NoOptimization so that .NET's JIT does not inline it either.
using System;
using System.Runtime.CompilerServices;

static class Acc
{
    public static int Seen;
    public static void Note(int x) { Seen += x; }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Note2(int x) { if (x == 4) Seen += 100; else Seen += x; }
}

sealed class Cell
{
    int _v;
    public int[] Items;
    public void Set(int v) { _v = v; }
    public int Get() => _v;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Peek() => _v;
    public Cell Self() => this;
    public void Nop() { }
    [MethodImpl(MethodImplOptions.NoOptimization)]
    public int First() => Items[0];
}

sealed class Wrap
{
    public void Twice(int x) { Acc.Note(x); Acc.Note(x); Acc.Note2(x); }
}

static class Program
{
    static Cell s_cell = new Cell();
    static Cell s_null;

    static string Top(Exception e)
    {
        var first = (e.StackTrace ?? "").Split('\n')[0].Trim();
        int at = first.IndexOf(" in ");
        return at >= 0 ? first.Substring(0, at) : first;
    }

    static void Main()
    {
        s_cell.Set(5);
        Console.WriteLine("field " + s_cell.Get());
        Console.WriteLine(s_cell.Peek() == 0 ? "peek: zero" : "peek: set");
        Acc.Note2(4);
        new Wrap().Twice(3);
        Console.WriteLine("static " + Acc.Seen);
        try { s_null.Nop(); Console.WriteLine("nop: no exception"); }
        catch (NullReferenceException) { Console.WriteLine("nop: null"); }
        try { s_null.Self(); Console.WriteLine("self: no exception"); }
        catch (NullReferenceException) { Console.WriteLine("self: null"); }
        try { Console.WriteLine(s_null.Get()); }
        catch (NullReferenceException e) { Console.WriteLine("get: null at " + Top(e)); }
        try { Console.WriteLine(new Cell().First()); }
        catch (NullReferenceException e) { Console.WriteLine("first: null at " + Top(e)); }
    }
}
