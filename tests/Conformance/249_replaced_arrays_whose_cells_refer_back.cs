// An array of a member that lives on is replaced by another; its cells, which refer back to the member, go with
// it, as under .NET (they stayed until the member's group went). Arrays of one, two and three dimensions, jagged
// ones, lists, with and without a local that holds the old array for a while, and cells that refer to each other.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Cell
{
    public static int Live;
    public Board? B; public Cell? Other; public Cell[]? Row;
    public Cell(Board? b) { B = b; Live++; }
    ~Cell() { Live--; }
}

sealed class Board
{
    public static int Live;
    public Cell[]? Cells;
    public Cell[,]? Grid;
    public Cell[,,]? Cube;
    public Cell[][]? Rows;
    public List<Cell>? List;
    public Board? Next;
    public Board() { Live++; }
    ~Board() { Live--; }
}

static class Program
{
    static Board s_board = new Board();
    static void Collect() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } }
    static string Counts() { Collect(); return $"boards {Board.Live} cells {Cell.Live}"; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Fill(Board b, int kind)
    {
        // 1: links between neighbours too; 2: each cell refers to its own row
        b.Cells = new Cell[4];
        for (int i = 0; i < 4; i++) b.Cells[i] = new Cell(b);
        b.Grid = new Cell[2, 3];
        for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) b.Grid[i, j] = new Cell(b);
        b.Cube = new Cell[2, 2, 2];
        for (int i = 0; i < 8; i++) b.Cube[i / 4, i / 2 % 2, i % 2] = new Cell(b);
        b.Rows = new Cell[3][];
        for (int i = 0; i < 3; i++) { b.Rows[i] = new Cell[2]; for (int j = 0; j < 2; j++) b.Rows[i][j] = new Cell(b); }
        b.List = new List<Cell>();
        for (int i = 0; i < 5; i++) b.List.Add(new Cell(b));
        if (kind == 1)
        {
            for (int i = 0; i < 4; i++) b.Cells[i].Other = b.Cells[(i + 1) % 4];
            b.Grid[0, 0].Other = b.Grid[1, 2]; b.Grid[1, 2].Other = b.Grid[0, 0];
            b.Cube[0, 0, 0].Other = b.Cells[0];
            b.List[0].Other = b.List[4]; b.List[4].Other = b.List[0];
        }
        if (kind == 2)
        {
            for (int i = 0; i < 4; i++) b.Cells[i].Row = b.Cells;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++) b.Rows[i][j].Row = b.Rows[i];
        }
    }

    // how: 0 stored over; 1 a local holds the old one across the store; 2 set to null; 3 the old one's cells
    // cleared one by one after it was replaced
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Replace(Board b, int what, int how)
    {
        switch (what)
        {
            case 0:
                if (how == 0) b.Cells = new Cell[4];
                else if (how == 1) { var old = b.Cells; b.Cells = new Cell[4]; Use(old); }
                else if (how == 2) b.Cells = null;
                else { var old = b.Cells!; b.Cells = new Cell[2]; for (int i = 0; i < old.Length; i++) old[i] = null!; }
                break;
            case 1:
                if (how == 0) b.Grid = new Cell[2, 3];
                else if (how == 1) { var old = b.Grid; b.Grid = new Cell[1, 1]; Use(old); }
                else if (how == 2) b.Grid = null;
                else { var old = b.Grid!; b.Grid = new Cell[2, 2]; for (int i = 0; i < 2; i++) for (int j = 0; j < 3; j++) old[i, j] = null!; }
                break;
            case 2:
                if (how == 0) b.Cube = new Cell[2, 2, 2];
                else if (how == 1) { var old = b.Cube; b.Cube = new Cell[1, 1, 1]; Use(old); }
                else if (how == 2) b.Cube = null;
                else { var old = b.Cube!; b.Cube = new Cell[1, 2, 1]; Array.Clear(old); }
                break;
            case 3:
                if (how == 0) b.Rows = new Cell[3][];
                else if (how == 1) { var old = b.Rows; b.Rows = new Cell[1][]; Use(old); }
                else if (how == 2) b.Rows = null;
                else { var old = b.Rows!; b.Rows = new Cell[2][]; for (int i = 0; i < old.Length; i++) old[i] = null!; }
                break;
            default:
                if (how == 0) b.List = new List<Cell>();
                else if (how == 1) { var old = b.List; b.List = new List<Cell>(); Use(old); }
                else if (how == 2) b.List = null;
                else { var old = b.List!; b.List = new List<Cell>(); old.Clear(); }
                break;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Use(object? o) { if (o == null) Console.WriteLine("null"); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Under(int kind) { var b = new Board { Next = s_board.Next }; s_board.Next = b; Fill(b, kind); }

    // (not in Main: under .NET a board read there would stay in a slot of Main's frame)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReplaceUnder()
    {
        for (int what = 0; what <= 4; what++) { Replace(s_board.Next!, what, what % 4); Replace(s_board.Next!.Next!, what, (what + 1) % 4); }
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Console.WriteLine("start: " + Counts());
        for (int kind = 0; kind <= 2; kind++)
            for (int how = 0; how <= 3; how++)
            {
                Fill(s_board, kind);
                Console.WriteLine($"kind {kind} filled: " + Counts());
                for (int what = 0; what <= 4; what++)
                {
                    Replace(s_board, what, how);
                    Console.WriteLine($"kind {kind} how {how} what {what}: " + Counts());
                }
            }
        // a board further down a chain of boards that lives on
        for (int kind = 0; kind <= 2; kind++)
        {
            Under(kind); Under(kind);
            ReplaceUnder();
            Console.WriteLine($"under, kind {kind}: " + Counts());
        }
        s_board.Next = null;
        Console.WriteLine("chain cut: " + Counts());
        Console.WriteLine(s_board.Cells == null ? "none" : "cells " + s_board.Cells.Length);
    }
}
