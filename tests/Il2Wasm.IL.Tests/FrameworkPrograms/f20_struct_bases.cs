// An address made from a struct that holds a reference (a byref into the array a struct argument holds): the struct
// is a base of the address, so the array lives to the address's last use. Let go at the struct's own last use, the
// call, the array was freed under the byref and the next array of its size reused its memory.
using System;
using System.Runtime.CompilerServices;

struct Seg { public int[] A; public int I; }

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)] static Seg Make(int n)
    {
        var a = new int[8];
        for (int i = 0; i < a.Length; i++) a[i] = i * 10 + n;
        return new Seg { A = a, I = n % 8 };
    }

    [MethodImpl(MethodImplOptions.NoInlining)] static ref int At(Seg s) => ref s.A[s.I];

    static void Main()
    {
        long sum = 0;
        for (int n = 1; n < 30; n++)
        {
            ref int r = ref At(Make(n));
            // (an array of the same size, made while the byref is in use, takes freed memory first)
            var other = new int[8];
            for (int i = 0; i < other.Length; i++) other[i] = -1;
            sum += r * 3 + other[0];
        }
        Console.WriteLine(sum);
    }
}
