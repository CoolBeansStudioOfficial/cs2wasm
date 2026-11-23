// Test 131's four async methods that attach a child to one parent, in two hundred rounds, with and without the
// array of results kept by the parent and with and without a first yield: what test 131 shows once in some hundred
// loaded runs, the parent and its children left for good, showed here in 3 runs of 100. (A result adopted by its
// parent's list when its task died on a pool thread, while the state machine of the method that was ending still
// held the parent, whose check at the drop had failed by that result's reference: the check that the adoption asks
// for was not made, the parent being held, and the parent's note, stale, kept its last release from checking.)
// The children are added under a lock here; tests/Load/219_no_lock.cs is this test without it (List.Add from four
// threads at once, which .NET frees whole all the same: a round's five objects were left in one run of fifteen to
// one of five until Rt.TakeInAbove).
// The rounds wait, bounded, for the pool thread that completed the last method to be out of it (its state machine
// holds the parent until then, in .NET too: test 127's note), so only what is never freed counts.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
class Node
{
    public static int Live;
    public Node Parent; public List<Node> Children = new(); public object Tag; public event Action<Node> Changed;
    public Node() { Interlocked.Increment(ref Live); }
    ~Node() { Interlocked.Decrement(ref Live); }
    public void Raise() => Changed?.Invoke(this);
}
static class P
{
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pending() { var tcs = new TaskCompletionSource<Node>(); var waiter = new Node(); tcs.Task.ContinueWith(t => waiter.Raise()); waiter.Tag = tcs; }
    static async Task<Node> MakeAsync(Node parent, bool yield) { if (yield) await Task.Yield(); var n = new Node() { Parent = parent }; await Task.Delay(1); lock (parent.Children) parent.Children.Add(n); return n; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Four(int round) { var root = new Node(); var all = Task.WhenAll(Enumerable.Range(0, 4).Select(i => MakeAsync(root, (round + i) % 3 != 0))).GetAwaiter().GetResult(); if (round % 2 == 0) root.Tag = all; }
    static int Left() { var sw = System.Diagnostics.Stopwatch.StartNew(); do { GC.Collect(); GC.WaitForPendingFinalizers(); if (Volatile.Read(ref Node.Live) == 0) break; Thread.Sleep(1); } while (sw.ElapsedMilliseconds < 2000); return Volatile.Read(ref Node.Live); }
    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        int left = 0;
        for (int round = 0; round < 200; round++)
        {
            Four(round);
            if (round % 5 == 0) Pending();
            if (round % 10 == 9 && Left() != 0) left++;
        }
        Console.WriteLine(left + " " + Left());
    }
}
