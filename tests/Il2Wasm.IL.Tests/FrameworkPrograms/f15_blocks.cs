// initblk and cpblk (Unsafe.InitBlock, Unsafe.CopyBlock and their unaligned forms).
using System;
using System.Runtime.CompilerServices;

static unsafe class Program
{
    static void Main()
    {
        var a = new byte[16];
        fixed (byte* p = a) Unsafe.InitBlock(p + 2, 0xAB, 5);
        Unsafe.InitBlockUnaligned(ref a[9], 7, 3);
        var b = new byte[16];
        fixed (byte* s = a) fixed (byte* d = b) Unsafe.CopyBlock(d + 1, s, 12);
        Unsafe.CopyBlockUnaligned(ref b[13], ref a[2], 2);
        Console.WriteLine(Convert.ToHexString(a) + " " + Convert.ToHexString(b));
    }
}
