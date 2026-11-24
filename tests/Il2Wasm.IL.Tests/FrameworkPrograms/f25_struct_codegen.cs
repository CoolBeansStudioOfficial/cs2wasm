// Struct code generation: field reads where the struct is (Ir/StructReads.cs), frames given back at a catch instead of
// in a handler of each function (FunctionLowering.RestoreFrame, IL2WASM_NO_SP_AT_CATCH=1 restores the old shape),
// the back end's frame promotion (Wasm/FrameSlots.cs), and the IR inliner's struct arguments and results
// (Ir/Inline.cs, with IL2WASM_MEM_PLAN=1).
// - Reads through an address keep their checks: a null object's struct field, an index out of range, a static's
//   initializer, in the order .NET runs them.
// - A frame-only function has no handler of its own: the catch gives the stack back. Thrown through 200,000 times
//   from frames of struct temporaries, the stack must stay where it is (IL2WASM_SP_AT_CATCH_BREAK=1 skips the
//   catch's restore: the stack runs out, "stack ok: False" or a trap).
// - An inlined callee that writes its struct parameter writes its own copy, never the caller's value
//   (IL2WASM_INLINE_STRUCTS_BREAK=1 inlines such callees reading and writing the caller's value: "bump 1 2 3" becomes
//   "bump 1 2 4": Twice's second read of its parameter sees the inlined Bump's write).
using System;
using System.Runtime.CompilerServices;

struct V3
{
    public double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public double Len2 => X * X + Y * Y + Z * Z;
    public override string ToString() => X + "," + Y + "," + Z;
}

struct Seg { public V3 A, B; public int Tag; }
struct Small { public byte B; public short S; }

sealed class Holder { public Seg S; public V3[] Arr = new V3[3]; }

static class Statics
{
    public static V3 Origin = Init();
    static V3 Init() { Console.WriteLine("Statics init"); return new V3(1, 2, 3); }
}

static class Program
{
    static V3 Bump(V3 a) { a.X += 1; return a; }
    static double Twice(V3 a) => Bump(a).X + a.X;
    static void Fail(double x) => throw new InvalidOperationException("deep " + x);
    static double Sum(V3 a) => a.X + a.Y + a.Z;
    static V3 Pick(V3 a, V3 b, bool first) { if (first) return a; return b; }
    static double ViaIn(in V3 a) => a.X * 2;
    static int Depth(V3 v, int n)
    {
        var w = v + v;
        if (n == 0) Fail(w.X);
        return Depth(w * 0.5, n - 1) + 1;
    }
    static int Catch(int n)
    {
        try { return Depth(new V3(n, 0, 0), 5); }
        catch (InvalidOperationException) { return -1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static long StackMark() { Span<byte> b = stackalloc byte[16]; unsafe { fixed (byte* p = b) return (long)p; } }

    static void Main()
    {
        var a = new V3(1, 2, 3);
        var b = new V3(0.5, -1, 4);
        var c = a + b * 2 - a;
        Console.WriteLine(c + " " + c.Len2 + " " + Sum(c) + " " + ViaIn(c));
        var seg = new Seg { A = a, B = b, Tag = 7 };
        Console.WriteLine(seg.A.Y + seg.B.Z + seg.Tag + " " + (seg.A - seg.B).X);
        var h = new Holder { S = seg };
        h.Arr[1] = c;
        Console.WriteLine(h.S.B.X + " " + h.Arr[1].Z + " " + h.Arr[0].X + " " + Statics.Origin.Y);
        var sm = new Small { B = 200, S = -3 };
        Console.WriteLine(sm.B + sm.S);
        // a value read, then the variable written: the value is the one read
        var v = a;
        double before = v.X;
        v = b;
        Console.WriteLine(before + " " + v.X);
        Holder? none = null;
        try { Console.WriteLine(none!.S.A.X); } catch (NullReferenceException) { Console.WriteLine("null holder: NullReferenceException"); }
        try { Console.WriteLine(h.Arr[3].X); } catch (IndexOutOfRangeException) { Console.WriteLine("index: IndexOutOfRangeException"); }
        var bumped = Bump(a);
        Console.WriteLine("bump " + a.X + " " + bumped.X + " " + Twice(a));
        Console.WriteLine(Pick(a, b, true) + " " + Pick(a, b, false));
        // the stack after many exceptions through frames of struct temporaries
        long mark = StackMark();
        int caught = 0;
        for (int i = 0; i < 200000; i++)
        {
            if (Catch(i) == -1) caught++;
            // (a catch in this function: the frames the exception left are given back here, not at a return)
            try { Depth(new V3(i, 1, 0), 3); } catch (InvalidOperationException) { caught++; }
        }
        Console.WriteLine("caught " + caught + ", stack ok: " + (StackMark() == mark));
        // a loop of struct arithmetic (NBody's shape)
        var bodies = new V3[16];
        for (int i = 0; i < bodies.Length; i++) bodies[i] = new V3(i, i * 2 % 7, i * 3 % 5);
        var acc = new V3(0, 0, 0);
        for (int step = 0; step < 50; step++)
            for (int i = 0; i < bodies.Length; i++)
                for (int j = 0; j < bodies.Length; j++)
                {
                    if (i == j) continue;
                    var d = bodies[j] - bodies[i];
                    acc = acc + d * (1 / (d.Len2 + 0.01));
                }
        Console.WriteLine((long)(acc.X * 1000) + " " + (long)(acc.Y * 1000) + " " + (long)(acc.Z * 1000));
    }
}
