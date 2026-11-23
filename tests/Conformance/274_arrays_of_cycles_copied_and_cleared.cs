// Arrays of two and three dimensions and jagged arrays of a class that can be in a cycle, where the cells refer back
// to the array's holder and to each other: copied (Array.Copy, Clone), cleared, resized, read through GetValue and
// IList, enumerated; a static that holds such a structure, set by its initializer and set again; and a container
// let go while one of its items is held from elsewhere (the other items go with it: Rt.ShedHolds). What is alive
// after each step is what .NET says.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Cell
{
    public static int Live;
    public Grid? G; public Cell? Other; public Cell[,]? Around; public int Id;
    public Cell(int id, Grid? g) { Id = id; G = g; Live++; }
    ~Cell() { Live--; }
}

sealed class Grid
{
    public static int Live;
    public Cell[,]? A; public Cell[,,]? B; public Cell[][]? J; public Cell[]? V; public object? Any; public Grid? Twin;
    public Grid() { Live++; }
    ~Grid() { Live--; }
}

static class Program
{
    static Grid s_grid = Fill(new Grid(), 0);
    static List<Cell>? s_list;
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"grids {Grid.Live} cells {Cell.Live}"; }

    static Grid Fill(Grid g, int from)
    {
        g.A = new Cell[2, 3]; g.B = new Cell[2, 2, 2]; g.J = new Cell[2][]; g.V = new Cell[3];
        int id = from;
        for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) g.A[i, j] = new Cell(id++, g);
        for (int i = 0; i < 8; i++) g.B[i / 4, i / 2 % 2, i % 2] = new Cell(id++, g);
        for (int i = 0; i < 2; i++) { g.J[i] = new Cell[2]; for (int j = 0; j < 2; j++) g.J[i][j] = new Cell(id++, g) { Around = g.A }; }
        for (int i = 0; i < 3; i++) g.V[i] = new Cell(id++, g) { Other = g.A[0, i] };
        g.A[0, 0].Other = g.B[1, 1, 1]; g.B[1, 1, 1].Other = g.A[0, 0];
        return g;
    }

    static long Sum(Grid g)
    {
        long s = 0;
        if (g.A != null) foreach (var c in g.A) if (c != null) s += c.Id;
        if (g.B != null) foreach (Cell? c in (IEnumerable)g.B) if (c != null) s += c.Id * 3;
        if (g.J != null) foreach (var row in g.J) if (row != null) foreach (var c in row) if (c != null) s += c.Id * 5;
        if (g.V != null) s += g.V.Where(c => c != null).Sum(c => c.Id * 7);
        return s;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Ops(Grid g, int step)
    {
        switch (step)
        {
            case 1: { var to = new Cell[3, 3]; Array.Copy(g.A!, 0, to, 3, 6); g.A = to; break; }
            case 2: { try { Array.Copy(g.B!, new Cell[8], 8); } catch (RankException) { Console.Write("(ranks differ) "); } try { ((IList)g.B!)[0] = g.V![2]; } catch (ArgumentException) { Console.Write("(not a vector) "); } break; }
            case 3: { g.Any = g.B; g.B!.SetValue(g.V![2], 0, 0, 0); g.B = (Cell[,,])g.B.Clone(); break; }
            case 5: { var rows = g.J!; Array.Resize(ref rows, 3); rows[2] = rows[0]; rows[0] = new Cell[] { new Cell(-1, g) }; g.J = rows; break; }
            case 7: { foreach (var c in g.A!) if (c != null) c.Around = g.A; g.A = new Cell[1, 1]; break; }
            case 8: { g.Any = null; Array.Clear(g.B!); Array.Clear(g.V!); break; }
            case 9: { g.J = null; g.A = null; break; }
        }
    }

    // (nothing of the statics is read in Main: under .NET what Main reads stays in a slot of its frame)
    [MethodImpl(MethodImplOptions.NoInlining)] static string Sums() => Sum(s_grid).ToString();
    [MethodImpl(MethodImplOptions.NoInlining)] static void Step(int step) { Ops(s_grid, step); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Fresh(int from) { s_grid = Fill(new Grid(), from); }

    // a container let go while one of its items is held from elsewhere: the other items go with it
    // how: 0 the item is in a field of the holder too; 1 a cell of another array refers to it; 2 it is in a list
    // too; 3 as 0 without the cells' reference back to the holder; 4 a grid of two dimensions, two items held
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Held(int how)
    {
        var g = s_grid = new Grid();
        g.V = new Cell[4];
        for (int i = 0; i < 4; i++) g.V[i] = new Cell(i, how == 3 ? null : g);
        if (how is 0 or 3) g.Any = g.V[0];
        if (how == 1) { g.J = new Cell[1][]; g.J[0] = new Cell[1]; g.J[0][0] = new Cell(9, g) { Other = g.V[0] }; }
        if (how == 2) { s_list = new List<Cell> { g.V[0] }; }
        if (how == 4) { g.A = new Cell[2, 2]; for (int i = 0; i < 4; i++) g.A[i / 2, i % 2] = new Cell(10 + i, g); g.Any = g.A[1, 1]; g.V[0].Other = g.A[0, 0]; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void LetGo(int how, int part)
    {
        if (part == 0) { if (how == 4) s_grid.A = null; else s_grid.V = null; }
        else { s_grid.Any = null; s_grid.J = null; s_list = null; s_grid.V = null; }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Console.WriteLine("start: " + Counts() + " " + Sums());
        int[][] runs = { new[] { 9 }, new[] { 1, 9 }, new[] { 5, 9 }, new[] { 3, 8, 9 }, new[] { 7, 8, 9 }, new[] { 1, 2 }, new[] { 3 }, new[] { 5 }, new[] { 7 }, new[] { 8 } };
        for (int r = 0; r < runs.Length; r++)
        {
            Fresh(100 * r);
            foreach (int step in runs[r])
            {
                Step(step);
                Console.WriteLine($"run {r} step {step}: " + Counts() + " " + Sums());
            }
        }
        for (int how = 0; how <= 4; how++)
        {
            Held(how);
            Console.WriteLine($"held {how}: " + Counts());
            LetGo(how, 0);
            Console.WriteLine($"held {how}, the array let go: " + Counts());
            LetGo(how, 1);
            Console.WriteLine($"held {how}, the item let go: " + Counts());
        }
    }
}
