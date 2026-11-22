// Test 213's chains where the program has a way to tie one that no store of the class shows: reflection
// (FieldInfo.SetValue links the last chunk to the first), a `ref` to the link handed out by the class and written by
// the caller, an [UnsafeAccessor] to the private link; and StringBuilder with its chunks enumerated (GetChunks: the
// enumerator passes its current chunk by reference from a field). Each ring must be freed when dropped, and the
// builders give the same text and the same chunks.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

struct ByReflection { } struct ByRef { } struct Untouched { }

sealed class Chunk<M> where M : struct
{
    Chunk<M>? _prev;
    public int Id;
    public static int Ids, Live;

    public Chunk() { Id = ++Ids; Live++; }
    ~Chunk() { System.Threading.Interlocked.Decrement(ref Live); }
    Chunk(Chunk<M> from) { Id = ++Ids; Live++; _prev = from._prev; }
    public void Expand() { _prev = new Chunk<M>(this); }
    public void Cut() { var c = this; if (c._prev != null) c = c._prev; _prev = c._prev; }
    public Chunk<M>? Prev => _prev;
    public ref Chunk<M>? Link => ref _prev;
    public Chunk<M> Last() { var c = this; while (c._prev != null) c = c._prev; return c; }
    public static Chunk<M> Build(int n) { var head = new Chunk<M>(); for (int i = 1; i < n; i++) head.Expand(); return head; }
    public string Show() { var sb = new StringBuilder(); int n = 0; for (var c = this; c != null && n < 8; c = c._prev, n++) sb.Append(c.Id).Append(c._prev != null ? ">" : ""); return sb.ToString(); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return typeof(M).Name + " " + Live; }
}

// (the accessor's class: not generic, as an accessor's target is named)
sealed class Plain
{
    Plain? _prev;
    public int Id;
    public static int Ids, Live;
    public Plain() { Id = ++Ids; Live++; }
    ~Plain() { System.Threading.Interlocked.Decrement(ref Live); }
    Plain(Plain from) { Id = ++Ids; Live++; _prev = from._prev; }
    public void Expand() { _prev = new Plain(this); }
    public Plain Last() { var c = this; while (c._prev != null) c = c._prev; return c; }
    public string Show() { var sb = new StringBuilder(); int n = 0; for (var c = this; c != null && n < 8; c = c._prev, n++) sb.Append(c.Id).Append(c._prev != null ? ">" : ""); return sb.ToString(); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return "Plain " + Live; }
}

static class Program
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_prev")]
    static extern ref Plain? PrevOf(Plain c);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Rings()
    {
        var a = Chunk<ByReflection>.Build(4);
        typeof(Chunk<ByReflection>).GetField("_prev", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(a.Last(), a);
        Console.WriteLine("reflection " + a.Show());
        var b = Chunk<ByRef>.Build(4);
        b.Last().Link = b;
        Console.WriteLine("ref " + b.Show());
        var c = new Plain(); c.Expand(); c.Expand(); c.Expand();
        PrevOf(c.Last()) = c;
        Console.WriteLine("accessor " + c.Show());
        var d = Chunk<Untouched>.Build(5);
        d.Cut();
        Console.WriteLine("untouched " + d.Show());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Builders()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++) sb.Append("gem x").Append(i).Append('|');
        sb.Insert(100, new string('m', 50)).Remove(20, 150).Replace("gem", "g");
        var lengths = new List<int>();
        foreach (var m in sb.GetChunks()) lengths.Add(m.Length);
        Console.WriteLine(sb.Length + " " + string.Join("+", lengths) + " " + sb.ToString(0, 30));
        sb.Length = 9;
        lengths.Clear();
        foreach (var m in sb.GetChunks()) lengths.Add(m.Length);
        Console.WriteLine(sb.Length + " " + string.Join("+", lengths) + " " + sb);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Rings();
        Console.WriteLine(string.Join(", ", Chunk<ByReflection>.Left(), Chunk<ByRef>.Left(), Plain.Left(), Chunk<Untouched>.Left()));
        Builders();
    }
}
