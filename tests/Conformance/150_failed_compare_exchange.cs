// Interlocked.CompareExchange of a reference into a slot of an object of a cycle-capable type, in a program with
// threads, where the exchange does not happen because the slot holds a sentinel from a static readonly field: nothing
// of the value offered is kept by the container. As Task.AddTaskContinuation does when the task completed meanwhile
// (its continuation slot holds the completion sentinel): the continuation kept the task as its owner after the task
// was freed, and the next store into the continuation read freed memory (072_http's rare out-of-bounds read).
using System;
using System.Runtime.InteropServices;
using System.Threading;

sealed class Node
{
    public static int Live;
    public object? Slot;
    public int Id;
    public Node(int id) { Id = id; Interlocked.Increment(ref Live); }
    ~Node() { Interlocked.Decrement(ref Live); }
}

static class Program
{
    static readonly Node s_done = new Node(-1);

    static int Round(int i)
    {
        var holder = new Node(i) { Slot = s_done };
        var offered = new Node(1000 + i);
        // (fails: the slot holds the sentinel, not null)
        int r = Interlocked.CompareExchange(ref holder.Slot, offered, null) == s_done ? 1 : 0;
        holder = null;
        // (the offered node lives on, and takes stores of its own)
        var next = new Node(2000 + i);
        offered.Slot = next;
        next.Slot = offered;
        return r + (offered.Slot == next ? 1 : 0);
    }

    [UnmanagedCallersOnly(EntryPoint = "main")]
    static void Main()
    {
        // (a thread: the program is one with threads, whose exchanges take the runtime's lock)
        int fromThread = 0;
        var t = new Thread(() => { for (int i = 0; i < 500; i++) fromThread += Round(i); });
        t.Start();
        t.Join();
        int sum = 0;
        for (int i = 0; i < 500; i++) sum += Round(i);
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        // (the sentinel stays: a static holds it)
        Console.WriteLine(fromThread + " " + sum + " " + Node.Live);
    }
}
