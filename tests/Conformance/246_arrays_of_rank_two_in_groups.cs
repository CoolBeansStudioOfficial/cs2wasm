// An array of two (and three) dimensions of a class that can be on a cycle, held in a member's field: the group
// goes when nothing refers to it, as under .NET (it stayed for good: the array was no member and its elements were
// counted references no link knew of). A board whose cells refer back to it; cells that refer to each other
// across the array; a cell replaced and cleared; the array replaced by another; the board in a list that lives on
// while boards are dropped; an array of rank two of a struct with a reference in it.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Cell
{
    public static int Live;
    public Board? Owner; public Cell? Other; public int Id;
    public Cell(int id) { Id = id; Live++; }
    ~Cell() { Live--; }
}

struct Pair { public Cell? A; public int N; }

sealed class Board
{
    public static int Live;
    public Cell?[,] Cells; public Cell?[,,]? Cube; public Pair[,]? Pairs;
    public Board(int w, int h) { Cells = new Cell?[w, h]; Live++; }
    ~Board() { Live--; }
}

static class Program
{
    static int s_next;
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"boards {Board.Live} cells {Cell.Live}"; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static Board Make(int w, int h, bool back)
    {
        var b = new Board(w, h);
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                var c = new Cell(s_next++);
                if (back) c.Owner = b;
                b.Cells[x, y] = c;
            }
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                b.Cells[x, y]!.Other = b.Cells[(x + 1) % w, (y + 1) % h];
        return b;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static long Sum(Board b)
    {
        long t = 0;
        foreach (var c in b.Cells) if (c != null) t += c.Id + (c.Other?.Id ?? 0) * 3 + (c.Owner == b ? 1000 : 0);
        if (b.Cube != null) foreach (var c in b.Cube) if (c != null) t += c.Id * 7;
        if (b.Pairs != null) foreach (var p in b.Pairs) t += (p.A?.Id ?? 0) * 11 + p.N;
        return t;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void One(bool back)
    {
        var b = Make(3, 4, back);
        Console.WriteLine($"made {Sum(b)}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Changes()
    {
        var b = Make(4, 4, true);
        b.Cells[1, 1] = new Cell(s_next++) { Owner = b };
        b.Cells[2, 2] = null;
        b.Cells[0, 3] = b.Cells[3, 0];
        b.Cells[0, 0]!.Other = b.Cells[0, 0];
        Console.WriteLine($"changed {Sum(b)}");
        b.Cube = new Cell?[2, 2, 2];
        b.Cube[1, 0, 1] = b.Cells[0, 0]; b.Cube[0, 1, 1] = new Cell(s_next++) { Owner = b };
        b.Pairs = new Pair[2, 3];
        b.Pairs[1, 2].A = b.Cells[3, 3]; b.Pairs[1, 2].N = 5; b.Pairs[0, 0] = new Pair { A = new Cell(s_next++) { Owner = b }, N = 9 };
        Console.WriteLine($"more arrays {Sum(b)}");
        var old = b.Cells;
        b.Cells = new Cell?[2, 2];
        b.Cells[1, 1] = old[1, 1]; b.Cells[0, 1] = new Cell(s_next++) { Owner = b, Other = old[3, 3] };
        Console.WriteLine($"array replaced {Sum(b)}");
        s_keep = b;
    }

    static Board? s_keep;
    static readonly List<Board> s_boards = new();

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Many()
    {
        for (int i = 0; i < 20; i++) s_boards.Add(Make(2 + i % 3, 3, i % 2 == 0));
        for (int i = s_boards.Count - 1; i >= 0; i -= 2) s_boards.RemoveAt(i);
        // (cells of one board referring to another's)
        s_boards[0].Cells[0, 0]!.Other = s_boards[1].Cells[1, 1];
        s_boards[1].Cells[1, 1]!.Owner = s_boards[0];
        long t = 0; foreach (var b in s_boards) t += Sum(b);
        Console.WriteLine($"many {s_boards.Count} {t}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        One(false);
        Console.WriteLine("no back references: " + Counts());
        One(true);
        Console.WriteLine("back references: " + Counts());
        Changes();
        // (the cells are not counted while the board lives: the cells of an array that was replaced, which refer
        // back to the board, stay until the board goes, with an array of one dimension too; docs/AUDIT.md)
        Collect();
        Console.WriteLine($"kept: boards {Board.Live} {Sum(s_keep!)}");
        s_keep = null;
        Console.WriteLine("let go: " + Counts());
        Many();
        Console.WriteLine("half of many: " + Counts());
        s_boards.RemoveAt(0);
        Console.WriteLine("one less: " + Counts());
        s_boards.Clear();
        Console.WriteLine("none: " + Counts());
    }
}
