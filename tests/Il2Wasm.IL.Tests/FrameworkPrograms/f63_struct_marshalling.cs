// Marshal.SizeOf, OffsetOf, StructureToPtr, PtrToStructure and DestroyStructure on the native layouts of structures:
// sequential, explicit and packed layouts, Size, bool (BOOL) and char (by CharSet), ByValArray and ByValTStr, nested
// structures, and strings round-tripped through pointers. Sizes and offsets are printed only for structures without
// pointer fields: those differ between wasm32 (4-byte pointers) and the desktop oracle.
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct Plain { public int A; public short B; public byte C; public long D; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct Packed { public byte A; public int B; public short C; }

[StructLayout(LayoutKind.Explicit, Size = 16)]
struct Expl { [FieldOffset(0)] public int A; [FieldOffset(8)] public double B; }

[StructLayout(LayoutKind.Sequential)]
struct WithBool { public bool B; public int X; public bool C; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct WithChar { public char C; public short S; public char D; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
struct WithCharAnsi { public char C; public int S; }

[StructLayout(LayoutKind.Sequential)]
struct Arr { public byte X; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public int[] V; }

[StructLayout(LayoutKind.Sequential)]
struct Nested { public Plain P; public int Z; }

[StructLayout(LayoutKind.Sequential)]
struct Fixed { public int N; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)] public string T; }

[StructLayout(LayoutKind.Sequential)]
struct BoolArr { public byte K; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public bool[] F; }

[StructLayout(LayoutKind.Sequential, Size = 24)]
struct Sized { public short A; public int B; }

[StructLayout(LayoutKind.Sequential)]
struct Strs { public int N; public string A; [MarshalAs(UnmanagedType.LPWStr)] public string W; }

static class Program
{
    static void Main()
    {
        // SizeOf of several layouts (no pointer fields)
        Console.WriteLine("sizes " + Marshal.SizeOf<Plain>() + " " + Marshal.SizeOf<Packed>() + " " + Marshal.SizeOf<Expl>() + " " + Marshal.SizeOf<WithBool>()
            + " " + Marshal.SizeOf<WithChar>() + " " + Marshal.SizeOf<WithCharAnsi>() + " " + Marshal.SizeOf<Arr>() + " " + Marshal.SizeOf<Nested>()
            + " " + Marshal.SizeOf<Fixed>() + " " + Marshal.SizeOf<BoolArr>() + " " + Marshal.SizeOf<Sized>() + " " + Marshal.SizeOf(typeof(Packed)));
        Console.WriteLine("primitives " + Marshal.SizeOf<int>() + " " + Marshal.SizeOf<bool>() + " " + Marshal.SizeOf<char>() + " " + Marshal.SizeOf<long>());

        // OffsetOf, generic and by Type
        Console.WriteLine("offsets " + Marshal.OffsetOf<Plain>("D") + " " + Marshal.OffsetOf<Plain>("C") + " " + Marshal.OffsetOf(typeof(Packed), "C")
            + " " + Marshal.OffsetOf<Expl>("B") + " " + Marshal.OffsetOf<WithBool>("C") + " " + Marshal.OffsetOf<WithChar>("D")
            + " " + Marshal.OffsetOf<Arr>("V") + " " + Marshal.OffsetOf<Nested>("Z") + " " + Marshal.OffsetOf<Fixed>("T"));

        // blittable structures: a round trip through native memory
        var p = new Plain { A = 1, B = -2, C = 3, D = 4000000000L };
        IntPtr mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(p, mem, false);
        Console.WriteLine("plain bytes " + Marshal.ReadInt32(mem, 0) + " " + Marshal.ReadInt16(mem, 4) + " " + Marshal.ReadByte(mem, 6) + " " + Marshal.ReadInt64(mem, 8));
        var q = Marshal.PtrToStructure<Plain>(mem);
        Console.WriteLine("plain back " + q.A + " " + q.B + " " + q.C + " " + q.D);
        Marshal.FreeHGlobal(mem);

        var ex = new Expl { A = 5, B = 2.5 };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(ex, mem, false);
        Console.WriteLine("expl bytes " + Marshal.ReadInt32(mem, 0) + " " + BitConverter.Int64BitsToDouble(Marshal.ReadInt64(mem, 8)));
        Marshal.FreeHGlobal(mem);

        // bool: a 4-byte BOOL by default
        var wb = new WithBool { B = true, X = 7, C = false };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(wb, mem, false);
        Console.WriteLine("bool bytes " + Marshal.ReadInt32(mem, 0) + " " + Marshal.ReadInt32(mem, 4) + " " + Marshal.ReadInt32(mem, 8));
        var wb2 = Marshal.PtrToStructure<WithBool>(mem);
        Console.WriteLine("bool back " + wb2.B + " " + wb2.X + " " + wb2.C);
        Marshal.FreeHGlobal(mem);

        // char: one byte under CharSet.Ansi, two under Unicode
        var wc = new WithCharAnsi { C = 'x', S = -5 };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(wc, mem, false);
        Console.WriteLine("char ansi bytes " + Marshal.ReadByte(mem, 0) + " " + Marshal.ReadInt32(mem, 4));
        var wc2 = Marshal.PtrToStructure<WithCharAnsi>(mem);
        Console.WriteLine("char ansi back " + wc2.C + " " + wc2.S);
        Marshal.FreeHGlobal(mem);

        var wu = new WithChar { C = 'y', S = 300, D = 'z' };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(wu, mem, false);
        Console.WriteLine("char uni bytes " + Marshal.ReadInt16(mem, 0) + " " + Marshal.ReadInt16(mem, 2) + " " + Marshal.ReadInt16(mem, 4));
        var wu2 = Marshal.PtrToStructure<WithChar>(mem);
        Console.WriteLine("char uni back " + wu2.C + " " + wu2.S + " " + wu2.D);
        Marshal.FreeHGlobal(mem);

        // ByValArray: the elements inline, after the byte that precedes them
        var ar = new Arr { X = 9, V = new[] { 1, 2, 3, 4 } };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(ar, mem, false);
        Console.WriteLine("arr bytes " + Marshal.ReadByte(mem, 0) + " " + Marshal.ReadInt32(mem, 4) + " " + Marshal.ReadInt32(mem, 8) + " " + Marshal.ReadInt32(mem, 16));
        var ar2 = Marshal.PtrToStructure<Arr>(mem);
        Console.WriteLine("arr back " + ar2.X + " " + ar2.V.Length + " " + string.Join(",", ar2.V));
        Marshal.FreeHGlobal(mem);

        var ba = new BoolArr { K = 2, F = new[] { true, false, true } };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(ba, mem, false);
        Console.WriteLine("boolarr bytes " + Marshal.ReadByte(mem, 0) + " " + Marshal.ReadInt32(mem, 4) + " " + Marshal.ReadInt32(mem, 8) + " " + Marshal.ReadInt32(mem, 12));
        var ba2 = Marshal.PtrToStructure<BoolArr>(mem);
        Console.WriteLine("boolarr back " + ba2.K + " " + ba2.F[0] + " " + ba2.F[1] + " " + ba2.F[2]);
        Marshal.FreeHGlobal(mem);

        // ByValTStr: inline characters, zero-padded
        var fx = new Fixed { N = 4, T = "abc" };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(fx, mem, false);
        Console.WriteLine("fixed bytes " + Marshal.ReadInt32(mem, 0) + " " + Marshal.ReadByte(mem, 4) + " " + Marshal.ReadByte(mem, 6) + " " + Marshal.ReadByte(mem, 7));
        var fx2 = Marshal.PtrToStructure<Fixed>(mem);
        Console.WriteLine("fixed back " + fx2.N + " [" + fx2.T + "]");
        Marshal.FreeHGlobal(mem);

        // nested structures
        var ne = new Nested { P = new Plain { A = 11, B = 12, C = 13, D = 14 }, Z = 15 };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(ne, mem, false);
        var ne2 = Marshal.PtrToStructure<Nested>(mem);
        Console.WriteLine("nested back " + ne2.P.A + " " + ne2.P.B + " " + ne2.P.C + " " + ne2.P.D + " " + ne2.Z + " " + Marshal.ReadInt32(mem, 16));
        Marshal.FreeHGlobal(mem);

        // strings through pointers: the values come back (the pointers' sizes differ by target, so none are printed)
        var s = new Strs { N = 3, A = "hello", W = "wide" };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(s, mem, false);
        var s2 = Marshal.PtrToStructure<Strs>(mem);
        Console.WriteLine("strs back " + s2.N + " " + s2.A + " " + s2.W);
        Marshal.DestroyStructure<Strs>(mem);
        Marshal.FreeHGlobal(mem);

        // a structure with an explicit Size
        var sz = new Sized { A = 1, B = 2 };
        mem = Marshal.AllocHGlobal(64);
        Marshal.StructureToPtr(sz, mem, false);
        Console.WriteLine("sized " + Marshal.ReadInt16(mem, 0) + " " + Marshal.ReadInt32(mem, 4) + " " + Marshal.PtrToStructure<Sized>(mem).B);
        Marshal.FreeHGlobal(mem);
    }
}
