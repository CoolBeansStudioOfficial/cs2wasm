// Structs of sizes that are not a multiple of 4 (explicit Size), copied every way: local, argument, result,
// array element, field, static, box, Nullable box, span, multi-dimensional element. Every byte must survive.
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Explicit, Size = 3)] struct T3 { [FieldOffset(0)] public byte A; }
[StructLayout(LayoutKind.Explicit, Size = 13)] struct T13 { [FieldOffset(0)] public byte A; }
[StructLayout(LayoutKind.Sequential, Size = 6)] struct T6 { public short A; }
class H<T> where T : struct { public T V; public int Tail = 0x11223344; }
static class St<T> where T : struct { public static T V; }

static class Program
{
    static T Fill<T>(byte seed) where T : struct
    {
        T v = default;
        var b = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref v, 1));
        for (int i = 0; i < b.Length; i++) b[i] = (byte)(seed + i * 3);
        return v;
    }
    static string Dump<T>(ref T v) where T : struct => Convert.ToHexString(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref v, 1)));
    static T Pass<T>(T v) where T : struct => v;

    static void All<T>(byte seed) where T : struct
    {
        var v = Fill<T>(seed);
        var l = v; var a = Pass(v);
        var arr = new T[3]; arr[1] = v; var e = arr[1];
        var h = new H<T> { V = v }; var f = h.V;
        St<T>.V = v; var s = St<T>.V;
        object box = v; var u = (T)box;
        object nb = (T?)v; var n = ((T?)nb).GetValueOrDefault();
        Span<T> sp = arr; sp[0] = v; var x = sp[0];
        var g = new T[2, 2]; g[1, 0] = v; var c = g[1, 0];
        Console.WriteLine(Marshal.SizeOf<T>() + " " + Dump(ref l) + " " + Dump(ref a) + " " + Dump(ref e) + " " + Dump(ref arr[0]) + Dump(ref arr[2]) + " " + Dump(ref f) + " " + h.Tail
            + " " + Dump(ref s) + " " + Dump(ref u) + " " + Dump(ref n) + " " + Dump(ref x) + " " + Dump(ref c) + Dump(ref g[0, 1]));
    }

    static void Main() { All<T3>(1); All<T13>(2); All<T6>(3); }
}
