// Ported from tests/Conformance/083_list_reverse.cs (the old suite): the same program, for the cycle groups (docs/MEMORY-IR.md S2).
// live: 0
// A singly linked list reversed in place, and lists whose links are rewritten while variables walk them: each node
// is in turn the holder and the held, so which node owns which turns round at every store (section 4.3).
using System;
using System.Runtime.InteropServices;

sealed class ListNode { public ListNode? Next; public string Name = ""; public int Value; }

static class Program
{
    static ListNode? Build(int n)
    {
        ListNode? head = null;
        for (int i = 0; i < n; i++) head = new ListNode { Next = head, Name = "n" + (i % 7), Value = i };
        return head;
    }

    static long Sum(ListNode? head)
    {
        long sum = 0; int k = 0;
        for (var n = head; n != null; n = n.Next) { sum += (long)n.Value * (++k % 5 + 1); if (n.Name.Length != 2) sum = -1; }
        return sum;
    }

    static ListNode? Reverse(ListNode? head)
    {
        ListNode? prev = null;
        for (var n = head; n != null;) { var next = n.Next; n.Next = prev; prev = n; n = next; }
        return prev;
    }

    static void Main()
    {
        foreach (int size in new[] { 1, 2, 3, 10, 1000, 100000 })
        {
            var head = Build(size);
            long a = Sum(head);
            head = Reverse(head);
            long b = Sum(head);
            head = Reverse(Reverse(head));
            Console.WriteLine(size + " " + a + " " + b + " " + Sum(head));
        }
        // every second node taken out; then the rest made a ring and cut again
        var list = Build(1000);
        for (var n = list; n != null && n.Next != null; n = n.Next) n.Next = n.Next.Next;
        Console.WriteLine(Sum(list));
        var tail = list;
        while (tail!.Next != null) tail = tail.Next;
        tail.Next = list;
        long ring = 0; var p = list;
        for (int i = 0; i < 1500; i++) { ring += p!.Value; p = p.Next; }
        tail.Next = null;
        Console.WriteLine(ring + " " + Sum(list));
    }
}
