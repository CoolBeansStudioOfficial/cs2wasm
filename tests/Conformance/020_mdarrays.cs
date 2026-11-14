using System;
using System.Runtime.InteropServices;

static class Program
{
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        var grid = new int[3, 4];
        for (int y = 0; y < grid.GetLength(0); y++)
            for (int x = 0; x < grid.GetLength(1); x++)
                grid[y, x] = y * 10 + x;
        Console.WriteLine(grid[2, 3] + " " + grid.Length + " " + grid.Rank + " " + grid.GetUpperBound(1));
        int sum = 0;
        foreach (var v in grid) sum += v;
        Console.WriteLine(sum);
        var names = new string[,] { { "a", "b" }, { "c", "d" } };
        Console.WriteLine(names[1, 0] + names[0, 1]);
        names[0, 0] = "z";
        foreach (var n in names) Console.Write(n);
        Console.WriteLine();
        var cube = new double[2, 2, 2];
        cube[1, 1, 1] = 2.5;
        cube[0, 1, 0] += 1;
        Console.WriteLine(cube[1, 1, 1] + cube[0, 1, 0] + " " + cube.Length);
        try { Console.WriteLine(grid[3, 0]); } catch (IndexOutOfRangeException e) { Console.WriteLine(e.Message); }
        Console.WriteLine(grid.GetType() + " " + new int[1][].GetType());
        var board = new char[2, 3];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) board[i, j] = (char)('a' + i * 3 + j);
        Console.WriteLine(new string(new[] { board[0, 0], board[1, 2] }));
    }
}
