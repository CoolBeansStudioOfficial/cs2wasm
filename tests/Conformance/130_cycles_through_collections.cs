// Cycles through a dictionary's entries, a list of tuples, an object field, arrays in an object field, a boxed
// struct, and an object stored into its own slots (with the last reference there was, too).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
class Node
{
    public static int Live;
    public Dictionary<string, Node> ByName = new(); public List<(Node n, int k)> Pairs = new(); public object Tag; public Node[] Slots = new Node[2];
    public List<Node> Kids = new();
    public Node() { System.Threading.Interlocked.Increment(ref Live); }
    ~Node() { System.Threading.Interlocked.Decrement(ref Live); }
}
struct Holder { public Node N; public List<Holder> More; }
static class P
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void A() { var a = new Node(); var b = new Node(); a.ByName["b"] = b; b.ByName["a"] = a; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void B() { var a = new Node(); var b = new Node(); a.Pairs.Add((b, 1)); b.Pairs.Add((a, 2)); }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void C() { var a = new Node(); a.Tag = a; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void D() { var a = new Node(); var b = new Node(); a.Tag = new object[] { b }; b.Tag = new[] { a }; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void E() { var a = new Node(); a.Tag = new Holder { N = a }; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void F() { var a = new Node(); a.Slots[0] = a; a.Kids.Add(a); }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void G() { var a = new Node(); a.ByName["me"] = a; }
    // (.NET: the collector has run, and its finalizers)
    static int Left() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return System.Threading.Volatile.Read(ref Node.Live); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        A(); Console.Write(Left() + " "); B(); Console.Write(Left() + " "); C(); Console.Write(Left() + " "); D(); Console.Write(Left() + " ");
        E(); Console.Write(Left() + " "); F(); Console.Write(Left() + " "); G(); Console.WriteLine(Left());
    }
}
