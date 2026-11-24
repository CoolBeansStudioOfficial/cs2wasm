// Pinned locals (SPEC 6.5): `fixed` keeps the object alive (and in place) for its block, even when nothing else holds
// it; the pointer is used after the array's last use as a reference and after other allocations that would take its
// memory. An array, a string, a span's storage, a field of a new object.
using System;
using System.Runtime.CompilerServices;

static unsafe class Program
{
    class Holder { public int A = 3, B = 4; }

    [MethodImpl(MethodImplOptions.NoInlining)] static byte[] Make() => new byte[] { 7, 8 };
    [MethodImpl(MethodImplOptions.NoInlining)] static string MakeText() => new string('x', 3) + "yz";
    [MethodImpl(MethodImplOptions.NoInlining)] static void Churn() { for (int i = 0; i < 20; i++) { var x = new byte[2]; x[0] = 1; var s = new string('q', 5); GC.KeepAlive(x); GC.KeepAlive(s); } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int FromArray() { fixed (byte* p = Make()) { Churn(); return p[0] + p[1]; } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int FromString() { fixed (char* p = MakeText()) { Churn(); return p[0] + p[4]; } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int FromSpan() { Span<byte> s = Make(); fixed (byte* p = s) { Churn(); return p[1]; } }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int FromField() { fixed (int* p = &new Holder().B) { Churn(); return *p; } }

    static void Main()
    {
        Console.WriteLine(FromArray());
        Console.WriteLine(FromString());
        Console.WriteLine(FromSpan());
        Console.WriteLine(FromField());
    }
}
