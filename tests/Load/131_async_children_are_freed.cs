// Objects an async method makes and attaches to a parent it was given, in a program where a task can be on a cycle
// (a continuation that captures what holds its task): they refer to the parent before they are in its list, held
// by the state machine meanwhile. Let go, the parent and the children are gone.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
class Node
{
    public static int Live;
    public Node Parent; public List<Node> Children = new(); public object Tag; public event Action<Node> Changed;
    public Node() { System.Threading.Interlocked.Increment(ref Live); }
    ~Node() { System.Threading.Interlocked.Decrement(ref Live); }
    public void Raise() => Changed?.Invoke(this);
}
static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pending() { var tcs = new TaskCompletionSource<Node>(); var waiter = new Node(); tcs.Task.ContinueWith(t => waiter.Raise()); waiter.Tag = tcs; }
    static async Task<Node> MakeAsync(Node parent) { await Task.Yield(); var n = new Node() { Parent = parent }; await Task.Delay(1); parent.Children.Add(n); return n; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void One() { var root = new Node(); MakeAsync(root).GetAwaiter().GetResult(); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Four() { var root = new Node(); Task.WhenAll(Enumerable.Range(0, 4).Select(_ => MakeAsync(root))).GetAwaiter().GetResult(); }
    static int Left() { for (int i = 0; i < 5; i++) { System.Threading.Thread.Sleep(20); GC.Collect(); GC.WaitForPendingFinalizers(); } return System.Threading.Volatile.Read(ref Node.Live); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        One(); Console.WriteLine(Left()); Four(); Console.WriteLine(Left()); Pending(); Console.WriteLine(Left());
    }
}
