// Subtrees cut from a tree that lives on, whose children their parent holds twice (in a list and a dictionary, in a
// list and an array): nothing outside refers to them, so they are gone. And the same with one child still held from
// outside: that child, its parent and everything above them up to the cut stay (they are reachable), the rest of
// the tree they hang in does too, as .NET keeps it; once that reference goes, so do they.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
class Node
{
    public static int Live;
    public Node Parent; public List<Node> Children = new(); public Dictionary<string, Node> ByName = new(); public Node[] Slots = new Node[4];
    public Node() { System.Threading.Interlocked.Increment(ref Live); }
    ~Node() { System.Threading.Interlocked.Decrement(ref Live); }
}
static class P
{
    static readonly Node s_root = new Node();
    static Node s_held;
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Cycle(int kind, bool hold)
    {
        var n = new Node { Parent = s_root }; s_root.Children.Add(n);
        for (int i = 0; i < 3; i++)
        {
            var c = new Node { Parent = n };
            n.Children.Add(c);
            if (kind == 1 || kind == 3) n.ByName["c" + i] = c;
            if (kind == 2 || kind == 3) n.Slots[i] = c;
            if (hold && i == 1) s_held = c;
        }
        s_root.Children.Remove(n);
    }
    static int Left() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return System.Threading.Volatile.Read(ref Node.Live); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int start = Left();
        for (int kind = 0; kind <= 3; kind++)
        {
            for (int i = 0; i < 100; i++) Cycle(kind, false);
            Console.WriteLine("kind " + kind + ": " + (Left() - start));
            Cycle(kind, true);
            Console.WriteLine("kind " + kind + " held: " + (Left() - start) + " " + (s_held.Parent.Children.Count + s_held.Parent.ByName.Count));
            s_held = null;
            Console.WriteLine("kind " + kind + " let go: " + (Left() - start));
        }
    }
}
