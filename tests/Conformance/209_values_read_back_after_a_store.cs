// A place stored and then read (Wasm/SameBaseLoads.cs: the read is the value that was stored when it is the same
// variable plus the same offset, the variable was not assigned since, and every store between is to other bytes of
// that object): `--p.Life > 0`, a field set and used at once, compound assignments, mostly in loops (there a null
// check is written out and the place's address is the variable itself). What the read must see is printed. Where
// it is not simply the stored value: a place narrower than the value (a byte, a short, a char, a bool: the read
// sees what fits), a float that is a NaN, the variable assigned inside the stored value's own code or between the
// store and the read (the read is of another object), a store between that overlaps the place (explicit layout) or
// goes through another variable, a ref or a pointer to it, a call between. CS2WASM_SAME_BASE_WITHOUT=overlap, other,
// calls, assigned, value or address: a line each prints something else.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class P { public int Life; public long Big; public float F; public double D; public byte B; public sbyte Sb; public short S; public ushort Us; public char C; public bool Flag; public P? Next; public int Other; }
[StructLayout(LayoutKind.Explicit)] sealed class Over { [FieldOffset(0)] public int A; [FieldOffset(2)] public short Mid; [FieldOffset(0)] public long Wide; [FieldOffset(4)] public int High; [FieldOffset(8)] public int Apart; }
struct V { public int X; public byte B; public float F; }

static class Program
{
    static int s_calls;
    [MethodImpl(MethodImplOptions.NoInlining)] static void Touch(P p) { p.Life += 1000; s_calls++; }
    [MethodImpl(MethodImplOptions.NoInlining)] static P Make(int k) => new P { Life = k, Big = k * 1000000007L, F = k + 0.5f, D = k + 0.25, B = (byte)(k * 37), Sb = (sbyte)(k * 11), S = (short)(k * 1234), Us = (ushort)(k * 4321), C = (char)('a' + k), Other = k };

    [MethodImpl(MethodImplOptions.NoInlining)] static string Whole(P[] ps, int k)
    {
        long a = 0, b = 0; float c = 0; double d = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            p.Life = p.Life * 3 + k; a += p.Life;
            p.Big = p.Big * 5 - k; b += p.Big;
            p.F = p.F * 1.5f + k; c += p.F;
            p.D = p.D / 3 + k; d += p.D;
            p.Life += 7; p.Other = p.Life; a += p.Other * 2 + p.Life;
        }
        return a + " " + b + " " + c + " " + d;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static long Narrow(P[] ps, int k)
    {
        long sum = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            p.B = (byte)(p.B + k); sum = sum * 3 + p.B;
            p.Sb = (sbyte)(p.Sb - k); sum = sum * 3 + p.Sb;
            p.S = (short)(p.S * k); sum = sum * 3 + p.S;
            p.Us = (ushort)(p.Us * k + 7); sum = sum * 3 + p.Us;
            p.C = (char)(p.C + k * 1000); sum = sum * 3 + p.C;
            p.Flag = k > 3; sum = sum * 3 + (p.Flag ? 1 : 0);
            p.B += 200; sum = sum * 3 + p.B;
            p.Sb += 100; sum = sum * 3 + p.Sb;
            p.S += 30000; sum = sum * 3 + p.S;
            p.Us -= 60000; sum = sum * 3 + p.Us;
            p.C += (char)60000; sum = sum * 3 + p.C;
            p.B++; sum = sum * 3 + p.B; p.Sb--; sum = sum * 3 + p.Sb;
            unchecked { p.B = (byte)(k * 1000003); p.S = (short)(k * 1000003); }
            sum = sum * 3 + p.B + p.S;
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string NaNs(P[] ps, float zero)
    {
        int nans = 0; long bits = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            p.F = zero / zero; if (p.F != p.F) nans++;
            p.F = p.F + i; if (float.IsNaN(p.F)) nans += 10;
            p.D = (double)(zero / zero) * 2; if (p.D != p.D) nans += 100;
            p.F = float.PositiveInfinity - float.PositiveInfinity + zero; if (!(p.F == p.F)) nans += 1000;
            p.F = i * 0.5f; if (p.F == p.F) bits += (long)(p.F * 4);
        }
        return nans + " " + bits;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static int CountDown(P[] ps)
    {
        int alive = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (--p.Life > 0) alive++;
            if ((p.Other -= 3) < 0) alive += 100;
            if (++p.B == 0) alive += 10000;
        }
        return alive;
    }
    // the variable assigned in the stored value's own code, and between the store and the read
    [MethodImpl(MethodImplOptions.NoInlining)] static long Assigned(P[] ps, P[] qs)
    {
        long sum = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            P p = ps[i], q = qs[i];
            p.Other = 1;
            p.Life = (p = q).Life + 1;      // stored into the first object; p is the second from here
            sum = sum * 7 + p.Life;
            p.Big = 77; p = p.Next!; sum = sum * 7 + p.Big;
            p.Other = 5; p = ps[i]; sum = sum * 7 + p.Other + p.Life;
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static long Between(P[] ps, P[] others, Over[] os)
    {
        long sum = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            P p = ps[i], same = others[i];
            var o = os[i];
            sum += p.Other + same.Other + o.Apart;
            p.Life = 10; same.Life = 20; sum = sum * 5 + p.Life;
            p.Life = 60; p.Other = 61; p.Big = 62; sum = sum * 5 + p.Life;
            o.A = 0x11223344; o.Mid = 0x7777; sum = sum * 5 + o.A;
            o.High = 5; o.Wide = 0x0102030405060708; sum = sum * 5 + o.High;
            o.Wide = -1; o.A = 0; sum = sum * 5 + o.Wide;
            o.Wide = 3; o.Apart = 9; sum = sum * 5 + o.Wide;
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static long Calls(P[] ps)
    {
        long sum = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            sum += p.Other;
            p.Life = 50; Touch(p); sum = sum * 5 + p.Life;
            p.Big = 5; sum = sum * 5 + p.Big;
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static unsafe long Pointers(V[] vs, P[] ps)
    {
        long sum = 0;
        fixed (V* q0 = vs)
            for (int i = 0; i < ps.Length; i++)
            {
                V* q = q0 + i;
                var p = ps[i];
                ref int life = ref p.Life;
                int* raw = &q->X;
                q->X = 5; *raw = 6; sum = sum * 5 + q->X;
                q->B = 200; q->B += 100; sum = sum * 5 + q->B;
                life = 9; p.Life = 8; sum = sum * 5 + life;
                p.Life = 7; life = 6; sum = sum * 5 + p.Life;
                p.Other = 4; *raw = 1; sum = sum * 5 + p.Other + q->X;
            }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Arrays(int[] xs, byte[] bs, int i, int j)
    {
        xs[i] = 100; xs[j] = 200; int a = xs[i];
        bs[i] = 250; bs[i] += 10; int b = bs[i];
        xs[i] += 1; int c = xs[i];
        return a + " " + b + " " + c;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static string Branches(P p, bool t)
    {
        p.Life = 1;
        if (t) p.Life = 2;
        int a = p.Life;
        p.Other = 3;
        for (int i = 0; i < 3; i++) { int was = p.Other; p.Other = was * 2 + i; }
        int b = p.Other;
        p.Big = t ? 4 : 5; long c = p.Big;
        return a + " " + b + " " + c;
    }
    static P[] Many(int n, int from) { var a = new P[n]; for (int i = 0; i < n; i++) { a[i] = Make(from + i); a[i].Next = Make(from + 100 + i); a[i].Next!.Big = 4242 + i; } return a; }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Console.WriteLine("whole: " + Whole(Many(4, 3), 4) + " | " + Whole(Many(3, -7), 100000));
        Console.WriteLine("narrow: " + Narrow(Many(3, 3), 5) + " | " + Narrow(Many(2, 60), 77));
        Console.WriteLine("NaNs: " + NaNs(Many(3, 1), 0f));
        var ps = Many(9, 0);
        for (int i = 0; i < ps.Length; i++) ps[i].B = (byte)(250 + i);
        Console.WriteLine("count down: " + CountDown(ps) + " " + CountDown(ps) + " " + CountDown(ps) + " " + ps[2].Life + " " + ps[8].Other + " " + ps[7].B);
        var a1 = Many(3, 10); var q1 = Many(3, 20);
        Console.WriteLine("assigned: " + Assigned(a1, q1) + " " + a1[0].Life + " " + q1[0].Life + " " + q1[2].Next!.Big);
        var same = Many(3, 30);
        var os = new Over[3]; for (int i = 0; i < 3; i++) os[i] = new Over();
        Console.WriteLine("between, the same: " + Between(same, same, os));
        Console.WriteLine("between, apart: " + Between(Many(3, 31), Many(3, 32), os));
        Console.WriteLine("calls: " + Calls(Many(3, 33)) + " " + s_calls);
        var vs = new V[3];
        Console.WriteLine("pointers: " + Pointers(vs, Many(3, 40)) + " " + vs[1].X + " " + vs[2].B);
        Console.WriteLine("arrays: " + Arrays(new int[4], new byte[4], 1, 1) + " | " + Arrays(new int[4], new byte[4], 1, 2));
        Console.WriteLine("branches: " + Branches(Make(50), true) + " | " + Branches(Make(51), false));
    }
}
