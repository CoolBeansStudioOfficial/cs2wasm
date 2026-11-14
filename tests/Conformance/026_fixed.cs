using System;
using System.Runtime.InteropServices;

static unsafe class Program
{
    struct Pair { public int A, B; }

    static int Sum(int* p, int n) { int s = 0; for (int i = 0; i < n; i++) s += p[i]; return s; }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var arr = new[] { 1, 2, 3, 4 };
        fixed (int* p = arr)
        {
            p[1] = 20;
            Console.WriteLine(Sum(p, arr.Length) + " " + *(p + 3));
        }
        Console.WriteLine(arr[1]);
        int[] none = null, empty = new int[0];
        fixed (int* a = none, b = empty) Console.WriteLine((a == null) + " " + (b == null));
        string s = "hello";
        fixed (char* c = s)
        {
            int n = 0;
            for (char* q = c; *q != '\0'; q++) n++;
            Console.WriteLine(n + " " + c[1]);
        }
        var pair = new Pair { A = 3, B = 4 };
        int* pb = &pair.B; *pb = 40;
        Console.WriteLine(pair.A + pair.B);
        var holder = new Pair[1];
        fixed (int* hb = &holder[0].B) { *hb = 7; }
        Console.WriteLine(holder[0].B);
        Span<int> span = arr;
        fixed (int* sp = span) { sp[0] = 100; }
        Console.WriteLine(arr[0]);
        var grid = new double[2, 2];
        fixed (double* g = grid) { g[3] = 1.5; }
        Console.WriteLine(grid[1, 1]);
        byte* buf = stackalloc byte[4];
        for (int i = 0; i < 4; i++) buf[i] = (byte)(i * 3);
        Console.WriteLine(buf[3]);
    }
}
