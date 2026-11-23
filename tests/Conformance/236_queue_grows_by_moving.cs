// A queue that grows hands its items to the new array without counting them (Rt.MoveRotated: the array is full and
// starts anywhere, so the items are two stretches) when nothing else refers to the old array. What the queue holds
// and gives back after growing many times from every position of its head, with items that are counted (each has a
// finalizer: none is lost, none freed early, all freed when the queue goes), strings, and structs with references;
// a queue trimmed and given room (not full: copied); and the case in which something else still holds the old
// array (taken by reflection, as a debugger or a serializer would): .NET's old array keeps its items, and so must
// this one (with CS2WASM_ARRAY_MOVE_BREAK=1, the items moved whoever holds the old array, the "held" line differs).
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

sealed class Item
{
    public static int Live, Made;
    public readonly int Id;
    public Item(int id) { Id = id; Live++; Made++; }
    ~Item() { System.Threading.Interlocked.Decrement(ref Live); }
    public static string Left() { for (int i = 0; i < 4; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); } return "live " + Live + " of " + Made; }
    public override string ToString() => "#" + Id;
}
struct Pair { public Item? A; public int N; public string? S; }

static class Program
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Grow()
    {
        long sum = 0;
        // every position of the head at the moment the queue is full and grows
        for (int start = 0; start < 9; start++)
        {
            var q = new Queue<Item>(8);
            for (int i = 0; i < start; i++) q.Enqueue(new Item(-1));
            for (int i = 0; i < start; i++) q.Dequeue();
            for (int i = 0; i < 8; i++) q.Enqueue(new Item(start * 100 + i));
            for (int i = 0; i < 30; i++) q.Enqueue(new Item(start * 100 + 8 + i));
            int k = 0;
            while (q.Count > 0) { var it = q.Dequeue(); if (it.Id != start * 100 + k) sum += 1000000; sum += it.Id; k++; }
        }
        Console.WriteLine($"every head {sum}");
        var big = new Queue<Item>();
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < 100 + round * 7; i++) big.Enqueue(new Item(round * 1000 + i));
            for (int i = 0; i < 60; i++) sum += big.Dequeue().Id;
        }
        Console.WriteLine($"rounds {big.Count} {sum} {big.Peek()}");
        big.TrimExcess();
        big.EnsureCapacity(big.Count + 500);
        for (int i = 0; i < 700; i++) big.Enqueue(new Item(90000 + i));
        long rest = 0; foreach (var it in big) rest += it.Id;
        Console.WriteLine($"trimmed {big.Count} {rest}");
        var names = new Queue<string>(2);
        for (int i = 0; i < 300; i++) { names.Enqueue("n" + i); if (i % 3 == 0) sum += names.Dequeue().Length; }
        Console.WriteLine($"strings {names.Count} {names.Peek()} {sum}");
        var pairs = new Queue<Pair>(2);
        for (int i = 0; i < 200; i++) { pairs.Enqueue(new Pair { A = new Item(70000 + i), N = i, S = "p" + i }); if (i % 4 == 0) sum += pairs.Dequeue().A!.Id; }
        long ps = 0; foreach (var p in pairs) ps += p.A!.Id + p.N + p.S!.Length;
        Console.WriteLine($"structs {pairs.Count} {ps}");
        var numbers = new Queue<int>(2);
        for (int i = 0; i < 100; i++) numbers.Enqueue(i);
        long ns = 0; foreach (int n in numbers) ns += n;
        Console.WriteLine($"numbers {numbers.Count} {ns}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Held()
    {
        var q = new Queue<Item>(4);
        q.Enqueue(new Item(1)); q.Enqueue(new Item(2)); q.Dequeue();
        q.Enqueue(new Item(3)); q.Enqueue(new Item(4)); q.Enqueue(new Item(5));
        // (the queue's own array, full, its head at 1, held from outside while the queue grows)
        var field = typeof(Queue<Item>).GetField("_array", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var old = (Item[])field.GetValue(q)!;
        for (int i = 0; i < 20; i++) q.Enqueue(new Item(10 + i));
        string held = "";
        foreach (var it in old) held += (it == null ? "null" : it.ToString()) + " ";
        Console.WriteLine($"held {old.Length}: {held}queue {q.Count} {q.Peek()} same array {ReferenceEquals(old, field.GetValue(q))}");
    }

    [UnmanagedCallersOnly(EntryPoint = "main")] static void Main()
    {
        Grow(); Console.WriteLine("after grow: " + Item.Left());
        Held(); Console.WriteLine("after held: " + Item.Left());
    }
}
