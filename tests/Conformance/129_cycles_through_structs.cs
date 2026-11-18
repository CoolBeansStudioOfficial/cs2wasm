// Cycles through references inside structs that are fields of a class: a tuple, a key and value pair, a struct of
// the program's, written whole, through a member, through the struct's own method. Nothing is left afterwards.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
class Node
{
    public static int Live;
    public (Node other, int n) Pair; public Node? Maybe; public KeyValuePair<string, Node> Kv; public Holder H;
    public Node() { System.Threading.Interlocked.Increment(ref Live); }
    ~Node() { System.Threading.Interlocked.Decrement(ref Live); }
}
struct Holder { public Node N; public int X; public void Set(Node n) { N = n; } }
static class P
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void A() { var a = new Node(); var b = new Node(); a.Pair = (b, 1); b.Pair = (a, 2); }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void B() { var a = new Node(); var b = new Node(); a.Pair.other = b; b.Pair.other = a; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void C() { var a = new Node(); var b = new Node(); a.Kv = new KeyValuePair<string, Node>("k", b); b.Kv = new("j", a); }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void D() { var a = new Node(); var b = new Node(); a.H.Set(b); b.H = new Holder { N = a }; var h = a.H; b.H = h; b.H.N = a; }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)] static void E() { var a = new Node(); a.Pair = (a, 0); a.H.N = a; }
    // (.NET: the collector has run, and its finalizers)
    static int Left() { for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return System.Threading.Volatile.Read(ref Node.Live); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        A(); Console.WriteLine(Left()); B(); Console.WriteLine(Left()); C(); Console.WriteLine(Left()); D(); Console.WriteLine(Left()); E(); Console.WriteLine(Left());
    }
}
