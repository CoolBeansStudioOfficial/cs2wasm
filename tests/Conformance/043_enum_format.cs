using System;
using System.Runtime.InteropServices;

enum Color { Red, Green, Blue }
enum Sparse { A = 1, B = 10, C = 100, Dup = 10 }
[Flags] enum Perm { None = 0, Read = 1, Write = 2, Exec = 4, ReadWrite = Read | Write, All = 7 }
[Flags] enum NoZero { X = 1, Y = 2, Z = 8 }
enum Small : sbyte { Neg = -2, Zero = 0, Pos = 3 }
enum Big : ulong { Max = ulong.MaxValue, One = 1 }
enum Wide : long { Low = long.MinValue, Mid = 0 }
enum Bytes : byte { B0, B200 = 200 }
enum Shorts : short { M = -300, P = 300 }

static class Program
{
    static string Fmt(Enum e, string f) => e.ToString(f);

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        Console.WriteLine(Color.Green + " " + (Color)7 + " " + (Color)(-1));
        Console.WriteLine(Sparse.B + " " + (Sparse)10 + " " + (Sparse)11 + " " + Sparse.C);
        Console.WriteLine(Perm.None + "|" + Perm.Read + "|" + (Perm.Read | Perm.Exec) + "|" + Perm.ReadWrite + "|" + (Perm)7 + "|" + (Perm)8 + "|" + (Perm)15);
        Console.WriteLine((NoZero)0 + "|" + (NoZero)3 + "|" + (NoZero)4 + "|" + (NoZero)11);
        Console.WriteLine(Small.Neg + " " + (Small)(-5) + " " + Big.Max + " " + (Big)2 + " " + Wide.Low + " " + (Wide)(-7));
        Console.WriteLine(Bytes.B200 + " " + (Bytes)201 + " " + Shorts.M + " " + (Shorts)(-1));
        foreach (var f in new[] { "G", "g", "D", "d", "X", "x", "F", "f" })
            Console.WriteLine(f + ": " + Fmt(Color.Blue, f) + " " + Fmt((Perm)6, f) + " " + Fmt((Color)5, f) + " " + Fmt(Small.Neg, f) + " " + Fmt(Big.Max, f) + " " + Fmt((Shorts)(-1), f) + " " + Fmt((NoZero)9, f));
        Console.WriteLine($"{Color.Red} {Perm.Write | Perm.Read} {Small.Pos:D} {Bytes.B200:X} {(Perm)3:F} {Color.Green,8}|{Color.Blue,-6}|");
        Console.WriteLine(string.Format("{0} {1:X} {2:D}", Perm.All, Shorts.P, (NoZero)2));
        try { Color.Red.ToString("Q"); } catch (FormatException e) { Console.WriteLine("FormatException " + e.Message); }
        try { Color.Red.ToString("GG"); } catch (FormatException) { Console.WriteLine("FormatException GG"); }
        object boxed = Sparse.Dup;
        Console.WriteLine(boxed.ToString() + " " + ((IFormattable)boxed).ToString("D", null) + " " + Enum.GetName(typeof(Color), 1));
    }
}
