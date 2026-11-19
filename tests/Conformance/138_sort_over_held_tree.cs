// A sort that moves references to structures whose check at the drop failed (each owns a tree whose children are held
// twice, a list and a dictionary, and outside code holds one of its nodes): every move lets go of a reference to one.
// Such a loss must not go through the whole structure again each time, only when the look can succeed (the compiler
// compiling itself hung in Roslyn's sort of its declarations). Then the pins go, and everything with them.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class Node
{
    public readonly List<Node> Children = new();
    public readonly Dictionary<int, Node> ById = new();
    public int Id;
}

sealed class Holder
{
    public Node Tree = null!;
    public int Key;
}

static class Program
{
    static readonly List<Node> s_pins = new();

    static Node Build(int id, int depth, ref int count)
    {
        var n = new Node { Id = id };
        count++;
        if (depth > 0)
            for (int i = 0; i < 3; i++)
            {
                var c = Build(id * 3 + i + 1, depth - 1, ref count);
                n.Children.Add(c);
                n.ById[c.Id] = c;
            }
        return n;
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int count = 0;
        var items = new List<(Holder h, int key)>();
        for (int i = 0; i < 2000; i++)
        {
            var h = new Holder { Tree = Build(i, 4, ref count), Key = (int)((uint)(i * 2654435761u) % 100003) };
            // (outside code holds a node of each tree)
            s_pins.Add(h.Tree.Children[1].Children[2]);
            items.Add((h, h.Key));
        }
        long check = 0;
        for (int round = 0; round < 10; round++)
        {
            items.Sort((a, b) => round % 2 == 0 ? a.key.CompareTo(b.key) : b.key.CompareTo(a.key));
            for (int i = 0; i < items.Count; i += 97) check += items[i].h.Tree.Id * (long)(i + 1);
        }
        int pinned = s_pins.Count;
        items.Clear();
        s_pins.Clear();
        Console.WriteLine(count + " " + pinned + " " + check + " " + items.Count + " " + s_pins.Count);
    }
}
