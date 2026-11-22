// A field read twice with stores between (Wasm/SameBaseLoads.cs: the second read is the first one's value when
// every store between is to other bytes of the same object, through the same variable): each case reads a place,
// changes something, and reads the place again; what the second read must see is printed. The cases where it must
// see a new value: the place itself stored; stored through another variable that holds the same object, through a
// ref to the field, through a ref that walks to it, through a pointer; a field that overlaps it (explicit layout,
// narrower and wider); a call that stores it (a method, a property, a constructor's side effect, a delegate); the
// variable assigned another object between; an array's element stored by an index that is the same one. The cases
// where it is the same value: stores to the object's other fields (floats with NaNs among them, bytes side by
// side, longs), in loops and straight lines.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class P { public float X, Y, Vx, Vy; public int Life; public long Big; public byte B0, B1, B2, B3; public double D; public P? Next; public int V { get; set; } }
[StructLayout(LayoutKind.Explicit)] sealed class Over { [FieldOffset(0)] public int A; [FieldOffset(2)] public short Mid; [FieldOffset(0)] public long Wide; [FieldOffset(4)] public int High; [FieldOffset(8)] public int Apart; }
struct S { public int X, Y; public float F; }

static class Program
{
    static int s_calls;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Bump(P p) { p.Life += 100; s_calls++; }
    [MethodImpl(MethodImplOptions.NoInlining)] static P Other(P p) => new P { Life = p.Life * 2, Vy = p.Vy + 1, Next = p };

    [MethodImpl(MethodImplOptions.NoInlining)] static string SamePlace(P p)
    {
        int a = p.Life; p.Life = a + 1; int b = p.Life;
        float f = p.Vy; p.Vy = f * 2; float g = p.Vy;
        byte c = p.B1; p.B1 = (byte)(c + 1); byte d = p.B1;
        return a + " " + b + " " + f + " " + g + " " + c + " " + d;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string OtherVariable(P p, P q)
    {
        int a = p.Life; q.Life = a + 7; int b = p.Life;
        float f = p.Vy; q.Vy = 9.5f; float g = p.Vy;
        long l = p.Big; q.Big = l - 1; long m = p.Big;
        return a + " " + b + " " + f + " " + g + " " + l + " " + m;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string ThroughRef(P p)
    {
        ref int r = ref p.Life;
        int a = p.Life; r = a + 3; int b = p.Life;
        ref byte rb = ref p.B0;
        byte c = p.B2; Unsafe.Add(ref rb, 2) = 77; byte d = p.B2;
        return a + " " + b + " " + c + " " + d;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static unsafe string ThroughPointer(ref S s)
    {
        fixed (S* q = &s)
        {
            int a = s.X; q->X = a + 5; int b = s.X;
            float f = s.F; *(int*)&q->F = 0x40490fdb; float g = s.F;
            return a + " " + b + " " + f + " " + g.ToString("F2");
        }
    }
    // (a pointer and a ref moved on between the reads: the same variable, another place)
    [MethodImpl(MethodImplOptions.NoInlining)] static unsafe string Moved(S[] arr)
    {
        fixed (S* q0 = arr)
        {
            S* q = q0;
            int a = q->X; q->Y = a + 1; q++; int b = q->X;
            float f = q->F; q->X = 3; q--; float g = q->F;
            ref int r = ref arr[0].X;
            int c = r; Unsafe.Add(ref r, 1) = 8; r = ref Unsafe.Add(ref r, 3); int d = r;
            q = q0;
            int y0 = q->Y; q->X = 11; q++; int x1 = q->X; int y1 = q->Y;
            int sum = 0;
            q = q0;
            for (int k = 0; k < arr.Length - 1; k++) { int y = q->Y; q->X = y + 1; q++; sum = sum * 100 + q->Y + y; }
            return a + " " + b + " " + f + " " + g + " " + c + " " + d + " " + y0 + " " + x1 + " " + y1 + " " + sum;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Overlapping(Over o)
    {
        int a = o.A; o.Mid = 0x1234; int b = o.A;
        int h = o.High; o.Wide = 0x0102030405060708; int i = o.High; int a2 = o.A;
        long w = o.Wide; o.High = -1; long w2 = o.Wide;
        int far = o.Apart; o.Wide = 5; int far2 = o.Apart;
        return a.ToString("x") + " " + b.ToString("x") + " " + h + " " + i.ToString("x") + " " + a2.ToString("x") + " " + w.ToString("x") + " " + w2.ToString("x") + " " + far + " " + far2;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Calls(P p, Action<P> act)
    {
        int a = p.Life; Bump(p); int b = p.Life;
        int c = p.V; p.V = c + 1; int d = p.V;
        int e = p.Life; act(p); int f = p.Life;
        int g = p.Life; var o = Other(p); int h = p.Life + o.Life;
        return a + " " + b + " " + c + " " + d + " " + e + " " + f + " " + g + " " + h;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Reassigned(P p, P q)
    {
        // (q and its Next are read first: no null test stands between the reads below)
        int z = q.Life; var n = q.Next!; float nf = n.Vy;
        int a = p.Life; p.Y = 1; p = q; int b = p.Life;
        float f = p.Vy; p.X = 2; p = n; float g = p.Vy;
        long l = p.Big; p.D = 3; p = q; long m = p.Big;
        return a + " " + b + " " + f + " " + g + " " + l + " " + m + " " + z + " " + nf;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Elements(int[] arr, P[] ps, int i, int j)
    {
        int a = arr[i]; arr[j] = a + 1; int b = arr[i];
        var x = ps[i]; ps[j] = ps[j + 1]; var y = ps[i];
        int c = ps[i].Life; ps[j].Life = c + 50; int d = ps[i].Life;
        return a + " " + b + " " + ReferenceEquals(x, y) + " " + c + " " + d;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Branches(P p, bool flag)
    {
        int a = p.Life; if (flag) p.Life = a + 9; int b = p.Life;
        float f = p.Vy; p.Y = flag ? f : -f; float g = p.Vy;
        int c = p.Life; for (int k = 0; k < 3; k++) { p.X += 1; if (k == 1) p.Life++; } int d = p.Life;
        return a + " " + b + " " + f + " " + g + " " + c + " " + d;
    }
    // other fields stored between: the second read is the same value
    [MethodImpl(MethodImplOptions.NoInlining)] static string OtherFields(P p)
    {
        p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.5f; p.X += p.Vx; p.Y += p.Vy;
        byte c = p.B0; p.B1 = 200; p.B3 = 201; byte d = p.B0; p.B2 = (byte)(c + d);
        long l = p.Big; p.Life = 5; p.D = l; long m = p.Big; p.Big = l + m;
        double e = p.D; p.X = (float)e; p.Y = (float)p.D;
        return p.X + " " + p.Y + " " + p.Vy + " " + c + " " + d + " " + p.B2 + " " + p.Big + " " + p.D + " " + p.Life;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string NaNs(P p)
    {
        // (a NaN made here is read back as the one NaN, twice)
        p.X = p.Vx - p.Vx * p.Vy; p.Y = p.X + p.X; float a = p.X; p.Vy = 1; float b = p.X;
        return float.IsNaN(a) + " " + float.IsNaN(b) + " " + BitConverter.SingleToInt32Bits(a).ToString("x") + " " + BitConverter.SingleToInt32Bits(b).ToString("x") + " " + BitConverter.SingleToInt32Bits(p.Y).ToString("x");
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Loop(P[] ps)
    {
        long sum = 0;
        for (int r = 0; r < 4; r++)
            for (int i = 0; i < ps.Length; i++)
            {
                var p = ps[i];
                p.X += p.Vx; p.Y += p.Vy; p.Vy += 0.25f;
                if (--p.Life > 0) sum += p.Life + (long)p.Y;
                p.Next = ps[(i + 1) % ps.Length]; p.Next.Vy -= 0.125f; sum += (long)(p.Vy * 8) + (long)(p.Next.Vy * 8);
            }
        return sum.ToString();
    }

    static P Make(int n) => new P { X = n, Y = n * 2, Vx = 0.5f * n, Vy = 1.5f + n, Life = 10 + n, Big = 1000L * n, B0 = (byte)n, B1 = (byte)(n + 1), B2 = (byte)(n + 2), B3 = (byte)(n + 3), D = n * 0.25, V = n };

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Console.WriteLine("same place: " + SamePlace(Make(1)));
        var p = Make(2);
        Console.WriteLine("other variable, same object: " + OtherVariable(p, p));
        Console.WriteLine("other variable, another object: " + OtherVariable(Make(3), Make(4)));
        Console.WriteLine("through a ref: " + ThroughRef(Make(5)));
        var s = new S { X = 6, Y = 7, F = 1.5f };
        Console.WriteLine("through a pointer: " + ThroughPointer(ref s));
        Console.WriteLine("moved on: " + Moved(new[] { new S { X = 1, Y = 2, F = 0.5f }, new S { X = 30, Y = 40, F = 2.5f }, new S { X = 5, Y = 6, F = 7 } }));
        Console.WriteLine("overlapping: " + Overlapping(new Over { Wide = 0x1111222233334444, Apart = 42 }));
        Console.WriteLine("calls: " + Calls(Make(8), x => x.Life -= 1000) + " " + s_calls);
        var q = Make(9); q.Next = Make(10);
        Console.WriteLine("reassigned: " + Reassigned(Make(11), q));
        var ps = new P[4]; for (int i = 0; i < 4; i++) ps[i] = Make(20 + i);
        Console.WriteLine("elements, the same index: " + Elements(new[] { 5, 6, 7 }, ps, 1, 1));
        Console.WriteLine("elements, another index: " + Elements(new[] { 5, 6, 7 }, ps, 0, 2));
        Console.WriteLine("branches: " + Branches(Make(12), true) + " | " + Branches(Make(13), false));
        Console.WriteLine("other fields: " + OtherFields(Make(14)));
        var nan = Make(15); nan.Vx = float.PositiveInfinity; nan.Vy = 1;
        Console.WriteLine("NaNs: " + NaNs(nan));
        Console.WriteLine("loop: " + Loop(ps));
    }
}
