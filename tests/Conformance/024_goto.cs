using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

static class Program
{
    sealed class Node { public int V; public Node(int v) { V = v; } }

    static int Find(int[,] grid, int target)
    {
        for (int y = 0; y < grid.GetLength(0); y++)
            for (int x = 0; x < grid.GetLength(1); x++)
                if (grid[y, x] == target) { Console.WriteLine("found at " + y + "," + x); goto done; }
        Console.WriteLine("not found");
        return -1;
    done:
        return target;
    }

    static int Retry()
    {
        int attempts = 0;
    again:
        var n = new Node(attempts);   // declared after the label: released on each backward goto
        attempts++;
        if (n.V < 3) goto again;
        return attempts;
    }

    static string Classify(int v)
    {
        string r = "";
        switch (v)
        {
            case 0: r += "zero "; goto case 1;
            case 1: r += "small "; break;
            case 5: r += "five "; goto default;
            default: r += "other"; break;
        }
        return r;
    }

    static IEnumerable<int> Gen()
    {
        int i = 0;
    top:
        yield return i;
        if (++i < 3) goto top;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var g = new int[3, 3];
        g[2, 1] = 7;
        Console.WriteLine(Find(g, 7) + " " + Find(g, 9));
        Console.WriteLine(Retry());
        Console.WriteLine(Classify(0) + "|" + Classify(1) + "|" + Classify(5) + "|" + Classify(9));
        Console.WriteLine(string.Join(",", Gen()));
        int k = 0;
        try
        {
        loop:
            try { k++; if (k < 4) goto loop; }
            finally { Console.Write("f" + k + " "); }
        }
        finally { Console.WriteLine("outer"); }
        Console.WriteLine(k);
    }
}
