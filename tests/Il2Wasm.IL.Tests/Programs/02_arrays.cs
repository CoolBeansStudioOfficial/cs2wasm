// Arrays of numbers and of references: new, length, loads and stores, bounds.
using System;

static class Program
{
    static int[] Squares(int n)
    {
        var a = new int[n];
        for (int i = 0; i < a.Length; i++) a[i] = i * i;
        return a;
    }

    static long Sum(int[] a)
    {
        long s = 0;
        foreach (var x in a) s += x;
        return s;
    }

    static void Sort(int[] a)
    {
        for (int i = 1; i < a.Length; i++)
        {
            int v = a[i], j = i - 1;
            while (j >= 0 && a[j] > v) { a[j + 1] = a[j]; j--; }
            a[j + 1] = v;
        }
    }

    static int[][] Triangle(int n)
    {
        var rows = new int[n][];
        for (int i = 0; i < n; i++)
        {
            rows[i] = new int[i + 1];
            rows[i][0] = rows[i][i] = 1;
            for (int j = 1; j < i; j++) rows[i][j] = rows[i - 1][j - 1] + rows[i - 1][j];
        }
        return rows;
    }

    static void Main()
    {
        var sq = Squares(10);
        Console.WriteLine(sq.Length);
        Console.WriteLine(Sum(sq));
        var a = new int[] { 5, 3, 9, 1, 7, 2, 8 };
        Sort(a);
        for (int i = 0; i < a.Length; i++) Console.Write(a[i]);
        Console.WriteLine();
        var t = Triangle(8);
        Console.WriteLine(t[7][3]);
        var bytes = new byte[4];
        bytes[0] = 255; bytes[1] = (byte)(bytes[0] + 2);
        Console.WriteLine(bytes[0] + bytes[1]);
        var longs = new long[3];
        longs[2] = 9223372036854775807L;
        Console.WriteLine(longs[2]);
        var chars = new char[] { 'h', 'i' };
        Console.WriteLine(chars[1]);
        object[] objs = new object[3];
        objs[0] = "x";
        objs[1] = objs[0];
        Console.WriteLine(objs[1] == objs[0]);
        Console.WriteLine(objs[2] == null);
    }
}
