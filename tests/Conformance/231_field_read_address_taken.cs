// An instance field read in a loop while it is stored by its address, or through memory that overlaps it: stores
// the compiler cannot tell from the store's own code (Wasm/FieldLoads.cs leaves every field alone whose place some
// code has by address: Driver.NoteFieldEscape). A `ref` local, a `ref` and an `out` argument (the callee stores), a
// method that returns a `ref` to the field (the address taken in another function than the loop), `fixed` and a
// pointer, Interlocked, Volatile, Unsafe.AsRef and Unsafe.Add, a span made over the field, a struct's method called
// on a field of a struct's type, a struct assigned as a whole over the field read, a tuple and a nullable; a class
// with explicit layout whose fields overlap; a volatile field. Each field is used by one case only, so that each
// case stands on its own.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

struct Counter { public int N; public void Inc() { N++; } public void Set(int v) { N = v; } }
struct Pair { public int A, B; public Pair(int a, int b) { A = a; B = b; } }

sealed class Box
{
    public int RefLocal, RefArg, OutArg, RefRet, Fixed, Inter, Vol, AsRef, Span, First, Second, Quiet;
    public byte B0, B1, B2, B3;
    public int W0, W1, W2;
    public long Long;
    public string Text = "a";
    public string Swap = "s";
    public Counter Cnt;
    public Pair Pr;
    public (int a, int b) Tup;
    public int? Maybe;
    public volatile int Volatile;
    public ref int Ref() => ref RefRet;
}

[StructLayout(LayoutKind.Explicit)]
sealed class Overlap
{
    [FieldOffset(0)] public int A;
    [FieldOffset(0)] public int B;
    [FieldOffset(4)] public int C;
    [FieldOffset(0)] public long Wide;
}

static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)] static void Set(ref int x, int v) { x = v; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Make(out int x, int v) { x = v; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void SetText(ref string x, string v) { x = v; }

    static int RefLocal(Box b)
    {
        int s = 0;
        ref int r = ref b.RefLocal;
        for (int i = 0; i < 4; i++) { s += b.RefLocal; r = i * 3 + 1; s += b.RefLocal; }
        return s;
    }
    static int Args(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.RefArg; Set(ref b.RefArg, i + 10); s += b.RefArg; }
        for (int i = 0; i < 4; i++) { s += b.OutArg; Make(out b.OutArg, i + 20); s += b.OutArg; }
        for (int i = 0; i < 4; i++) { s += b.Text.Length; SetText(ref b.Text, new string('x', i + 2)); s += b.Text.Length; }
        return s;
    }
    static int RefReturn(Box b)
    {
        int s = 0;
        ref int r = ref b.Ref();
        for (int i = 0; i < 4; i++) { s += b.RefRet; r += 7; s += b.RefRet; }
        for (int i = 0; i < 4; i++) { s += b.RefRet; b.Ref() = i; s += b.RefRet; }
        return s;
    }
    static unsafe int Pointers(Box b)
    {
        int s = 0;
        fixed (int* p = &b.Fixed)
            for (int i = 0; i < 4; i++) { s += b.Fixed; *p = i + 30; s += b.Fixed; }
        fixed (long* q = &b.Long)
            for (int i = 0; i < 4; i++) { s += (int)b.Long; *(int*)q = i + 35; s += (int)b.Long; }
        return s;
    }
    static int Interlock(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.Inter; Interlocked.Increment(ref b.Inter); s += b.Inter; Interlocked.Add(ref b.Inter, 5); s += b.Inter; }
        for (int i = 0; i < 4; i++) { s += b.Swap.Length; var old = Interlocked.Exchange(ref b.Swap, new string('y', i + 3)); s += b.Swap.Length + old.Length; }
        for (int i = 0; i < 4; i++) { s += b.Vol; Volatile.Write(ref b.Vol, i + 40); s += b.Vol + Volatile.Read(ref b.Vol); }
        return s;
    }
    static int Unsafes(Box b)
    {
        int s = 0;
        ref int r = ref Unsafe.AsRef(in b.AsRef);
        for (int i = 0; i < 4; i++) { s += b.AsRef; r = i + 50; s += b.AsRef; }
        // (the field after First, reached from First's address)
        ref int first = ref b.First;
        for (int i = 0; i < 4; i++) { s += b.First; Unsafe.Add(ref first, 0) = i + 60; s += b.First; }
        var span = MemoryMarshal.CreateSpan(ref b.Span, 1);
        for (int i = 0; i < 4; i++) { s += b.Span; span[0] = i + 70; s += b.Span; }
        for (int i = 0; i < 4; i++) { s += b.Span; span.Fill(i + 80); s += b.Span; }
        return s;
    }
    // a reference to one field walked to another: the field read is not the one whose address was taken
    static unsafe int Walked(Box b)
    {
        int s = 0;
        ref byte rb = ref b.B0;
        byte c = b.B2; Unsafe.Add(ref rb, 2) = 77; s += c + b.B2;
        for (int i = 0; i < 4; i++) { s += b.B3; Unsafe.Add(ref rb, 3) = (byte)(i + 1); s += b.B3; }
        ref int rw = ref b.W0;
        for (int i = 0; i < 4; i++) { s += b.W1 + b.W2; Unsafe.Add(ref rw, 1) = i + 5; Unsafe.Add(ref rw, 2) += 3; s += b.W1 + b.W2; }
        var span = MemoryMarshal.CreateSpan(ref b.W0, 3);
        for (int i = 0; i < 4; i++) { s += b.W2; span[2] = i * 9; s += b.W2; }
        fixed (int* p = &b.W0)
            for (int i = 0; i < 4; i++) { s += b.W1; p[1] = i + 40; s += b.W1; }
        return s;
    }
    static int Structs(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.Cnt.N; b.Cnt.Inc(); s += b.Cnt.N; }
        for (int i = 0; i < 4; i++) { s += b.Cnt.N; b.Cnt.Set(i + 90); s += b.Cnt.N; b.Cnt.N += 2; s += b.Cnt.N; }
        // a struct assigned as a whole over the fields read
        for (int i = 0; i < 4; i++) { s += b.Pr.A + b.Pr.B; b.Pr = new Pair(i, i + 1); s += b.Pr.A + b.Pr.B; }
        for (int i = 0; i < 4; i++) { s += b.Pr.A; b.Pr = default; s += b.Pr.A; b.Pr.A = i + 5; }
        for (int i = 0; i < 4; i++) { s += b.Tup.a + b.Tup.b; b.Tup = (i + 3, i + 4); s += b.Tup.a; b.Tup.b++; s += b.Tup.b; }
        for (int i = 0; i < 4; i++) { s += b.Maybe ?? -1; b.Maybe = i == 2 ? null : i + 6; s += b.Maybe.GetValueOrDefault(); }
        ref Pair rp = ref b.Pr;
        for (int i = 0; i < 4; i++) { s += b.Pr.B; rp.B = i + 100; s += b.Pr.B; rp = new Pair(1, i); s += b.Pr.B; }
        return s;
    }
    static long Overlaps(Overlap o)
    {
        long s = 0;
        for (int i = 0; i < 4; i++) { s += o.A; o.B = i * 7 + 1; s += o.A; }
        for (int i = 0; i < 4; i++) { s += o.A + o.C; o.Wide = ((long)(i + 2) << 32) | (uint)(i + 3); s += o.A + o.C; }
        for (int i = 0; i < 4; i++) { s += o.Wide; o.C = i + 9; s += o.Wide; }
        return s;
    }
    static int Volatiles(Box b)
    {
        int s = 0;
        for (int i = 0; i < 4; i++) { s += b.Volatile; b.Volatile = i + 110; s += b.Volatile; }
        for (int i = 0; i < 4; i++) { s += b.Volatile; Set(ref b.Second, b.Volatile + 1); s += b.Second; }
        return s;
    }
    // (nothing stores Quiet: its reads may be made once)
    static int Quiet(Box b)
    {
        int s = 0;
        ref int r = ref b.RefLocal;
        for (int i = 0; i < 4; i++) { s += b.Quiet; r = i; Set(ref b.RefArg, i); b.Cnt.Inc(); b.Pr = new Pair(i, i); }
        return s + b.RefLocal + b.RefArg + b.Cnt.N + b.Pr.A;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        var b = new Box { Quiet = 5 };
        Console.WriteLine("ref local " + RefLocal(b));
        Console.WriteLine("arguments " + Args(b));
        Console.WriteLine("ref return " + RefReturn(b));
        Console.WriteLine("pointers " + Pointers(b));
        Console.WriteLine("interlocked " + Interlock(b));
        Console.WriteLine("unsafe " + Unsafes(b));
        Console.WriteLine("walked " + Walked(b));
        Console.WriteLine("structs " + Structs(b));
        Console.WriteLine("overlap " + Overlaps(new Overlap()));
        Console.WriteLine("volatile " + Volatiles(b));
        Console.WriteLine("quiet " + Quiet(b));
    }
}
